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
///  - Artist search → generic YouTube search ("&lt;artist&gt;").
///  - Recent/RSS    → empty (YouTube has no music release feed).
///
/// Every result is shaped for the host decision engine:
///  - <c>Title</c> is composed as "<c>Channel - Title</c>" so the host parser
///    (ParseAlbumTitleWithSearchCriteria: ^artist...album$) can match it.
///  - <c>Guid</c> is the stable video/playlist id (dedupe + grab identity).
///  - <c>DownloadUrl</c> is the real YouTube watch/playlist URL; the download
///    client parses it back to a yt-dlp target via <see cref="YtDlpAlbumId.TryFromUrl"/>.
///  - <c>Size</c> = 0 (unknown) → the host skips its size specifications.
///  - <c>PublishDate</c> = DateTime.MinValue (unknown; no spec in the search
///    decision path rejects an old publish date).
///
/// Search failures never throw to the host — they return an empty list.
/// </summary>
public class YtDlpIndexer : IndexerBase<YtDlpIndexerSettings>
{
    /// <summary>Bounded timeout for the settings Test() connection check.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(90);

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

        return FetchReleasesAsync(_builder.BuildAlbumSearchArgs(query, Limit), searchCriteria.Artist.Name);
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

    /// <summary>Runs one serialized yt-dlp search; failures → empty list (search never throws).</summary>
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
    /// Maps one yt-dlp search entry to a host <see cref="ReleaseInfo"/>.
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
            PublishDate = DateTime.MinValue,
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
