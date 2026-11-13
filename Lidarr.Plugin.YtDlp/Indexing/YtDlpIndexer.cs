using System.Collections.Concurrent;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;
using Lidarr.Plugin.YtDlp.Commands;
using Lidarr.Plugin.YtDlp.Download;
using Lidarr.Plugin.YtDlp.Probing;
using Lidarr.Plugin.YtDlp.Runner;
using Lidarr.Plugin.YtDlp.Settings;

namespace Lidarr.Plugin.YtDlp.Indexing;

/// <summary>
/// Native host <see cref="IIndexer"/> over yt-dlp (process-driven, NOT an HTTP
/// API indexer).
///
/// Search:
///  - Album search  → YouTube Music album search ("&lt;artist&gt; &lt;album&gt;").
///    The music search page's flat entries carry only {id, url} - no titles.
///    Each top-N entry is therefore re-fetched flat once (one fast page
///    fetch per entry) to read its real album title, which becomes the
///    release title so the decision engine only accepts the actually-searched
///    album (other albums in the ranked list get rejected on parse, which is
///    the correct signal).
///  - Artist search → generic YouTube search ("&lt;artist&gt;"). Entries are
///    track-level (real titles); most won't parse to a catalog album, which
///    is expected - album search is the primary workflow.
///  - Recent/RSS    → empty (YouTube has no music release feed).
///
/// Every result is shaped for the host decision engine:
///  - <c>Title</c> is composed as "<c>Artist - Album</c>" (artist from the
///    search criteria for album search, channel for artist search) so the
///    host parser's search-criteria fallback (ParseAlbumTitleWithSearchCriteria:
///    ^artist...album$) matches it - the primary ParseAlbumTitle path needs a
///    year/source suffix that music releases don't have.
///  - <c>Guid</c> is the stable video/playlist id (dedupe + grab identity).
///  - <c>DownloadUrl</c> is the real YouTube watch/playlist URL (for album
///    search: the music browse URL, which the download client handles via
///    <see cref="YtDlpAlbumId.TryFromUrl"/>).
///  - <c>PublishDate</c>: Dec 31 of the search-criteria album year (the host
///    derives the display "Age" from it; Dec 31 keeps the release on the
///    "old" side of EarlyReleaseSpecification's release-date check). Falls
///    back to DateTime.UtcNow when the criteria carry no year.
///  - <c>Size</c>: estimated from the summed flat-entry track durations when
///    the inspection saw them (× a conservative bytes/sec for the enabled
///    client's delivery mode), 0 (unknown) otherwise — the host skips its
///    size specifications for Size = 0.
///  - Quality: the host parses it from the release TITLE (not from the
///    ReleaseInfo), so for re-encode delivery modes the title carries a
///    host-parseable quality token ("MP3 VBR V0" / "AAC VBR" / "VORBIS Q10")
///    instead of resolving to Unknown (which profiles disallow).
///
/// Search failures never throw to the host — they return an empty list.
/// </summary>
public class YtDlpIndexer : IndexerBase<YtDlpIndexerSettings>
{
    /// <summary>Bounded timeout for the settings Test() connection check.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Bounded timeout per entry inspection (flat title resolve). Generous
    /// enough for slow networks; --socket-timeout 20 in the inspect args
    /// makes hung sockets fail earlier than this backstop.
    /// </summary>
    private static readonly TimeSpan InspectTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How many entry inspections may run at once. Flat metadata fetches are
    /// light (no media), so a little overlap keeps search snappy; downloads
    /// remain strictly serialized by the download client's scheduler.
    /// </summary>
    private const int InspectConcurrency = 3;

    private readonly YtDlpCommandBuilder _builder = new();
    private readonly SemaphoreSlim _searchGate = new(1, 1);
    private readonly IProviderRepository<DownloadClientDefinition>? _clientDefinitions;

    public YtDlpIndexer(
        IIndexerStatusService indexerStatusService,
        IConfigService configService,
        IParsingService parsingService,
        Logger logger,
        IProviderRepository<DownloadClientDefinition>? clientDefinitions = null)
        : base(indexerStatusService, configService, parsingService, logger)
    {
        _clientDefinitions = clientDefinitions;
    }

