using System.Globalization;
using Lidarr.Plugin.YtDlp.Download;
using Lidarr.Plugin.YtDlp.Probing;
using Lidarr.Plugin.YtDlp.Runner;
using Lidarr.Plugin.YtDlp.Settings;
using Lidarr.Plugin.YtDlp.Tests.Support;
using NLog;
using NSubstitute;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using NUnit.Framework;

namespace Lidarr.Plugin.YtDlp.Tests.Download;

/// <summary>
/// Phase 7 (native model): YtDlpDownloadClient derives from the host
/// DownloadClientBase&lt;YtDlpClientSettings&gt;. The host contract (DownloadId
/// stability, item mapping, removal, import marking) is what gets pinned here.
/// </summary>
public class YtDlpDownloadClientTests
{
    private const string WatchUrl = "https://www.youtube.com/watch?v=abc123";

    private YtDlpDownloadClient _client = null!;
    private FakeYtDlpRunner _fake = null!;
    private string _root = null!;
    private IDiskProvider _diskProvider = null!;
    private Func<string, IYtDlpRunner>? _previousFactory;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "ytdlp-client-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _fake = new FakeYtDlpRunner
        {
            // Metadata probe succeeds so the downloader proceeds straight to streaming.
            BufferBehavior = (args, ct) => Task.FromResult(new YtDlpRunResult(0,
                "{\"title\":\"Song\",\"channel\":\"Artist A\",\"duration\":120}", string.Empty))
        };
        // Streaming: one completed file.
        _fake.StreamLines.Enqueue((null, "[download] Destination: /tmp/Song.m4a"));
        _fake.StreamLines.Enqueue((null, "[download] 100.0% of 10.00KiB at 10.00KiB/s"));

        _previousFactory = YtDlpRunners.Factory;
        YtDlpRunners.Factory = _ => _fake;

        var settings = new YtDlpClientSettings
        {
            YtDlpPath = "yt-dlp",
            DownloadPath = _root,
            MinDownloadDelaySeconds = 0,
            MaxDownloadDelaySeconds = 0
        };

        _diskProvider = Substitute.For<IDiskProvider>();

        _client = new YtDlpDownloadClient(
            Substitute.For<IConfigService>(),
            _diskProvider,
            Substitute.For<IRemotePathMappingService>(),
            Substitute.For<ILocalizationService>(),
            LogManager.GetLogger("test"));

