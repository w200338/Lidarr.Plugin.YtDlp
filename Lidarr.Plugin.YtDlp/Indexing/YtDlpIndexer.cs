using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
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
///  - <c>Size</c> = 0 (unknown) → the host skips its size specifications.
///  - <c>PublishDate</c> = DateTime.UtcNow (search time; yt-dlp's flat search
///    gives no release date, and DateTime.MinValue rendered as an absurd age).
///
/// Search failures never throw to the host — they return an empty list.
/// </summary>
public class YtDlpIndexer : IndexerBase<YtDlpIndexerSettings>
{
    /// <summary>Bounded timeout for the settings Test() connection check.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Bounded timeout per entry inspection (flat title resolve).</summary>
    private static readonly TimeSpan InspectTimeout = TimeSpan.FromSeconds(30);

    private readonly YtDlpCommandBuilder _builder = new();
    private readonly SemaphoreSlim _searchGate = new(1, 1);

    public YtDlpIndexer(
        IIndexerStatusService indexerStatusService,
        IConfigService configService,
        IParsingService parsingService,
        Logger logger)
        : base(indexerStatusService, configService, parsingService, logger)
    {
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

        return FetchAlbumReleasesAsync(query, searchCriteria.Artist.Name, searchCriteria.AlbumTitle);
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
            var result = await YtDlpConnectionTest.RunAsync(Settings, cts.Token).ConfigureAwait(false);
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
    private async Task<IList<ReleaseInfo>> FetchAlbumReleasesAsync(string query, string artist, string? albumHint)
    {
        IReadOnlyList<SearchEntry> entries = [];
        var resolutions = new Dictionary<string, EntryResolution>();

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

            foreach (var entry in entries)
            {
                var resolved = await ResolveEntryAsync(entry.Url).ConfigureAwait(false);
                if (resolved != null)
                {
                    resolutions[entry.Id] = resolved;
                }
            }
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

        var releases = entries
            .Select(entry => MapAlbumRelease(entry, artist, albumHint, query,
                resolutions.TryGetValue(entry.Id, out var resolved) ? resolved : null))
            .Where(r => r != null)
            .ToList();

        return CleanupReleases(releases);
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
    /// (the music search page's flat entries carry no per-entry titles).
    /// Returns null on any failure — the caller falls back to the
    /// search-criteria-derived title.
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

            foreach (var line in result.StandardOutput.Split('\n'))
            {
                if (SearchLineParser.TryParseInspectLine(line, out var resolution) && resolution != null)
                {
                    return resolution;
                }
            }

            return null;
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
    /// accepts. Internal + static for unit tests.
    /// </summary>
    internal static ReleaseInfo? MapAlbumRelease(
        SearchEntry entry,
        string artist,
        string? albumHint,
        string query,
        EntryResolution? resolved)
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

        return new ReleaseInfo
        {
            Guid = entry.Id,
            Title = $"{artist} - {album}",
            Artist = artist,
            Album = album,
            DownloadUrl = downloadUrl,
            InfoUrl = entry.Url,
            PublishDate = DateTime.UtcNow,
            Size = 0
        };
    }

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
