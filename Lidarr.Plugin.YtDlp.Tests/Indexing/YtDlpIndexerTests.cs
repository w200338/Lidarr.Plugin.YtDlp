using System.Text;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.YtDlp.Indexing;
using Lidarr.Plugin.YtDlp.Runner;
using Lidarr.Plugin.YtDlp.Settings;
using Lidarr.Plugin.YtDlp.Tests.Support;

namespace Lidarr.Plugin.YtDlp.Tests.Indexing;

public class YtDlpIndexerTests
{
    private static YtDlpSettings Settings() => new()
    {
        YtDlpPath = "yt-dlp",
        SearchLimit = 25
    };

    private static FakeYtDlpRunner Fake(IReadOnlyList<string> lines)
    {
        return new FakeYtDlpRunner
        {
            BufferBehavior = (args, ct) => Task.FromResult(
                new YtDlpRunResult(0, string.Join('\n', lines), string.Empty))
        };
    }

    // JSON line builder for canned search output.
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
    public async Task Metadata_describes_service()
    {
        await using var indexer = new YtDlpIndexer(Settings(), new FakeYtDlpRunner());
        Assert.That(indexer.Protocol, Is.EqualTo("ytdlp"));
        Assert.That(indexer.PageSize, Is.EqualTo(25));
        Assert.That(indexer.MaxPageSize, Is.EqualTo(25));
        Assert.That(indexer.SupportsRss, Is.False);
        Assert.That(indexer.ServiceName, Does.Contain("yt-dlp"));
    }

