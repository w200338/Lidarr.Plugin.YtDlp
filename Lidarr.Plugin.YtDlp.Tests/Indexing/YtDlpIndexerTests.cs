using System.Text;
using FluentValidation.Results;
using Lidarr.Plugin.YtDlp.Indexing;
using Lidarr.Plugin.YtDlp.Probing;
using Lidarr.Plugin.YtDlp.Runner;
using Lidarr.Plugin.YtDlp.Settings;
using Lidarr.Plugin.YtDlp.Tests.Support;
using NLog;
using NSubstitute;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;

namespace Lidarr.Plugin.YtDlp.Tests.Indexing;

/// <summary>
/// Phase 7 (native model): YtDlpIndexer now derives from the host
/// IndexerBase&lt;YtDlpIndexerSettings&gt;. The decision-engine contract (the
/// ReleaseInfo shape) is the part that must stay pinned, plus the host-facing
/// plumbing (DefaultDefinitions, Test, GetDownloadRequest).
/// </summary>
public class YtDlpIndexerTests
{
    private YtDlpIndexer _indexer = null!;
    private FakeYtDlpRunner _fake = null!;
    private Func<string, IYtDlpRunner>? _previousFactory;

    [SetUp]
    public void SetUp()
    {
        _fake = new FakeYtDlpRunner();
        _previousFactory = YtDlpRunners.Factory;
        YtDlpRunners.Factory = _ => _fake;

        var settings = new YtDlpIndexerSettings
        {
            YtDlpPath = "yt-dlp",
            SearchLimit = 25
        };

        _indexer = new YtDlpIndexer(
            Substitute.For<IIndexerStatusService>(),
            Substitute.For<IConfigService>(),
            Substitute.For<IParsingService>(),
            LogManager.GetLogger("test"));

        // CleanupReleases stamps Guid/IndexerId/DownloadProtocol from the definition.
        _indexer.Definition = new IndexerDefinition
        {
            Id = 7,
            Name = "TestIndexer",
            Priority = 25,
            Settings = settings
        };
    }

    [TearDown]
    public void TearDown()
    {
        YtDlpRunners.Factory = _previousFactory!;
        YtDlpRunners.ClearCache();
        YtDlpConnectionTest.Override = null;
    }

    // ----- host-facing plumbing -----

    [Test]
    public void Host_surface_matches_native_contract()
    {
        Assert.Multiple(() =>
        {
            Assert.That(_indexer, Is.InstanceOf<IndexerBase<YtDlpIndexerSettings>>());
            Assert.That(_indexer.Name, Is.EqualTo("YouTube (yt-dlp)"));
            Assert.That(_indexer.Protocol, Is.EqualTo(nameof(NzbDrone.Core.Indexers.YtDlpDownloadProtocol)));
            Assert.That(_indexer.SupportsRss, Is.False);
            Assert.That(_indexer.SupportsSearch, Is.True);
            Assert.That(_indexer.ConfigContract, Is.EqualTo(typeof(YtDlpIndexerSettings)));
        });
    }

    [Test]
    public void DefaultDefinitions_enables_interactive_search()
    {
        var definitions = _indexer.DefaultDefinitions.OfType<IndexerDefinition>().ToList();

        Assert.That(definitions, Has.Count.EqualTo(1));

        var d = definitions[0];
        Assert.Multiple(() =>
        {
            Assert.That(d.Name, Is.Null, "Name=null → ProviderFactory picks this as the add-template");
            Assert.That(d.Implementation, Is.EqualTo(nameof(YtDlpIndexer)), "factory matches implementation by type name");
            Assert.That(d.EnableRss, Is.False);
            Assert.That(d.EnableAutomaticSearch, Is.False);
            Assert.That(d.EnableInteractiveSearch, Is.True);
            Assert.That(d.Settings, Is.InstanceOf<YtDlpIndexerSettings>());
        });
    }

    [Test]
    public async Task FetchRecent_returns_empty()
    {
        Assert.That(await _indexer.FetchRecent(), Is.Empty);
    }

    [Test]
    public void GetDownloadRequest_wraps_link_in_get_request()
    {
        var link = "https://www.youtube.com/watch?v=abc123";
        var request = _indexer.GetDownloadRequest(link);

        Assert.Multiple(() =>
        {
            Assert.That(request.Url.ToString(), Is.EqualTo(link));
            Assert.That(request.Method, Is.EqualTo(System.Net.Http.HttpMethod.Get));
        });
    }

