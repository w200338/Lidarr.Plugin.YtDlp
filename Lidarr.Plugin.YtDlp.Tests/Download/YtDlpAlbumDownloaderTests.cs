using Lidarr.Plugin.YtDlp.Download;
using Lidarr.Plugin.YtDlp.Runner;
using Lidarr.Plugin.YtDlp.Settings;
using Lidarr.Plugin.YtDlp.Tests.Support;
using NUnit.Framework;

namespace Lidarr.Plugin.YtDlp.Tests.Download;

/// <summary>
/// Phase 7 (native model): the downloader now mutates a host-free
/// <see cref="DownloadJobState"/> (the old Abstractions StreamingDownloadItem
/// is gone). The command contract (probe → streaming download) is unchanged.
/// </summary>
public class YtDlpAlbumDownloaderTests
{
    private const string SingleVideo = "abc123";
    private const string Playlist = "PL123";

    private static YtDlpSettings Settings() => new()
    {
        YtDlpPath = "yt-dlp",
        MinDownloadDelaySeconds = 0,
        MaxDownloadDelaySeconds = 0
    };

    private static DownloadJobState State(YtDlpAlbumRef album, string outputPath, string title) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Album = album,
            OutputPath = outputPath,
            Title = title
        };

    private static void FeedCompletion(FakeYtDlpRunner fake, double percent = 100, string? track = "Final Track")
    {
        // YtDlpProgressParser line format: "Downloading X in Ys at Z" or the
        // [download] 100.0% of ... line. Use the parser's actual trigger shape.
        fake.StreamLines.Enqueue((null, "[download] " + percent.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".0% of 10.00KiB at 10.00KiB/s"));
        if (track != null)
        {
            // Parser sets CurrentTrack from "[download] Destination:" lines.
            fake.StreamLines.Enqueue((null, $"[download] Destination: /tmp/{track}.m4a"));
        }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ytdlp-downloader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ----- single track -----

    [Test]
    public async Task SingleTrack_probes_then_downloads_with_no_playlist()
    {
        var fake = new FakeYtDlpRunner
        {
            BufferBehavior = (args, ct) =>
            {
                // Probe: --simulate -J --no-playlist
                Assert.That(args, Does.Contain("-J"));
                Assert.That(args, Does.Contain("--no-playlist"));
                return Task.FromResult(new YtDlpRunResult(0,
                    "{\"title\":\"Song\",\"channel\":\"Artist\",\"duration\":120}", string.Empty));
            }
        };
        FeedCompletion(fake);

        var dir = TempDir();
        var state = State(new YtDlpAlbumRef(YtDlpAlbumKind.SingleTrack, SingleVideo), dir, "Artist - Song");
        var downloader = new YtDlpAlbumDownloader(fake, Settings());

        var outcome = await downloader.DownloadAsync(state.Album, dir, state, state.Cts.Token);

        var downloadCall = fake.Calls.Last();
        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(YtDlpDownloadOutcome.Completed));
            Assert.That(state.Status, Is.EqualTo(YtDlpJobStatus.Completed));
            Assert.That(state.Success, Is.True);
            Assert.That(state.Progress, Is.EqualTo(100).Within(0.01));
            Assert.That(downloadCall, Does.Contain("--no-playlist"), "single track must not walk playlists");
            Assert.That(downloadCall[^1], Is.EqualTo("https://www.youtube.com/watch?v=" + SingleVideo));
            Assert.That(downloadCall, Does.Contain("-o"), "output template expected");
        });
        Assert.That(Directory.Exists(dir), Is.True);
    }

    [Test]
    public async Task SingleTrack_probe_failure_is_non_fatal()
    {
        var fake = new FakeYtDlpRunner
        {
            BufferBehavior = (args, ct) => Task.FromResult(new YtDlpRunResult(1, string.Empty, "probe exploded"))
        };
        FeedCompletion(fake, 100, null);

        var dir = TempDir();
        var state = State(new YtDlpAlbumRef(YtDlpAlbumKind.SingleTrack, SingleVideo), dir, "A - B");
        var downloader = new YtDlpAlbumDownloader(fake, Settings());

        var outcome = await downloader.DownloadAsync(state.Album, dir, state, state.Cts.Token);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(YtDlpDownloadOutcome.Completed));
            Assert.That(state.Status, Is.EqualTo(YtDlpJobStatus.Completed));
            Assert.That(fake.Calls, Has.Count.EqualTo(2), "probe + download");
        });
    }

    // ----- playlist -----

    [Test]
    public async Task Playlist_uses_archive_and_break_on_existing()
    {
        var fake = new FakeYtDlpRunner
        {
            BufferBehavior = (args, ct) =>
            {
                Assert.That(args, Does.Contain("--flat-playlist"));
                return Task.FromResult(new YtDlpRunResult(0,
                    "{\"title\":\"Album\",\"playlist_count\":3}", string.Empty));
            }
        };
        FeedCompletion(fake, 66, "01 - Track A");

        var dir = TempDir();
        var state = State(new YtDlpAlbumRef(YtDlpAlbumKind.Playlist, Playlist), dir, "Artist - Album");
        var downloader = new YtDlpAlbumDownloader(fake, Settings());

        var outcome = await downloader.DownloadAsync(state.Album, dir, state, state.Cts.Token);

        var downloadCall = fake.Calls.Last();
        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(YtDlpDownloadOutcome.Completed));
            Assert.That(state.Status, Is.EqualTo(YtDlpJobStatus.Completed));
            Assert.That(downloadCall, Does.Contain("--download-archive"), "archive file makes re-runs idempotent");
            Assert.That(downloadCall, Does.Contain("--break-on-existing"));
            Assert.That(downloadCall, Does.Not.Contain("--no-playlist"));
            Assert.That(downloadCall[^1], Is.EqualTo("https://www.youtube.com/playlist?list=" + Playlist));
            Assert.That(state.CurrentTrack, Is.EqualTo("01 - Track A"), "destination line feeds the UI");
        });
    }

    // ----- failure / cancellation -----

    [Test]
    public async Task NonZeroExit_marks_failed_with_stderr_tail()
    {
        var fake = new FakeYtDlpRunner
        {
            BufferBehavior = (args, ct) => Task.FromResult(new YtDlpRunResult(0,
                "{\"title\":\"x\"}", string.Empty))
        };
        fake.StreamExitCode = 1;
        fake.StreamLines.Enqueue((null, "ERROR: RequestBlocked: bot check"));
        fake.StreamLines.Enqueue((null, "second error line"));

        var dir = TempDir();
        var state = State(new YtDlpAlbumRef(YtDlpAlbumKind.SingleTrack, SingleVideo), dir, "A - B");
        var downloader = new YtDlpAlbumDownloader(fake, Settings());

        var outcome = await downloader.DownloadAsync(state.Album, dir, state, state.Cts.Token);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(YtDlpDownloadOutcome.Failed));
            Assert.That(state.Status, Is.EqualTo(YtDlpJobStatus.Failed));
            Assert.That(state.Success, Is.False);
            Assert.That(state.ErrorMessage, Does.Contain("exited with code 1"));
            Assert.That(state.ErrorMessage, Does.Contain("bot check"));
            Assert.That(state.CompletedAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task Cancellation_marks_cancelled_and_does_not_report_failure()
    {
        var fake = new FakeYtDlpRunner();
        fake.StreamBlock = new TaskCompletionSource();

        var dir = TempDir();
        var state = State(new YtDlpAlbumRef(YtDlpAlbumKind.SingleTrack, SingleVideo), dir, "A - B");
        var downloader = new YtDlpAlbumDownloader(fake, Settings());

        var task = downloader.DownloadAsync(state.Album, dir, state, state.Cts.Token);
        // Let the job reach the streaming block.
        for (var i = 0; i < 100 && fake.StreamStarted < 1; i++)
        {
            await Task.Delay(5);
        }

        state.Cts.Cancel();

        var outcome = await task;

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(YtDlpDownloadOutcome.Cancelled));
            Assert.That(state.Status, Is.EqualTo(YtDlpJobStatus.Cancelled));
            Assert.That(state.Success, Is.False);
        });
    }

    // ----- progress -----

    [Test]
    public async Task Progress_updates_flow_into_state()
    {
        var fake = new FakeYtDlpRunner
        {
            BufferBehavior = (args, ct) => Task.FromResult(new YtDlpRunResult(0,
                "{\"title\":\"Album\",\"playlist_count\":2}", string.Empty))
        };
        fake.StreamLines.Enqueue((null, "[download] Destination: /tmp/01 - One.m4a"));
        fake.StreamLines.Enqueue((null, "[download] 50.0% of 10.00KiB at 10.00KiB/s"));
        fake.StreamLines.Enqueue((null, "[download] Destination: /tmp/02 - Two.m4a"));
        fake.StreamLines.Enqueue((null, "[download] 100.0% of 10.00KiB at 10.00KiB/s"));

        var dir = TempDir();
        var state = State(new YtDlpAlbumRef(YtDlpAlbumKind.Playlist, Playlist), dir, "Artist - Album");
        var downloader = new YtDlpAlbumDownloader(fake, Settings());

        var outcome = await downloader.DownloadAsync(state.Album, dir, state, state.Cts.Token);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(YtDlpDownloadOutcome.Completed));
            Assert.That(state.Progress, Is.EqualTo(100).Within(0.01), "final un-throttled sync always lands");
            Assert.That(state.StartedAt, Is.GreaterThan(DateTime.MinValue));
        });
    }

    [Test]
    public void State_progress_updates_are_ignored_after_terminal()
    {
        var dir = TempDir();
        var state = State(new YtDlpAlbumRef(YtDlpAlbumKind.SingleTrack, SingleVideo), dir, "A - B");

        state.MarkDownloading();
        state.MarkTerminal(YtDlpJobStatus.Completed, success: true, null);
        state.UpdateProgress(10, "ghost");

        Assert.Multiple(() =>
        {
            Assert.That(state.Progress, Is.EqualTo(100), "terminal progress is frozen");
            Assert.That(state.CurrentTrack, Is.Null);
            Assert.That(state.IsCompleted, Is.True);
        });
    }
}