    [Test]
    public void Initialize_fails_when_path_not_configured()
    {
        var indexer = new YtDlpIndexer(new YtDlpSettings { YtDlpPath = string.Empty }, new FakeYtDlpRunner());
        var result = indexer.InitializeAsync().AsTask().Result;
        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void Initialize_succeeds_when_path_configured()
    {
        var indexer = new YtDlpIndexer(Settings(), new FakeYtDlpRunner());
        var result = indexer.InitializeAsync().AsTask().Result;
        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public async Task SearchTracks_maps_entries_to_tracks()
    {
        var fake = Fake(new[]
        {
            Line("abc123", "Song One", "Artist A", 213.5, "video"),
            Line("def456", "Song | Two", "Artist B", 120.0, "video")
        });
        await using var indexer = new YtDlpIndexer(Settings(), fake);

        var tracks = await indexer.SearchTracksAsync("some query");

        Assert.That(tracks, Has.Count.EqualTo(2));
        Assert.That(tracks[0].Id, Is.EqualTo("yt:abc123"));
        Assert.That(tracks[0].Title, Is.EqualTo("Song One"));
        Assert.That(tracks[0].Artist.Name, Is.EqualTo("Artist A"));
        Assert.That(tracks[0].Duration, Is.EqualTo(TimeSpan.FromSeconds(213.5)));
        Assert.That((string)tracks[0].Metadata["url"], Is.EqualTo("https://www.youtube.com/watch?v=abc123"));

        // '|' in a title survives the JSON round-trip intact.
        Assert.That(tracks[1].Title, Is.EqualTo("Song | Two"));

        // The search used the ytsearchN: prefix.
        var call = fake.Calls.Single();
        Assert.That(call.Last(), Does.StartWith("ytsearch"));
    }

    [Test]
    public async Task SearchTracks_dedupes_by_id_and_caps_at_limit()
    {
        var settings = Settings();
        settings.SearchLimit = 2;
        var fake = Fake(new[]
        {
            Line("a", "A", "X", 1, "video"),
            Line("b", "B", "X", 2, "video"),
            Line("a", "A dup", "X", 3, "video"),
            Line("c", "C", "X", 4, "video"),
            Line("d", "D", "X", 5, "video")
        });
        await using var indexer = new YtDlpIndexer(settings, fake);

        var tracks = await indexer.SearchTracksAsync("q");

        Assert.That(tracks, Has.Count.EqualTo(2));
        Assert.That(tracks.Select(t => t.Id), Is.EqualTo(new[] { "yt:a", "yt:b" }));
    }

    [Test]
    public async Task SearchTracks_returns_empty_on_runner_failure()
    {
        var fake = new FakeYtDlpRunner
        {
            BufferBehavior = (a, ct) => Task.FromResult(new YtDlpRunResult(1, string.Empty, "boom"))
        };
        await using var indexer = new YtDlpIndexer(Settings(), fake);

        var tracks = await indexer.SearchTracksAsync("q");
        Assert.That(tracks, Is.Empty);
    }

    [Test]
    public async Task SearchAlbums_maps_playlists_and_single_videos()
    {
        var fake = Fake(new[]
        {
            // A playlist (channel = album artist; url is a watch URL but type says playlist).
            Line("PL1234567890abcdef", "Some Album", "The Band", null, "playlist"),
            // A plain video → single-track album.
            Line("vid0000001", "Standalone Song", "Singer", 95.0, "video")
        });
        await using var indexer = new YtDlpIndexer(Settings(), fake);

        var albums = await indexer.SearchAlbumsAsync("q");

        Assert.That(albums, Has.Count.EqualTo(2));

        var pl = albums[0];
        Assert.That(pl.Id, Is.EqualTo("playlist:PL1234567890abcdef"));
        Assert.That(pl.Title, Is.EqualTo("Some Album"));
        Assert.That(pl.Artist.Name, Is.EqualTo("The Band"));
        Assert.That(pl.Type, Is.EqualTo(StreamingAlbumType.Album));

        var single = albums[1];
        Assert.That(single.Id, Is.EqualTo("yt:vid0000001"));
        Assert.That(single.TrackCount, Is.EqualTo(1));
        Assert.That(single.Type, Is.EqualTo(StreamingAlbumType.Single));
        Assert.That(single.Duration, Is.EqualTo(TimeSpan.FromSeconds(95.0)));
    }

    [Test]
    public async Task SearchAlbums_skips_entries_without_id()
    {
        var fake = new FakeYtDlpRunner
        {
            BufferBehavior = (a, ct) => Task.FromResult(new YtDlpRunResult(0,
                "{not json}\n" + Line("vid1", "V", "A", 1, "video"), string.Empty))
        };
        await using var indexer = new YtDlpIndexer(Settings(), fake);

        var albums = await indexer.SearchAlbumsAsync("q");
        Assert.That(albums, Has.Count.EqualTo(1));
        Assert.That(albums[0].Id, Is.EqualTo("yt:vid1"));
    }

    [Test]
    public async Task GetAlbum_expands_playlist_probe()
    {
        const string probe = """"
            {"title":"Greatest Hits","channel":"The Band","playlist":[
              {"id":"v1","title":"T1","duration":120.0,"url":"https://www.youtube.com/watch?v=v1","type":"video"},
              {"id":"v2","title":"T2","duration":150.5,"url":"https://www.youtube.com/watch?v=v2","type":"video"}]}
            """";
        var fake = new FakeYtDlpRunner
        {
            BufferBehavior = (a, ct) => Task.FromResult(new YtDlpRunResult(0, probe, string.Empty))
        };
        await using var indexer = new YtDlpIndexer(Settings(), fake);

        var album = await indexer.GetAlbumAsync("playlist:PL1234567890abcdef");

        Assert.That(album, Is.Not.Null);
        Assert.That(album!.Id, Is.EqualTo("playlist:PL1234567890abcdef"));
        Assert.That(album.Title, Is.EqualTo("Greatest Hits"));
        Assert.That(album.Artist.Name, Is.EqualTo("The Band"));
        Assert.That(album.TrackCount, Is.EqualTo(2));

        var tracks = (List<StreamingTrack>)album.Metadata[YtDlpIndexer.TracksMetadataKey];
        Assert.That(tracks, Has.Count.EqualTo(2));
        Assert.That(tracks[0].TrackNumber, Is.EqualTo(1));
        Assert.That(tracks[1].Title, Is.EqualTo("T2"));
        Assert.That(tracks[1].Id, Is.EqualTo("yt:v2"));

        // Used the flat-playlist probe args.
        var call = fake.Calls.Single();
        Assert.That(call, Does.Contain("--flat-playlist"));
        Assert.That(call, Does.Contain("-J"));
    }

    [Test]
    public async Task GetAlbum_expands_single_video_probe()
    {
        const string probe = """"
            {"id":"abc123","title":"Solo Song","channel":"Singer","duration":213.5,
             "webpage_url":"https://www.youtube.com/watch?v=abc123"}
            """";
        var fake = new FakeYtDlpRunner
        {
            BufferBehavior = (a, ct) => Task.FromResult(new YtDlpRunResult(0, probe, string.Empty))
        };
        await using var indexer = new YtDlpIndexer(Settings(), fake);

        var album = await indexer.GetAlbumAsync("yt:abc123");

        Assert.That(album, Is.Not.Null);
        Assert.That(album!.Id, Is.EqualTo("yt:abc123"));
        Assert.That(album.Title, Is.EqualTo("Solo Song"));
        Assert.That(album.TrackCount, Is.EqualTo(1));
        Assert.That(album.Duration, Is.EqualTo(TimeSpan.FromSeconds(213.5)));

        var call = fake.Calls.Single();
        Assert.That(call, Is.Not.Empty);
        Assert.That(call, Does.Contain("--simulate"));
    }

    [Test]
    public async Task GetAlbum_returns_null_for_unknown_token()
    {
        await using var indexer = new YtDlpIndexer(Settings(), new FakeYtDlpRunner());
        Assert.That(await indexer.GetAlbumAsync("banana"), Is.Null);
        Assert.That(await indexer.GetAlbumAsync(""), Is.Null);
    }

    [Test]
    public async Task GetAlbum_returns_null_on_probe_failure()
    {
        var fake = new FakeYtDlpRunner
        {
            BufferBehavior = (a, ct) => Task.FromResult(new YtDlpRunResult(1, string.Empty, "nope"))
        };
        await using var indexer = new YtDlpIndexer(Settings(), fake);
        Assert.That(await indexer.GetAlbumAsync("yt:abc123"), Is.Null);
    }

    [Test]
    public async Task Stream_variants_yield_same_as_list_variants()
    {
        var fake = Fake(new[]
        {
            Line("abc123", "Song One", "Artist A", 213.5, "video")
        });
        await using var indexer = new YtDlpIndexer(Settings(), fake);

        var streamed = new List<StreamingTrack>();
        await foreach (var t in indexer.SearchTracksStreamAsync("q"))
        {
            streamed.Add(t);
        }

        Assert.That(streamed, Has.Count.EqualTo(1));
        Assert.That(streamed[0].Id, Is.EqualTo("yt:abc123"));
    }
}
