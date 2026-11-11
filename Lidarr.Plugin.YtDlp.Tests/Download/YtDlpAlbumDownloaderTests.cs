using System.Text;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.YtDlp.Download;
using Lidarr.Plugin.YtDlp.Settings;
using Lidarr.Plugin.YtDlp.Tests.Support;
using NUnit.Framework;
using YtDlpRunResult = Lidarr.Plugin.YtDlp.Runner.YtDlpRunResult;

namespace Lidarr.Plugin.YtDlp.Tests.Download;

[TestFixture]
public class YtDlpAlbumDownloaderTests
{
    private string _outputDir = null!;

    [SetUp]
    public void SetUp()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), $"ytdlp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outputDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_outputDir, recursive: true); } catch { /* best effort */ }
    }

    private static YtDlpSettings Settings(ReencodeFormat reencode = ReencodeFormat.Disabled) => new()
    {
        YtDlpPath = "yt-dlp",
        Reencode = reencode
    };

    [Test]
    public async Task SingleTrack_success_sets_album_progress_and_terminal_state()
    {
        var fake = new FakeYtDlpRunner
        {
            BufferBehavior = (args, ct) => Task.FromResult(new YtDlpRunResult(0,
                """{"title": "Get Lucky", "channel": "Daft Punk", "duration": 210}""", string.Empty))
        };
        fake.StreamLines.Enqueue(("[download] Destination: /x/Get Lucky.webm", null));
        fake.StreamLines.Enqueue((null, "[download]   5.0% of ~ 2.00MiB at 1.00MiB/s"));
        fake.StreamLines.Enqueue((null, "[download] 100% of  1.80MiB in 00:01 at 1.80MiB/s"));
        fake.StreamLines.Enqueue((null, "[ExtractAudio] Converting audio to mp3"));

        var downloader = new YtDlpAlbumDownloader(fake, Settings(ReencodeFormat.Mp3));
        var item = new StreamingDownloadItem();

        var outcome = await downloader.DownloadAsync(
            new YtDlpAlbumRef(YtDlpAlbumKind.SingleTrack, "abc123"), _outputDir, item, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(YtDlpDownloadOutcome.Completed));
            Assert.That(item.Status, Is.EqualTo(StreamingDownloadStatus.Completed));
            Assert.That(item.IsCompleted, Is.True);
            Assert.That(item.Success, Is.True);
            Assert.That(item.Progress, Is.EqualTo(100));
            Assert.That(item.Album, Is.Not.Null);
            Assert.That(item.Album!.Title, Is.EqualTo("Get Lucky"));
            Assert.That(item.Album.Artist.Name, Is.EqualTo("Daft Punk"));
            Assert.That(item.Album.TrackCount, Is.EqualTo(1));
            Assert.That(item.Album.Type, Is.EqualTo(StreamingAlbumType.Single));

            var probe = fake.Calls[0];
            var download = fake.Calls[1];
            Assert.That(probe, Does.Contain("-J"), "probe call");
            Assert.That(probe, Does.Contain("--no-playlist"));
            Assert.That(download, Does.Contain("--audio-format"));
            Assert.That(download, Does.Contain("mp3"));
            Assert.That(download, Does.Contain("--no-playlist"));
            Assert.That(download[^1], Is.EqualTo("https://www.youtube.com/watch?v=abc123"));
        });
    }

    [Test]
    public async Task Playlist_success_uses_playlist_args_and_track_count()
    {
        var fake = new FakeYtDlpRunner
        {
            BufferBehavior = (args, ct) => Task.FromResult(new YtDlpRunResult(0,
                """{"title": "Random Access Memories", "channel": "Daft Punk", "playlist_count": 3}""", string.Empty))
        };
        fake.StreamLines.Enqueue((null, "[download] Destination: /x/01 - Give Life Back.mp3"));
        fake.StreamLines.Enqueue((null, "[download] 100% of  3.00MiB in 00:02"));
        fake.StreamLines.Enqueue((null, "[download] Destination: /x/02 - Get Lucky.mp3"));
        fake.StreamLines.Enqueue((null, "[download] 100% of  3.00MiB in 00:02"));
        fake.StreamLines.Enqueue((null, "[download] Destination: /x/03 - Get Lucky (Alt).mp3"));
        fake.StreamLines.Enqueue((null, "[download] 100% of  3.00MiB in 00:02"));

        var downloader = new YtDlpAlbumDownloader(fake, Settings());
        var item = new StreamingDownloadItem();

        var outcome = await downloader.DownloadAsync(
            new YtDlpAlbumRef(YtDlpAlbumKind.Playlist, "PL1234567890abcdef"), _outputDir, item, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(YtDlpDownloadOutcome.Completed));
            Assert.That(item.Success, Is.True);
            Assert.That(item.Album!.Title, Is.EqualTo("Random Access Memories"));
            Assert.That(item.Album.TrackCount, Is.EqualTo(3));
            Assert.That(item.Album.Type, Is.EqualTo(StreamingAlbumType.Album));
            Assert.That(item.CurrentTrack, Is.EqualTo("03 - Get Lucky (Alt)"));

            var download = fake.Calls[1];
            Assert.That(download, Does.Contain("--download-archive"));
            Assert.That(download, Does.Contain("--break-on-existing"));
            Assert.That(download, Does.Not.Contain("--no-playlist"));
            Assert.That(download[^1], Is.EqualTo("https://www.youtube.com/playlist?list=PL1234567890abcdef"));
        });
    }

    [Test]
    public async Task Nonzero_exit_marks_failed_with_stderr_tail()
    {
        var fake = new FakeYtDlpRunner
        {
            BufferBehavior = (args, ct) => Task.FromResult(new YtDlpRunResult(0,
                """{"title": "Song", "channel": "Art", "duration": 60}""", string.Empty)),
            StreamExitCode = 1
        };
        fake.StreamLines.Enqueue((null, "[download]   5.0% of ~ 2.00MiB at 1.00MiB/s"));
        fake.StreamLines.Enqueue((null, "ERROR: [youtube] abc123: Unable to download JSON metadata: HTTP 403"));

        var downloader = new YtDlpAlbumDownloader(fake, Settings());
        var item = new StreamingDownloadItem();

        var outcome = await downloader.DownloadAsync(
            new YtDlpAlbumRef(YtDlpAlbumKind.SingleTrack, "abc123"), _outputDir, item, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(YtDlpDownloadOutcome.Failed));
            Assert.That(item.Status, Is.EqualTo(StreamingDownloadStatus.Failed));
            Assert.That(item.IsCompleted, Is.True);
            Assert.That(item.Success, Is.False);
            Assert.That(item.ErrorMessage, Does.Contain("exited with code 1"));
            Assert.That(item.ErrorMessage, Does.Contain("HTTP 403"));
        });
    }

    [Test]
    public async Task Cancellation_marks_cancelled()
    {
        var cts = new CancellationTokenSource();
        var fake = new FakeYtDlpRunner
        {
            BufferBehavior = (args, ct) => Task.FromResult(new YtDlpRunResult(0,
                """{"title": "Song", "channel": "Art", "duration": 60}""", string.Empty)),
            // Simulates the runner being told to abort: it throws OCE for the
            // caller's (now cancelled) token.
            StreamException = new OperationCanceledException()
        };

        var downloader = new YtDlpAlbumDownloader(fake, Settings());
        var item = new StreamingDownloadItem();
        cts.Cancel();

        var outcome = await downloader.DownloadAsync(
            new YtDlpAlbumRef(YtDlpAlbumKind.SingleTrack, "abc123"), _outputDir, item, cts.Token);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(YtDlpDownloadOutcome.Cancelled));
            Assert.That(item.Status, Is.EqualTo(StreamingDownloadStatus.Cancelled));
            Assert.That(item.IsCompleted, Is.True);
            Assert.That(item.Success, Is.False);
        });
    }

    [Test]
    public async Task Probe_failure_is_non_fatal_album_stays_null()
    {
        var fake = new FakeYtDlpRunner
        {
            BufferBehavior = (args, ct) => Task.FromResult(new YtDlpRunResult(1, string.Empty, "probe error")),
            StreamExitCode = 0
        };
        fake.StreamLines.Enqueue((null, "[download] Destination: /x/Song.mp3"));
        fake.StreamLines.Enqueue((null, "[download] 100% of  1.00MiB in 00:01"));

        var downloader = new YtDlpAlbumDownloader(fake, Settings());
        var item = new StreamingDownloadItem();

        var outcome = await downloader.DownloadAsync(
            new YtDlpAlbumRef(YtDlpAlbumKind.SingleTrack, "abc123"), _outputDir, item, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(YtDlpDownloadOutcome.Completed));
            Assert.That(item.Success, Is.True);
            Assert.That(item.Album, Is.Null);
            // Without a total, progress falls back to current track percent.
            Assert.That(item.Progress, Is.EqualTo(100));
        });
    }

    [Test]
    public async Task Progress_updates_are_throttled_but_final_flush_is_applied()
    {
        // The throttle (500ms) means rapid fake lines won't all reach the item,
        // but the final pre-terminal sync always does.
        var fake = new FakeYtDlpRunner
        {
            BufferBehavior = (args, ct) => Task.FromResult(new YtDlpRunResult(0,
                """{"title": "Song", "channel": "Art", "duration": 60}""", string.Empty))
        };
        for (var i = 1; i <= 100; i++)
        {
            fake.StreamLines.Enqueue((null, $"[download] {i:00.0}% of ~ 2.00MiB at 1.00MiB/s"));
        }

        var downloader = new YtDlpAlbumDownloader(fake, Settings());
        var item = new StreamingDownloadItem();

        var outcome = await downloader.DownloadAsync(
            new YtDlpAlbumRef(YtDlpAlbumKind.SingleTrack, "abc123"), _outputDir, item, CancellationToken.None);

        Assert.That(outcome, Is.EqualTo(YtDlpDownloadOutcome.Completed));
        Assert.That(item.Progress, Is.EqualTo(100), "final flush must reach the item regardless of throttle");
    }
}
