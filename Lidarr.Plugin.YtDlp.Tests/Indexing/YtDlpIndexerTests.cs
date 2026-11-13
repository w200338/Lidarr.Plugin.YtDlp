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

    /// <summary>
    /// Canned flat search entry line: the music search page's album entries
    /// carry ONLY {id, url} - no title/channel (that's the whole reason the
    /// indexer re-fetches each entry for its real title).
    /// </summary>
    private static string BrowseLine(string id, string? url = null)
        => $"{{\"id\":\"{id}\",\"url\":\"{url ?? $"https://music.youtube.com/browse/{id}"}\"}}";

    [Test]
    public async Task AlbumFetch_resolves_real_album_titles_per_entry()
    {
        _fake.BufferBehavior = (args, ct) =>
        {
            var target = args.Last();
            if (target.Contains("music.youtube.com/search", StringComparison.Ordinal))
            {
                return Task.FromResult(new YtDlpRunResult(0, string.Join('\n',
                [
                    BrowseLine("MPREb_1"),
                    BrowseLine("MPREb_2")
                ]), string.Empty));
            }

            // Entry inspection (flat re-fetch): one line of playlist metadata.
            // MPREb_1 also resolves its embedded album playlist (OLAK...);
            // MPREb_2 does not (fallback to the browse URL).
            return target.Contains("MPREb_1", StringComparison.Ordinal)
                ? Task.FromResult(new YtDlpRunResult(0, "{\"playlist_title\":\"Album - Great Album\",\"playlist_channel\":null,\"playlist_id\":\"OLAK5_test1\"}", string.Empty))
                : Task.FromResult(new YtDlpRunResult(0, "{\"playlist_title\":\"Album - Another Album\"}", string.Empty));
        };

        var releases = await _indexer.Fetch(new AlbumSearchCriteria
        {
            Artist = new Artist { Name = "Artist A" },
            AlbumTitle = "Great Album",
            Disambiguation = string.Empty
        });

        Assert.That(releases, Has.Count.EqualTo(2));

        var r = releases[0];
        Assert.Multiple(() =>
        {
            Assert.That(r.Title, Is.EqualTo("Artist A - Great Album"),
                "criteria artist + resolved album title ('Album - ' page prefix stripped); host parser needs 'Artist - Album' shape");
            Assert.That(r.Artist, Is.EqualTo("Artist A"));
            Assert.That(r.Album, Is.EqualTo("Great Album"));
            Assert.That(r.Guid, Is.EqualTo("7_MPREb_1"), "CleanupReleases prefixes Definition.Id");
            Assert.That(r.Indexer, Is.EqualTo("TestIndexer"));
            Assert.That(r.IndexerPriority, Is.EqualTo(25));
            Assert.That(r.DownloadUrl, Is.EqualTo("https://www.youtube.com/playlist?list=OLAK5_test1"),
                "inspection resolved the embedded album playlist → canonical playlist URL (the download client's accepted contract)");
            Assert.That(r.InfoUrl, Is.EqualTo("https://music.youtube.com/browse/MPREb_1"),
                "the browse page stays the human-facing link");
            Assert.That(r.Size, Is.EqualTo(0), "unknown size → host skips size specifications");
            Assert.That(r.PublishDate, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromMinutes(1)),
                "search time; flat search has no release date (MinValue rendered as a 739k-day age)");
            Assert.That(r.DownloadProtocol, Is.EqualTo(nameof(NzbDrone.Core.Indexers.YtDlpDownloadProtocol)),
                "CleanupReleases stamps the protocol from the indexer");
            Assert.That(r.IndexerId, Is.EqualTo(7));
        });

        // Honest per-entry titles: the second ranked entry is a different album
        // and must show its real name (the decision engine will reject it for
        // the "Great Album" search - that is the correct signal).
        Assert.That(releases[1].Title, Is.EqualTo("Artist A - Another Album"));
        Assert.That(releases[1].DownloadUrl, Is.EqualTo("https://music.youtube.com/browse/MPREb_2"),
            "no playlist id resolved → browse URL, which the download client also accepts");
    }

    [Test]
    public async Task AlbumFetch_falls_back_to_criteria_title_when_inspection_fails()
    {
        _fake.BufferBehavior = (args, ct) =>
        {
            var target = args.Last();
            if (target.Contains("music.youtube.com/search", StringComparison.Ordinal))
            {
                return Task.FromResult(new YtDlpRunResult(0, BrowseLine("MPREb_1"), string.Empty));
            }

            return Task.FromResult(new YtDlpRunResult(1, string.Empty, "inspection exploded"));
        };

        var releases = await _indexer.Fetch(new AlbumSearchCriteria
        {
            Artist = new Artist { Name = "Artist A" },
            AlbumTitle = "Great Album"
        });

        Assert.That(releases, Has.Count.EqualTo(1), "resolution failure must not drop the entry");
        Assert.That(releases[0].Title, Is.EqualTo("Artist A - Great Album"),
            "criteria-derived fallback title is still parseable by the host");
        Assert.That(releases[0].DownloadUrl, Is.EqualTo("https://music.youtube.com/browse/MPREb_1"));
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
            Assert.That(mapped.PublishDate, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromMinutes(1)),
                "search time (DateTime.MinValue rendered as a 739k-day age in the UI)");
        });
    }

    private static readonly DateTime FixedNow = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public void MapAlbumRelease_prefers_resolved_title_then_criteria_then_query()
    {
        var entry = new SearchEntry("MPREb_1", "https://music.youtube.com/browse/MPREb_1", "", "", null, "playlist");

        // 1. Resolved (real album title from the flat re-fetch) wins. A resolved
        //    playlist id upgrades the download URL to the canonical playlist.
        var r1 = YtDlpIndexer.MapAlbumRelease(entry, "Artist A", "Great Album", "Artist A Great Album",
            new EntryResolution("Real Album", null, "OLAK5_test1", 0), 2011, ReencodeFormat.Disabled, FixedNow, true);
        Assert.Multiple(() =>
        {
            Assert.That(r1, Is.Not.Null);
            Assert.That(r1!.Title, Is.EqualTo("Artist A - Real Album"));
            Assert.That(r1.Artist, Is.EqualTo("Artist A"));
            Assert.That(r1.Album, Is.EqualTo("Real Album"));
            Assert.That(r1.PublishDate, Is.EqualTo(new DateTime(2011, 12, 31)), "album-year based age");
            Assert.That(r1.DownloadUrl, Is.EqualTo("https://www.youtube.com/playlist?list=OLAK5_test1"),
                "resolved playlist id → canonical playlist URL");
        });

        // 1b. Resolved title but no playlist id → browse URL fallback.
        var r1b = YtDlpIndexer.MapAlbumRelease(entry, "Artist A", "Great Album", "Artist A Great Album",
            new EntryResolution("Real Album", null, null, 0), 2011, ReencodeFormat.Disabled, FixedNow, true);
        Assert.That(r1b!.DownloadUrl, Is.EqualTo("https://music.youtube.com/browse/MPREb_1"));

        // 2. No resolution → criteria album title (parseable: it IS what the user searched).
        var r2 = YtDlpIndexer.MapAlbumRelease(entry, "Artist A", "Great Album", "Artist A Great Album", null, 2011, ReencodeFormat.Disabled, FixedNow, true);
        Assert.That(r2!.Title, Is.EqualTo("Artist A - Great Album"));

        // 3. No criteria album either (artist-only search) → the whole query.
        var r3 = YtDlpIndexer.MapAlbumRelease(entry, "Artist A", null, "Artist A", null, 2011, ReencodeFormat.Disabled, FixedNow, true);
        Assert.That(r3!.Title, Is.EqualTo("Artist A - Artist A"), "still the 'Artist - X' shape");

        // 4. Unusable entries are dropped.
        Assert.That(YtDlpIndexer.MapAlbumRelease(new SearchEntry("", "https://x", "", "", null, "playlist"), "A", "B", "A B", null, 2011, ReencodeFormat.Disabled, FixedNow, true), Is.Null);
        Assert.That(YtDlpIndexer.MapAlbumRelease(new SearchEntry("id", "ftp://x", "", "", null, "playlist"), "A", "B", "A B", null, 2011, ReencodeFormat.Disabled, FixedNow, true), Is.Null);
    }

    [Test]
    public void MapAlbumRelease_publish_date_uses_album_year_dec31()
    {
        var entry = new SearchEntry("MPREb_1", "https://music.youtube.com/browse/MPREb_1", "", "", null, "playlist");

        // A released album: the age shown in the UI is now "years" not "0 minutes".
        Assert.That(
            YtDlpIndexer.MapAlbumRelease(entry, "A", "B", "A B", null, 2011, ReencodeFormat.Disabled, FixedNow, true)!.PublishDate,
            Is.EqualTo(new DateTime(2011, 12, 31, 0, 0, 0, DateTimeKind.Utc)));

        // Dec 31 is chosen deliberately: EarlyReleaseSpecification only rejects
        // same-year releases whose publish date falls INSIDE the early-release
        // window (album date + N days), and Dec 31 is never inside that window.
        Assert.That(
            YtDlpIndexer.PublishDateForYear(2011, FixedNow),
            Is.EqualTo(new DateTime(2011, 12, 31, 0, 0, 0, DateTimeKind.Utc)));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(1899)]
    [TestCase(2030)] // future year (bad metadata) → search time
    public void PublishDate_falls_back_to_search_time_for_missing_or_future_years(int albumYear)
    {
        Assert.That(YtDlpIndexer.PublishDateForYear(albumYear, FixedNow), Is.EqualTo(FixedNow));
    }

    [Test]
    public void MapAlbumRelease_estimates_size_from_entry_durations()
    {
        var entry = new SearchEntry("MPREb_1", "https://music.youtube.com/browse/MPREb_1", "", "", null, "playlist");

        // 1710s = 28:30 of summed track durations (observed live: ~1689s for a 13-track album).
        const double seconds = 1710;

        Assert.That(
            YtDlpIndexer.MapAlbumRelease(entry, "A", "B", "A B", new EntryResolution("B", null, null, seconds), 2011, ReencodeFormat.Mp3, FixedNow, true)!.Size,
            Is.EqualTo((long)(seconds * 30_000)), "MP3 V0 estimate");
        Assert.That(
            YtDlpIndexer.MapAlbumRelease(entry, "A", "B", "A B", new EntryResolution("B", null, null, seconds), 2011, ReencodeFormat.Aac, FixedNow, true)!.Size,
            Is.EqualTo((long)(seconds * 28_000)), "AAC VBR estimate");
        Assert.That(
            YtDlpIndexer.MapAlbumRelease(entry, "A", "B", "A B", new EntryResolution("B", null, null, seconds), 2011, ReencodeFormat.Disabled, FixedNow, true)!.Size,
            Is.EqualTo((long)(seconds * 32_000)), "source-stream estimate");

        // No resolved durations (inspection failed) → 0 = unknown, host skips size specs.
        Assert.That(
            YtDlpIndexer.MapAlbumRelease(entry, "A", "B", "A B", null, 2011, ReencodeFormat.Mp3, FixedNow, true)!.Size,
            Is.EqualTo(0));
    }

    [Test]
    public void MapAlbumRelease_reports_unknown_size_when_the_catalog_has_no_release_durations()
    {
        // AcceptableSizeSpecification rejects ANY Size > 0 release when the
        // catalog album has no release durations ("Album duration is 0"), so
        // the plugin must report 0 (unknown) in that case — the host then
        // skips the size check entirely, as it did before v0.2.4.
        var entry = new SearchEntry("MPREb_1", "https://music.youtube.com/browse/MPREb_1", "", "", null, "playlist");
        var resolved = new EntryResolution("B", null, null, 1710);

        Assert.That(
            YtDlpIndexer.MapAlbumRelease(entry, "A", "B", "A B", resolved, 2011, ReencodeFormat.Mp3, FixedNow, false)!.Size,
            Is.EqualTo(0), "no catalog durations → unknown size");
        Assert.That(
            YtDlpIndexer.MapAlbumRelease(entry, "A", "B", "A B", resolved, 2011, ReencodeFormat.Mp3, FixedNow, true)!.Size,
            Is.Not.EqualTo(0), "catalog durations present → estimate is reported");
    }

    [Test]
    public void CatalogHasReleaseDurations_mirrors_the_hosts_acceptable_size_math()
    {
        var album = new NzbDrone.Core.Music.Album
        {
            Title = "B",
            AnyReleaseOk = true,
            AlbumReleases = new List<NzbDrone.Core.Music.AlbumRelease>
            {
                new() { Monitored = true, Duration = 240_000 }
            }
        };

        // Same album shape but durationless release (catalog without duration data):
        var bareAlbum = new NzbDrone.Core.Music.Album
        {
            Title = "B",
            AnyReleaseOk = true,
            AlbumReleases = new List<NzbDrone.Core.Music.AlbumRelease>
            {
                new() { Monitored = true, Duration = 0 }
            }
        };

        Assert.Multiple(() =>
        {
            Assert.That(YtDlpIndexer.CatalogHasReleaseDurations(new AlbumSearchCriteria { Albums = new List<NzbDrone.Core.Music.Album> { album } }),
                Is.True, "catalog release with duration → estimate may be reported");
            Assert.That(YtDlpIndexer.CatalogHasReleaseDurations(new AlbumSearchCriteria { Albums = new List<NzbDrone.Core.Music.Album> { bareAlbum } }),
                Is.False, "durationless catalog → unknown size, host skips size check");
            // No albums at all → false.
            Assert.That(YtDlpIndexer.CatalogHasReleaseDurations(new AlbumSearchCriteria { Albums = new List<NzbDrone.Core.Music.Album>() }), Is.False);
            Assert.That(YtDlpIndexer.CatalogHasReleaseDurations(null), Is.False);
        });
    }

    [Test]
    public void MapAlbumRelease_adds_host_parseable_quality_token_for_reencode_modes()
    {
        var entry = new SearchEntry("MPREb_1", "https://music.youtube.com/browse/MPREb_1", "", "", null, "playlist");

        Assert.Multiple(() =>
        {
            Assert.That(YtDlpIndexer.MapAlbumRelease(entry, "A", "B", "A B", null, 2011, ReencodeFormat.Mp3, FixedNow, true)!.Title,
                Is.EqualTo("A - B (MP3 VBR V0)"));
            Assert.That(YtDlpIndexer.MapAlbumRelease(entry, "A", "B", "A B", null, 2011, ReencodeFormat.Aac, FixedNow, true)!.Title,
                Is.EqualTo("A - B (AAC VBR)"));
            Assert.That(YtDlpIndexer.MapAlbumRelease(entry, "A", "B", "A B", null, 2011, ReencodeFormat.Vorbis, FixedNow, true)!.Title,
                Is.EqualTo("A - B (VORBIS Q10)"));
            // Opus: the host Quality enum has no Opus entry → no token (Unknown), as before.
            Assert.That(YtDlpIndexer.MapAlbumRelease(entry, "A", "B", "A B", null, 2011, ReencodeFormat.Opus, FixedNow, true)!.Title,
                Is.EqualTo("A - B"));
            // Disabled: the delivered stream is the unknown source → no token, as before.
            Assert.That(YtDlpIndexer.MapAlbumRelease(entry, "A", "B", "A B", null, 2011, ReencodeFormat.Disabled, FixedNow, true)!.Title,
                Is.EqualTo("A - B"));
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
