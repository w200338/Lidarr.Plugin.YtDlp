using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.YtDlp.Download;
using Lidarr.Plugin.YtDlp.Settings;
using Lidarr.Plugin.YtDlp.Tests.Support;
using NUnit.Framework;
using YtDlpRunResult = Lidarr.Plugin.YtDlp.Runner.YtDlpRunResult;

namespace Lidarr.Plugin.YtDlp.Tests.Download;

[TestFixture]
public class YtDlpDownloadClientTests
{
    private string _outputDir = null!;

    [SetUp]
    public void SetUp()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), $"ytdlp-client-{Guid.NewGuid():N}");
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_outputDir, recursive: true); } catch { /* best effort */ }
    }

    private static YtDlpSettings NoDelaySettings() => new()
    {
        YtDlpPath = "yt-dlp",
        MinDownloadDelaySeconds = 0,
        MaxDownloadDelaySeconds = 0
    };

    private static FakeYtDlpRunner SuccessfulFake()
    {
        var fake = new FakeYtDlpRunner
        {
            BufferBehavior = (args, ct) => Task.FromResult(new YtDlpRunResult(0,
                """{"title": "Album", "channel": "Artist", "playlist_count": 1}""", string.Empty)),
            StreamExitCode = 0
        };
        fake.StreamLines.Enqueue((null, "[download] Destination: /x/Song.mp3"));
        fake.StreamLines.Enqueue((null, "[download] 100% of  1.00MiB in 00:01"));
        return fake;
    }

    private static async Task<StreamingDownloadItem> WaitForTerminalAsync(YtDlpDownloadClient client, string id, TimeSpan timeout = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        timeout = timeout == default ? TimeSpan.FromSeconds(10) : timeout;

        while (sw.Elapsed < timeout)
        {
            var item = await client.GetDownloadAsync(id);
            if (item != null && item.IsCompleted)
            {
                return item;
            }

            await Task.Delay(25);
        }

        Assert.Fail("download did not reach terminal state in time");
        return null!;
    }

    [Test]
    public async Task Enqueue_runs_to_completion_and_reports_result()
    {
        var fake = SuccessfulFake();
        await using var client = new YtDlpDownloadClient(NoDelaySettings(), fake);

        var init = await client.InitializeAsync();
        var id = await client.EnqueueAlbumDownloadAsync("yt:abc123", _outputDir);

        Assert.Multiple(() =>
        {
            Assert.That(init.IsValid, Is.True);
            Assert.That(id, Is.Not.Empty);
        });

        var item = await WaitForTerminalAsync(client, id);

        Assert.Multiple(() =>
        {
            Assert.That(item.Status, Is.EqualTo(StreamingDownloadStatus.Completed));
            Assert.That(item.Success, Is.True);
            Assert.That(item.AlbumId, Is.EqualTo("yt:abc123"));
            Assert.That(item.Album!.Title, Is.EqualTo("Album"));
        });
    }

    [Test]
    public async Task Enqueue_invalid_album_id_throws()
    {
        await using var client = new YtDlpDownloadClient(NoDelaySettings(), SuccessfulFake());

        // ValueTask → Task so the action fits NUnit's Func<Task> constraint shape.
        Func<Task> act = () => client.EnqueueAlbumDownloadAsync("not-a-valid-token", _outputDir).AsTask();
        Assert.That(act, Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task Duplicate_enqueue_returns_existing_id_while_active()
    {
        var fake = SuccessfulFake();
        fake.StreamBlock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new YtDlpDownloadClient(NoDelaySettings(), fake);

        var id1 = await client.EnqueueAlbumDownloadAsync("playlist:PL1234567890abcdef", _outputDir);
        var id2 = await client.EnqueueAlbumDownloadAsync("playlist:PL1234567890abcdef", _outputDir);
        var id3 = await client.EnqueueAlbumDownloadAsync("playlist:PL1234567890abcdef#2", _outputDir); // normalized form

        fake.StreamBlock.TrySetResult();
        await WaitForTerminalAsync(client, id1);

        Assert.Multiple(() =>
        {
            Assert.That(id2, Is.EqualTo(id1), "same album+path while active → same id");
            Assert.That(id3, Is.EqualTo(id1), "normalized token (#N stripped) → same id");
            Assert.That(fake.Calls.Count(c => !c.Contains("--simulate")), Is.EqualTo(1), "exactly one real download ran (probe excluded)");
        });
    }

    [Test]
    public async Task GetActiveDownloads_shows_active_and_recent_terminal_items()
    {
        var fake = SuccessfulFake();
        fake.StreamBlock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new YtDlpDownloadClient(NoDelaySettings(), fake);

        var id = await client.EnqueueAlbumDownloadAsync("yt:abc123", _outputDir);
        var whileQueued = await client.GetActiveDownloadsAsync();
        Assert.That(whileQueued, Has.Count.EqualTo(1));

        fake.StreamBlock.TrySetResult();
        var item = await WaitForTerminalAsync(client, id);
        var afterDone = await client.GetActiveDownloadsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(item.IsCompleted, Is.True);
            Assert.That(afterDone, Has.Count.EqualTo(1), "terminal item stays visible during grace period");
            Assert.That(afterDone[0].Id, Is.EqualTo(id));
        });
    }

    [Test]
    public async Task Remove_active_download_cancels_and_deletes_data()
    {
        var fake = SuccessfulFake();
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.StreamBlock = block;
        Directory.CreateDirectory(_outputDir);
        File.WriteAllText(Path.Combine(_outputDir, "partial.mp3"), "data");

        await using var client = new YtDlpDownloadClient(NoDelaySettings(), fake);
        var id = await client.EnqueueAlbumDownloadAsync("yt:abc123", _outputDir);

        // Let the download reach the in-flight state (probe done, stream blocked).
        while (fake.StreamStarted < 1 && fake.Calls.Count < 2)
        {
            await Task.Delay(10);
        }

        var removed = await client.RemoveDownloadAsync(id, deleteData: true);
        block.TrySetResult();

        // The canceled run should settle to Cancelled.
        var item = await client.GetDownloadAsync(id);
        for (var i = 0; i < 100 && item != null && !item.IsCompleted; i++)
        {
            await Task.Delay(25);
            item = await client.GetDownloadAsync(id);
        }

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.True);
            Assert.That(Directory.Exists(_outputDir), Is.False, "deleteData removes the output directory");
        });
    }

    [Test]
    public async Task Remove_unknown_download_returns_false()
    {
        await using var client = new YtDlpDownloadClient(NoDelaySettings(), SuccessfulFake());

        Assert.That(await client.RemoveDownloadAsync("does-not-exist"), Is.False);
    }

    [Test]
    public async Task Initialize_fails_when_path_unconfigured()
    {
        var settings = NoDelaySettings();
        settings.YtDlpPath = string.Empty;
        await using var client = new YtDlpDownloadClient(settings, SuccessfulFake());

        var result = await client.InitializeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors, Does.Contain("ytDlpPath is not configured."));
        });
    }

    [Test]
    public async Task Failed_download_surfaces_error_and_stays_visible()
    {
        var fake = SuccessfulFake();
        fake.StreamExitCode = 1;
        fake.StreamLines.Clear();
        fake.StreamLines.Enqueue((null, "ERROR: [youtube] abc123: bot check failed"));

        await using var client = new YtDlpDownloadClient(NoDelaySettings(), fake);
        var id = await client.EnqueueAlbumDownloadAsync("yt:abc123", _outputDir);
        var item = await WaitForTerminalAsync(client, id);
        var stillVisible = await client.GetDownloadAsync(id);

        Assert.Multiple(() =>
        {
            Assert.That(item.Status, Is.EqualTo(StreamingDownloadStatus.Failed));
            Assert.That(item.Success, Is.False);
            Assert.That(item.ErrorMessage, Does.Contain("bot check failed"));
            Assert.That(stillVisible, Is.SameAs(item));
        });
    }
}