    [Test]
    public void Test_maps_connection_errors_to_validation_failures()
    {
        YtDlpConnectionTest.Override = (s, ct) => Task.FromResult(new ConnectionTestResult(
            new[]
            {
                new ConnectionTestError("YtDlpPath", "yt-dlp not found at 'yt-dlp'.")
            },
            new[] { "unexpected yt-dlp output" }));

        var result = _indexer.Test();

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors.Any(e => e.PropertyName == "YtDlpPath"));
            Assert.That(result.Errors.Any(e => e.ErrorMessage == "yt-dlp not found at 'yt-dlp'."));
        });
    }

    [Test]
    public void Test_passes_when_connection_test_succeeds()
    {
        YtDlpConnectionTest.Override = (s, ct) => Task.FromResult(new ConnectionTestResult(
            Array.Empty<ConnectionTestError>(),
            Array.Empty<string>()));

        Assert.That(_indexer.Test().IsValid, Is.True);
    }

    // ----- search (host IIndexer contract) -----

    /// <summary>JSON line builder for canned search output (mirrors the yt-dlp print template).</summary>
    private static string Line(string id, string title, string channel, double? dur, string type, string? url = null)
    {
        url ??= $"https://www.youtube.com/watch?v={id}";
        var sb = new StringBuilder("{\"id\":\"").Append(id).Append("\",\"url\":\"").Append(url)
            .Append("\",\"title\":\"").Append(title).Append("\",\"channel\":\"").Append(channel).Append('"');
        sb.Append(dur.HasValue ? $",\"duration\":{dur.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : ",\"duration\":null");
        sb.Append(",\"type\":\"").Append(type).Append("\"}");
        return sb.ToString();
    }

    [Test]
    public async Task AlbumFetch_composes_query_and_maps_releases()
    {
        _fake.BufferBehavior = (args, ct) =>
        {
            // Last arg is the music.youtube.com search URL.
            var url = args.Last();
            Assert.That(url, Does.Contain("music.youtube.com/search"));

            return Task.FromResult(new YtDlpRunResult(0, string.Join('\n',
            [
                Line("abc123", "Song One", "Artist A", 213.5, "video")
            ]), string.Empty));
        };

        var releases = await _indexer.Fetch(new AlbumSearchCriteria
        {
            Artist = new Artist { Name = "Artist A" },
            AlbumTitle = "Great Album",
            Disambiguation = string.Empty
        });

        Assert.That(releases, Has.Count.EqualTo(1));

        var r = releases[0];
        Assert.Multiple(() =>
        {
            Assert.That(r.Title, Is.EqualTo("Artist A - Song One"),
                "host parser needs 'Artist - Album' shape (ParseAlbumTitleWithSearchCriteria)");
            Assert.That(r.Artist, Is.EqualTo("Artist A"));
            Assert.That(r.Album, Is.EqualTo("Song One"));
            Assert.That(r.Guid, Is.EqualTo("7_abc123"), "CleanupReleases prefixes Definition.Id");
            Assert.That(r.Indexer, Is.EqualTo("TestIndexer"));
            Assert.That(r.IndexerPriority, Is.EqualTo(25));
            Assert.That(r.DownloadUrl, Is.EqualTo("https://www.youtube.com/watch?v=abc123"));
            Assert.That(r.InfoUrl, Is.EqualTo("https://www.youtube.com/watch?v=abc123"));
            Assert.That(r.Size, Is.EqualTo(0), "unknown size → host skips size specifications");
            Assert.That(r.PublishDate, Is.EqualTo(DateTime.MinValue));
            Assert.That(r.DownloadProtocol, Is.EqualTo(nameof(NzbDrone.Core.Indexers.YtDlpDownloadProtocol)),
                "CleanupReleases stamps the protocol from the indexer");
            Assert.That(r.IndexerId, Is.EqualTo(7));
        });
    }

    [Test]
    public async Task AlbumFetch_includes_disambiguation_in_query()
    {
        string? seenUrl = null;
        _fake.BufferBehavior = (args, ct) =>
        {
            seenUrl = args.Last();
            return Task.FromResult(new YtDlpRunResult(0, string.Empty, string.Empty));
        };

        _ = await _indexer.Fetch(new AlbumSearchCriteria
        {
            Artist = new Artist { Name = "  Artist A " },
            AlbumTitle = "Great  Album",
            Disambiguation = "(2020)"
        });

        var url = seenUrl!;
        var rawQuery = url[(url.IndexOf("q=", StringComparison.Ordinal) + 2)..];
        rawQuery = rawQuery[..rawQuery.IndexOf('#', StringComparison.Ordinal)]; // strip #albums fragment
        var query = Uri.UnescapeDataString(rawQuery);
        Assert.That(query, Is.EqualTo("Artist A Great Album (2020)"),
            "whitespace collapsed, parts joined with single spaces");
    }

    [Test]
    public async Task ArtistFetch_uses_generic_search()
    {
        string? seenUrl = null;
        _fake.BufferBehavior = (args, ct) =>
        {
            seenUrl = args.Last();
            return Task.FromResult(new YtDlpRunResult(0, string.Empty, string.Empty));
        };

        var releases = await _indexer.Fetch(new ArtistSearchCriteria
        {
            Artist = new Artist { Name = "Artist A" }
        });

        Assert.Multiple(() =>
        {
            Assert.That(seenUrl, Does.StartWith("ytsearch"), "artist search → ytsearchN: prefix");
            Assert.That(seenUrl, Does.EndWith(":Artist A"));
            Assert.That(releases, Is.Empty);
        });
    }

    [Test]
    public async Task Fetch_returns_empty_on_runner_failure_never_throws()
    {
        _fake.BufferBehavior = (args, ct) => Task.FromResult(new YtDlpRunResult(1, string.Empty, "boom"));

        var releases = await _indexer.Fetch(new AlbumSearchCriteria
        {
            Artist = new Artist { Name = "X" },
            AlbumTitle = "Y"
        });

        Assert.That(releases, Is.Empty);
    }

    [Test]
    public async Task Fetch_skips_unusable_entries_and_bad_json_lines()
    {
        _fake.BufferBehavior = (args, ct) =>
            Task.FromResult(new YtDlpRunResult(0, string.Join('\n',
            [
                "WARNING: something yt-dlp grumbled",
                "{not json}",
                Line("vid1", "V", "A", 1, "video"),
                Line("vid2", "NoUrl", "A", 1, "video", url: null) // url fallback is https watch → usable
            ]), string.Empty));

        var releases = await _indexer.Fetch(new AlbumSearchCriteria
        {
            Artist = new Artist { Name = "X" },
            AlbumTitle = "Y"
        });

        // Both vid entries are usable (Line() falls back to a watch URL).
        Assert.That(releases, Has.Count.EqualTo(2));
    }

    [Test]
    public void MapRelease_requires_id_and_https_url()
    {
        Assert.That(YtDlpIndexer.MapRelease(new SearchEntry(string.Empty, "https://x", "T", "C", null, "video")), Is.Null);
        Assert.That(YtDlpIndexer.MapRelease(new SearchEntry("id1", "ftp://x", "T", "C", null, "video")), Is.Null);
        Assert.That(YtDlpIndexer.MapRelease(new SearchEntry("id1", string.Empty, "T", "C", null, "video")), Is.Null);

        var mapped = YtDlpIndexer.MapRelease(
            new SearchEntry("id1", "https://www.youtube.com/watch?v=id1", "Solo Song", "", null, "video"),
            fallbackArtist: "Fallback");
        Assert.Multiple(() =>
        {
            Assert.That(mapped, Is.Not.Null);
            Assert.That(mapped!.Artist, Is.EqualTo("Fallback"), "blank channel → fallback artist");
            Assert.That(mapped.Title, Is.EqualTo("Fallback - Solo Song"));
        });
    }

    [Test]
    public void NormalizeQuery_collapses_and_trims_parts()
    {
        Assert.Multiple(() =>
        {
            Assert.That(YtDlpIndexer.NormalizeQuery("  A  ", "B", null, "", " C "), Is.EqualTo("A B C"));
            Assert.That(YtDlpIndexer.NormalizeQuery(null, "", "   "), Is.EqualTo(string.Empty));
        });
    }
}