    public override string Name => "YouTube (yt-dlp)";

    public override string Protocol => nameof(YtDlpDownloadProtocol);

    public override bool SupportsRss => false;

    public override bool SupportsSearch => true;

    /// <summary>
    /// "Add indexer" template. Name = null makes ProviderFactory.GetDefaultDefinitions
    /// pick this definition (it matches the v.Name == null branch) instead of the
    /// base-class one, which would compute the search flags from Validate() on
    /// empty settings (no yt-dlp path yet) and leave interactive search off.
    /// </summary>
    public override IEnumerable<ProviderDefinition> DefaultDefinitions
    {
        get
        {
            yield return new IndexerDefinition
            {
                Name = null,
                EnableRss = false,
                EnableAutomaticSearch = false,
                EnableInteractiveSearch = true,
                Implementation = GetType().Name,
                Settings = new YtDlpIndexerSettings(),
                Protocol = Protocol,
                SupportsRss = SupportsRss,
                SupportsSearch = SupportsSearch
            };
        }
    }

    public override Task<IList<ReleaseInfo>> FetchRecent()
    {
        // YouTube has no useful "recent releases" feed for a music library.
        return Task.FromResult<IList<ReleaseInfo>>(new ReleaseInfo[0]);
    }

    public override Task<IList<ReleaseInfo>> Fetch(AlbumSearchCriteria searchCriteria)
    {
        var query = NormalizeQuery(searchCriteria.ArtistQuery, searchCriteria.AlbumTitle, searchCriteria.Disambiguation);
        if (query.Length == 0)
        {
            return Task.FromResult<IList<ReleaseInfo>>(new ReleaseInfo[0]);
        }

        return FetchAlbumReleasesAsync(query, searchCriteria.Artist.Name, searchCriteria.AlbumTitle, searchCriteria.AlbumYear, searchCriteria);
    }

    public override Task<IList<ReleaseInfo>> Fetch(ArtistSearchCriteria searchCriteria)
    {
        var query = NormalizeQuery(searchCriteria.ArtistQuery);
        if (query.Length == 0)
        {
            return Task.FromResult<IList<ReleaseInfo>>(new ReleaseInfo[0]);
        }

        return FetchReleasesAsync(_builder.BuildSearchArgs(query, Limit), searchCriteria.Artist.Name);
    }

    public override HttpRequest GetDownloadRequest(string link) => new(link);