        _client.Definition = new DownloadClientDefinition
        {
            Id = 3,
            Name = "TestClient",
            Settings = settings
        };
    }

    [TearDown]
    public void TearDown()
    {
        YtDlpRunners.Factory = _previousFactory!;
        YtDlpRunners.ClearCache();
        YtDlpConnectionTest.Override = null;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static RemoteAlbum RemoteAlbum() => new()
    {
        Release = new ReleaseInfo
        {
            Title = "Artist A - Song",
            Artist = "Artist A",
            Album = "Song",
            DownloadUrl = WatchUrl
        },
        Artist = new Artist { Name = "Artist A" },
        Albums = new List<Album> { new() { Title = "Song" } }
    };

    private static async Task<DownloadClientItem> WaitFor(
        YtDlpDownloadClient client, string id, DownloadItemStatus? expectedStatus = null, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var item = client.GetItems().FirstOrDefault(i => i.DownloadId == id);
            if (item != null && (expectedStatus == null || item.Status == expectedStatus))
            {
                return item;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"item {id} never reached {expectedStatus?.ToString() ?? "any"} status; last=" +
            string.Join("|", client.GetItems().Select(i => $"{i.DownloadId}:{i.Status}:{i.Message}")));
    }

    // ----- Download / GetItems -----

    [Test]
    public async Task Download_returns_stable_id_and_completes_with_host_parseable_title()
    {
        var id = await _client.Download(RemoteAlbum(), Substitute.For<IIndexer>());

        Assert.That(!string.IsNullOrEmpty(id), Is.True, "download id must be a non-empty stable string");

        var item = await WaitFor(_client, id, DownloadItemStatus.Completed);
        Assert.Multiple(() =>
        {
            Assert.That(item.Title, Is.EqualTo("Artist A - Song"), "host import parses this title");
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Completed));
            Assert.That(item.DownloadClientInfo.Protocol, Is.EqualTo(nameof(NzbDrone.Core.Indexers.YtDlpDownloadProtocol)));
            Assert.That(item.DownloadClientInfo.Id, Is.EqualTo(3));
            Assert.That(item.DownloadClientInfo.Name, Is.EqualTo("TestClient"));
            Assert.That(item.OutputPath.FullPath, Is.EqualTo(Path.Combine(_root, "Artist A - Song")));
            Assert.That(Directory.Exists(Path.Combine(_root, "Artist A - Song")), Is.True);
        });
    }

    [Test]
    public void Download_rejects_non_youtube_urls_with_release_exception()
    {
        var album = RemoteAlbum();
        album.Release = new ReleaseInfo
        {
            Title = "Artist A - Song",
            Artist = "Artist A",
            Album = "Song",
            DownloadUrl = "https://example.com/song.mp3"
        };

        var ex = Assert.Throws<ReleaseDownloadException>(() => _client.Download(album, Substitute.For<IIndexer>()));
        Assert.That(ex!.Release.DownloadUrl, Is.EqualTo("https://example.com/song.mp3"));
    }

    [Test]
    public async Task Duplicate_download_while_active_returns_same_id()
    {
        // Hold the job in-flight so the second enqueue sees it as not-terminal.
        var blocker = new TaskCompletionSource();
        _fake.StreamBlock = blocker;

        var id1 = await _client.Download(RemoteAlbum(), Substitute.For<IIndexer>());
        // Let the first job reach the streaming block.
        for (var i = 0; i < 200 && _fake.StreamStarted < 1; i++)
        {
            await Task.Delay(5);
        }

        var id2 = await _client.Download(RemoteAlbum(), Substitute.For<IIndexer>());

        Assert.That(id2, Is.EqualTo(id1), "same album+path while active → same DownloadId");

        blocker.SetResult();
        _ = await WaitFor(_client, id1, DownloadItemStatus.Completed);
    }

    [Test]
    public async Task Failed_job_surfaces_error_message_as_item_message()
    {
        _fake.StreamLines.Clear();
        _fake.StreamExitCode = 1;
        _fake.StreamLines.Enqueue((null, "ERROR: [youtube] Signature extraction failed"));

        var id = await _client.Download(RemoteAlbum(), Substitute.For<IIndexer>());

        var item = await WaitFor(_client, id, DownloadItemStatus.Failed);
        Assert.That(item.Message, Does.Contain("exited with code 1"));
        Assert.That(item.Message, Does.Contain("Signature extraction failed"));
    }

    // ----- RemoveItem / MarkItemAsImported -----

    [Test]
    public async Task RemoveItem_drops_job_from_get_items()
    {
        var id = await _client.Download(RemoteAlbum(), Substitute.For<IIndexer>());
        var item = await WaitFor(_client, id, DownloadItemStatus.Completed);

        _client.RemoveItem(item, deleteData: false);

        Assert.That(_client.GetItems().Any(i => i.DownloadId == id), Is.False);
        Assert.That(Directory.Exists(Path.Combine(_root, "Artist A - Song")), Is.True, "deleteData=false keeps files");
    }

    [Test]
    public async Task RemoveItem_with_deleteData_deletes_output_folder()
    {
        var id = await _client.Download(RemoteAlbum(), Substitute.For<IIndexer>());
        var item = await WaitFor(_client, id, DownloadItemStatus.Completed);

        // The host base class performs the delete through IDiskProvider; the
        // substitute stands in for the filesystem, so we assert the call.
        var folder = Path.Combine(_root, "Artist A - Song");
        _diskProvider.FolderExists(folder).Returns(true);

        _client.RemoveItem(item, deleteData: true);

        Assert.That(_client.GetItems().Any(i => i.DownloadId == id), Is.False);
        _diskProvider.Received().DeleteFolder(folder, true);
    }

    [Test]
    public async Task MarkItemAsImported_keeps_item_visible_and_flags_import()
    {
        var id = await _client.Download(RemoteAlbum(), Substitute.For<IIndexer>());
        var item = await WaitFor(_client, id, DownloadItemStatus.Completed);

        _client.MarkItemAsImported(item); // must not throw (base default throws)

        Assert.That(_client.GetItems().Any(i => i.DownloadId == id), Is.True,
            "imported terminal items stay visible until grace expiry/removal");
    }

    // ----- status / test -----

    [Test]
    public void GetStatus_reports_download_root()
    {
        var status = _client.GetStatus();

        Assert.Multiple(() =>
        {
            Assert.That(status.IsLocalhost, Is.False);
            Assert.That(status.OutputRootFolders.Select(p => p.FullPath), Does.Contain(_root));
        });
    }

    [Test]
    public void Test_reports_missing_download_path()
    {
        _client.Definition = new DownloadClientDefinition
        {
            Id = 3,
            Name = "TestClient",
            Settings = new YtDlpClientSettings { YtDlpPath = "yt-dlp" }
        };

        var result = _client.Test();

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors.Any(e => e.PropertyName == "DownloadPath"));
    }

    [Test]
    public void Test_maps_connection_errors_into_failures()
    {
        YtDlpConnectionTest.Override = (s, ct) => Task.FromResult(new ConnectionTestResult(
            new[] { new ConnectionTestError("YtDlpPath", "yt-dlp not found.") },
            Array.Empty<string>()));

        var result = _client.Test();

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors.Any(e => e.PropertyName == "YtDlpPath"));
        });
    }

    // ----- pure helpers -----

    [Test]
    public void ResolveAlbumTitle_prefers_release_then_matched_then_parsed()
    {
        var release = new ReleaseInfo { Title = "Artist A - Song", Album = "Song", Artist = "Artist A" };
        var album = new RemoteAlbum { Release = release, Albums = new List<Album> { new() { Title = "Matched" } } };

        Assert.Multiple(() =>
        {
            Assert.That(YtDlpDownloadClient.ResolveAlbumTitle(album, release), Is.EqualTo("Song"), "release.Album wins");

            release.Album = string.Empty;
            Assert.That(YtDlpDownloadClient.ResolveAlbumTitle(album, release), Is.EqualTo("Matched"), "matched album second");

            album.Albums = null;
            Assert.That(YtDlpDownloadClient.ResolveAlbumTitle(album, release), Is.EqualTo("Song"), "parse 'Artist - Album' last");

            var bare = new ReleaseInfo { Title = "NoDashTitle" };
            Assert.That(YtDlpDownloadClient.ResolveAlbumTitle(new RemoteAlbum { Release = bare }, bare), Is.EqualTo("NoDashTitle"));
        });
    }

    [Test]
    public void SanitizeDirectoryName_collapses_whitespace_and_handles_empty()
    {
        Assert.Multiple(() =>
        {
            Assert.That(YtDlpDownloadClient.SanitizeDirectoryName("   A   B  "), Is.EqualTo("A B"));
            Assert.That(YtDlpDownloadClient.SanitizeDirectoryName(""), Is.EqualTo("download"));
            Assert.That(YtDlpDownloadClient.SanitizeDirectoryName("   "), Is.EqualTo("download"));
            Assert.That(YtDlpDownloadClient.SanitizeDirectoryName("trailing..."), Is.EqualTo("trailing"));
        });
    }

    [Test]
    public void Host_surface_matches_native_contract()
    {
        Assert.Multiple(() =>
        {
            Assert.That(_client, Is.InstanceOf<DownloadClientBase<YtDlpClientSettings>>());
            Assert.That(_client.Name, Is.EqualTo("YouTube (yt-dlp)"));
            Assert.That(_client.Protocol, Is.EqualTo(nameof(NzbDrone.Core.Indexers.YtDlpDownloadProtocol)));
            Assert.That(_client.ConfigContract, Is.EqualTo(typeof(YtDlpClientSettings)));
        });
    }
}