    protected override async Task Test(List<ValidationFailure> failures)
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        try
        {
            // sponsorBlockEndpoint: null — the indexer never downloads, so
            // SponsorBlock is not its concern (v0.2.4 settings split).
            var result = await YtDlpConnectionTest.RunAsync(Settings, null, cts.Token).ConfigureAwait(false);
            foreach (var error in result.Errors)
            {
                failures.Add(new ValidationFailure(error.Property, error.Message));
            }

            foreach (var warning in result.Warnings)
            {
                _logger.Warn("[{0}] {1}", Name, warning);
            }
        }
        catch (OperationCanceledException)
        {
            failures.Add(new ValidationFailure("YtDlpPath", "Connection test timed out."));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[{0}] Connection test failed", Name);
            failures.Add(new ValidationFailure(string.Empty, "Connection test failed: " + ex.Message));
        }
    }

    private int Limit => Math.Clamp(Settings.SearchLimit, 1, 100);

    /// <summary>
    /// Album search: flat search (ids/urls only), then one flat re-fetch per
    /// entry for its real album title. Everything runs under the search gate
    /// so no two yt-dlp commands overlap. Search/resolve failures never throw
    /// to the host — they yield an empty list / a fallback title.
    /// </summary>
    private async Task<IList<ReleaseInfo>> FetchAlbumReleasesAsync(string query, string artist, string? albumHint, int albumYear, SearchCriteriaBase criteria)
    {
        IReadOnlyList<SearchEntry> entries = [];
        var resolutions = new ConcurrentDictionary<string, EntryResolution>();
        var delivery = ResolveDeliveryMode();
        var catalogHasDurations = CatalogHasReleaseDurations(criteria);

        await _searchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var searchResult = await YtDlpRunners.For(Settings.YtDlpPath)
                .RunBufferedAsync(_builder.BuildAlbumSearchArgs(query, Limit), CancellationToken.None)
                .ConfigureAwait(false);

            if (!searchResult.IsSuccess)
            {
                _logger.Warn("[{0}] yt-dlp album search failed (exit {1}): {2}", Name, searchResult.ExitCode, Truncate(searchResult.StandardError, 300));
                return new ReleaseInfo[0];
            }

            entries = SearchLineParser.ParseAll(searchResult.StandardOutput)
                .Take(Limit)
                .Where(e => !string.IsNullOrEmpty(e.Id) && !string.IsNullOrWhiteSpace(e.Url))
                .ToList();

            // Resolve titles for all entries with bounded overlap: a slow or
            // throttled single entry no longer stalls the whole search, and
            // one hung entry costs at most InspectTimeout instead of
            // InspectTimeout x N (the old serial loop).
            using var inspectGate = new SemaphoreSlim(InspectConcurrency, InspectConcurrency);
            await Task.WhenAll(entries.Select(async entry =>
            {
                await inspectGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var resolved = await ResolveEntryAsync(entry.Url).ConfigureAwait(false);
                    if (resolved != null)
                    {
                        resolutions[entry.Id] = resolved;
                    }
                }
                finally
                {
                    inspectGate.Release();
                }
            })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn("[{0}] Album search failed: {1}", Name, ex.Message);
            return new ReleaseInfo[0];
        }
        finally
        {
            _searchGate.Release();
        }

        var utcNow = DateTime.UtcNow;
        var releases = entries
            .Select(entry => MapAlbumRelease(entry, artist, albumHint, query,
                resolutions.TryGetValue(entry.Id, out var resolved) ? resolved : (EntryResolution?)null,
                albumYear, delivery, utcNow, catalogHasDurations))
            .Where(r => r != null)
            .ToList();

        return CleanupReleases(releases);
    }

    /// <summary>
    /// The re-encode mode of the first enabled yt-dlp download client — it
    /// determines what the release title's quality token must say and how
    /// big a download will roughly be. Resolved at search time from the host's
    /// provider repository (DI-injected); falls back to Disabled (no token,
    /// generic estimate) when nothing usable is found or the lookup fails.
    /// </summary>
    private ReencodeFormat ResolveDeliveryMode()
    {
        if (_clientDefinitions is null)
        {
            return ReencodeFormat.Disabled;
        }

        try
        {
            foreach (var definition in _clientDefinitions.All())
            {
                if (!definition.Enable || definition.Implementation != nameof(YtDlpDownloadClient))
                {
                    continue;
                }

                if (definition.Settings is YtDlpClientSettings settings)
                {
                    return settings.Reencode;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[{0}] Could not read download client settings; assuming no re-encode", Name);
        }

        return ReencodeFormat.Disabled;
    }

    /// <summary>
    /// Runs one serialized yt-dlp search (artist search: track entries with
    /// real titles); failures → empty list (search never throws).
    /// </summary>
    private async Task<IList<ReleaseInfo>> FetchReleasesAsync(IReadOnlyList<string> args, string? fallbackArtist)
    {
        YtDlpRunResult result;
        await _searchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            result = await YtDlpRunners.For(Settings.YtDlpPath)
                .RunBufferedAsync(args, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _searchGate.Release();
        }

        if (!result.IsSuccess)
        {
            _logger.Warn("[{0}] yt-dlp search failed (exit {1}): {2}", Name, result.ExitCode, Truncate(result.StandardError, 300));
            return new ReleaseInfo[0];
        }

        var releases = SearchLineParser.ParseAll(result.StandardOutput)
            .Take(Limit)
            .Select(entry => MapRelease(entry, fallbackArtist))
            .Where(r => r != null)
            .ToList();

        return CleanupReleases(releases);
    }

    /// <summary>
    /// Flat re-fetch of one entry URL to read its real playlist/album title
    /// (the music search page's flat entries carry no per-entry titles) and,
    /// from the per-entry durations, the album's total duration (size
    /// estimate). The flat page echoes the shared playlist fields on every
    /// line, so the first non-empty value wins and durations are summed.
    /// Returns null when no line carries a title — the caller falls back to
    /// the search-criteria-derived title (and Size = 0).
    /// </summary>
    private async Task<EntryResolution?> ResolveEntryAsync(string entryUrl)
    {
        try
        {
            using var cts = new CancellationTokenSource(InspectTimeout);
            var result = await YtDlpRunners.For(Settings.YtDlpPath)
                .RunBufferedAsync(_builder.BuildEntryInspectArgs(entryUrl), cts.Token)
                .ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                return null;
            }

            string? title = null;
            string? channel = null;
            string? playlistId = null;
            var totalDuration = 0.0;

            foreach (var line in result.StandardOutput.Split('\n'))
            {
                if (!SearchLineParser.TryParseInspectLine(line, out var inspectLine))
                {
                    continue;
                }

                title ??= inspectLine.Title;
                channel ??= inspectLine.Channel;
                playlistId ??= inspectLine.PlaylistId;
                if (inspectLine.DurationSeconds is > 0)
                {
                    totalDuration += inspectLine.DurationSeconds!.Value;
                }
            }

            return title is null ? null : new EntryResolution(title, channel, playlistId, totalDuration);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("[{0}] Entry inspection timed out for {1}", Name, entryUrl);
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warn("[{0}] Entry inspection failed for {1}: {2}", Name, entryUrl, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Maps one yt-dlp search entry to a host <see cref="ReleaseInfo"/> (artist
    /// search path: entries have real titles/channels).
    /// Internal + static so the mapping (the decision-engine contract) is unit-testable.
    /// </summary>
    internal static ReleaseInfo? MapRelease(SearchEntry entry, string? fallbackArtist = null)
    {
        if (string.IsNullOrEmpty(entry.Id) || string.IsNullOrWhiteSpace(entry.Url))
        {
            return null;
        }

        if (!entry.Url.StartsWith("https://", StringComparison.Ordinal))
        {
            // No usable form for this entry.
            return null;
        }

        var artist = string.IsNullOrWhiteSpace(entry.Channel)
            ? (fallbackArtist ?? "Unknown Artist")
            : entry.Channel;

        return new ReleaseInfo
        {
            Guid = entry.Id,
            Title = $"{artist} - {entry.Title}",
            Artist = artist,
            Album = entry.Title,
            DownloadUrl = entry.Url,
            InfoUrl = entry.Url,
            PublishDate = DateTime.UtcNow,
            Size = 0
        };
    }

    /// <summary>
    /// Maps one ALBUM search entry to a host <see cref="ReleaseInfo"/>.
    /// The flat entry has no title, so the title is composed from the search
    /// criteria (guaranteed parseable by the host's criteria-fallback parser)
    /// and upgraded to the entry's real album title when the flat re-fetch
    /// resolved it. The download URL prefers the entry's canonical playlist
    /// URL (when the inspection resolved its playlist id) and falls back to
    /// the entry's browse URL, which yt-dlp (and the download client) also
    /// accepts.
    ///
    /// PublishDate, Size and the title's quality token are the decision-
    /// engine contract (see the class docs): album-year-based age, duration
    /// estimate, re-encode-aware quality token. Internal + static for unit
    /// tests.
    /// </summary>
    internal static ReleaseInfo? MapAlbumRelease(
        SearchEntry entry,
        string artist,
        string? albumHint,
        string query,
        EntryResolution? resolved,
        int albumYear,
        ReencodeFormat delivery,
        DateTime utcNow,
        bool catalogHasDurations)
    {
        if (string.IsNullOrEmpty(entry.Id) || string.IsNullOrWhiteSpace(entry.Url))
        {
            return null;
        }

        if (!entry.Url.StartsWith("https://", StringComparison.Ordinal))
        {
            return null;
        }

        var album = resolved is not null && !string.IsNullOrWhiteSpace(resolved.Title)
            ? resolved.Title
            : (string.IsNullOrWhiteSpace(albumHint) ? query : albumHint!);

        var downloadUrl = resolved is not null && !string.IsNullOrWhiteSpace(resolved.PlaylistId)
            ? YtDlpAlbumId.PlaylistUrlBase + resolved.PlaylistId
            : entry.Url;

        var title = $"{artist} - {album}";
        var qualitySuffix = QualityTitleSuffix(delivery);
        if (qualitySuffix is not null)
        {
            title = $"{title} ({qualitySuffix})";
        }

        return new ReleaseInfo
        {
            Guid = entry.Id,
            Title = title,
            Artist = artist,
            Album = album,
            DownloadUrl = downloadUrl,
            InfoUrl = entry.Url,
            PublishDate = PublishDateForYear(albumYear, utcNow),
            Size = catalogHasDurations ? EstimateSizeBytes(resolved, delivery) : 0
        };
    }

    /// <summary>
    /// Dec 31 of the album year (the host derives the displayed "Age" from
    /// PublishDate; Dec 31 makes a same-year release count as old, which
    /// keeps EarlyReleaseSpecification from rejecting it). Falls back to the
    /// search time for missing/implausible years (0, future).
    /// </summary>
    internal static DateTime PublishDateForYear(int albumYear, DateTime utcNow)
    {
        if (albumYear >= 1900 && albumYear <= utcNow.Year)
        {
            return new DateTime(albumYear, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        }

        return utcNow;
    }

    /// <summary>
    /// Host-parseable quality token for the delivery mode, or null (title
    /// stays plain → host parses Unknown, as before) when there is no
    /// accurate host quality for what yt-dlp will deliver:
    ///  - MP3 re-encode → MP3 VBR V0 (libmp3lame -q:a 0 via --audio-quality 0)
    ///  - AAC re-encode → AAC VBR (aac -q:a 0.1)
    ///  - Vorbis re-encode → VORBIS Q10 (libvorbis -q:a 10)
    ///  - Opus / Disabled → null: the host Quality enum has no Opus entry and
    ///    "disabled" delivers the (unknown) source stream.
    /// </summary>
    internal static string? QualityTitleSuffix(ReencodeFormat delivery) => delivery switch
    {
        ReencodeFormat.Mp3 => "MP3 VBR V0",
        ReencodeFormat.Aac => "AAC VBR",
        ReencodeFormat.Vorbis => "VORBIS Q10",
        _ => null
    };

    /// <summary>
    /// Conservative bytes/second for the delivery mode (worst case the host
    /// may compare against its min/max size specs, so over- rather than
    /// under-estimate):
    ///  - MP3 V0 ≈ up to ~245 kbps → 30,000 B/s
    ///  - AAC VBR ≈ up to ~192 kbps → 28,000 B/s
    ///  - Vorbis Q10 ≈ up to ~256 kbps → 32,000 B/s
    ///  - source stream (e.g. Opus ≈ 160-200 kbps) → 32,000 B/s
    /// </summary>
    internal static double EstimatedBytesPerSecond(ReencodeFormat delivery) => delivery switch
    {
        ReencodeFormat.Mp3 => 30_000,
        ReencodeFormat.Aac => 28_000,
        _ => 32_000
    };

    /// <summary>Duration-based size estimate; 0 (unknown) when the inspection saw no durations.</summary>
    internal static long EstimateSizeBytes(EntryResolution? resolved, ReencodeFormat delivery) =>
        resolved is not null && resolved.TotalDurationSeconds > 0
            ? (long)(resolved.TotalDurationSeconds * EstimatedBytesPerSecond(delivery))
            : 0;

    /// <summary>
    /// Whether the host catalog has release durations for the searched
    /// album(s) — mirroring AcceptableSizeSpecification's max-duration math.
    /// Without catalog durations the host rejects ANY release with Size > 0
    /// ("Album duration is 0"), so the plugin reports unknown size (0) in
    /// that case and lets the host skip the size check, exactly as before.
    /// </summary>
    internal static bool CatalogHasReleaseDurations(SearchCriteriaBase? criteria) =>
        criteria?.Albums is { Count: > 0 } albums &&
        albums
            .Select(a => a.AlbumReleases.Value
                .Where(r => r.Monitored || a.AnyReleaseOk)
                .Select(r => r.Duration)
                .MaxOrDefault())
            .Sum() > 0;

    internal static string NormalizeQuery(params string?[] parts)
    {
        var joined = string.Join(" ", parts
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim()));
        return System.Text.RegularExpressions.Regex.Replace(joined, @"\s+", " ").Trim();
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[^max..];
}
