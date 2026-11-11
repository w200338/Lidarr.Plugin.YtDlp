using Lidarr.Plugin.YtDlp.Probing;
using Lidarr.Plugin.YtDlp.Runner;
using Lidarr.Plugin.YtDlp.Tests.Support;

namespace Lidarr.Plugin.YtDlp.Tests.Probing;

public class YtDlpProbeTests
{
    private static (YtDlpProbe Probe, FakeYtDlpRunner Yt, FakeYtDlpRunner Ffmpeg) Build(
        Func<IReadOnlyList<string>, CancellationToken, Task<YtDlpRunResult>>? yt = null,
        Func<IReadOnlyList<string>, CancellationToken, Task<YtDlpRunResult>>? ffmpeg = null)
    {
        var y = new FakeYtDlpRunner();
        var f = new FakeYtDlpRunner();
        if (yt != null)
        {
            y.BufferBehavior = yt;
        }

        if (ffmpeg != null)
        {
            f.BufferBehavior = ffmpeg;
        }

        return (new YtDlpProbe(y, f, "/opt/bin/yt-dlp"), y, f);
    }

    [Test]
    public async Task CheckYtDlp_parses_version()
    {
        var (probe, yt, _) = Build(yt: (a, ct) =>
            Task.FromResult(new YtDlpRunResult(0, "2025.01.15\n", string.Empty)));

        var result = await probe.CheckYtDlpAsync(CancellationToken.None);

        Assert.That(result.Ok, Is.True);
        Assert.That(result.Value, Is.EqualTo("2025.01.15"));
        Assert.That(yt.Calls.Single().Single(), Is.EqualTo("--version"));
    }

    [Test]
    public async Task CheckYtDlp_nonzero_exit_fails_with_actionable_message()
    {
        var (probe, _, _) = Build(yt: (a, ct) =>
            Task.FromResult(new YtDlpRunResult(127, string.Empty, "not found")));

        var result = await probe.CheckYtDlpAsync(CancellationToken.None);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Does.Contain("/opt/bin/yt-dlp"));
        Assert.That(result.Error, Does.Contain("github.com/yt-dlp/yt-dlp/releases"));
    }

    [Test]
    public async Task CheckYtDlp_launch_exception_fails_with_install_hint()
    {
        var (probe, _, _) = Build(yt: (a, ct) => throw new InvalidOperationException("No such file or directory"));

        var result = await probe.CheckYtDlpAsync(CancellationToken.None);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Does.Contain("not found or not runnable"));
        Assert.That(result.Error, Does.Contain("releases"));
    }

    [Test]
    public async Task CheckYtDlp_unexpected_version_output_passes_with_warning()
    {
        var (probe, _, _) = Build(yt: (a, ct) =>
            Task.FromResult(new YtDlpRunResult(0, "yt-dlp is great\n", string.Empty)));

        var result = await probe.CheckYtDlpAsync(CancellationToken.None);

        Assert.That(result.Ok, Is.True);
        Assert.That(result.IsWarning, Is.True);
        Assert.That(result.Error, Does.Contain("unexpected"));
    }

    [Test]
    public async Task CheckFfmpeg_success_returns_first_line()
    {
        var (probe, _, ffmpeg) = Build(ffmpeg: (a, ct) =>
            Task.FromResult(new YtDlpRunResult(0, "ffmpeg version 7.0.1\nCopyright (c)\n", string.Empty)));

        var result = await probe.CheckFfmpegAsync(CancellationToken.None);

        Assert.That(result.Ok, Is.True);
        Assert.That(result.Value, Is.EqualTo("ffmpeg version 7.0.1"));
        Assert.That(ffmpeg.Calls.Single().Single(), Is.EqualTo("-version"));
    }

    [Test]
    public async Task CheckFfmpeg_missing_fails_with_install_hint()
    {
        var (probe, _, _) = Build(ffmpeg: (a, ct) => Task.FromResult(new YtDlpRunResult(127, string.Empty, "command not found")));

        var result = await probe.CheckFfmpegAsync(CancellationToken.None);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Does.Contain("ffmpeg not found on PATH"));
        Assert.That(result.Error, Does.Contain("apt install ffmpeg"));
    }

    [Test]
    public async Task ProbeVideo_success()
    {
        var (probe, yt, _) = Build(yt: (a, ct) =>
            Task.FromResult(new YtDlpRunResult(0, "{}", string.Empty)));

        var result = await probe.ProbeVideoAsync("https://www.youtube.com/watch?v=abc123", CancellationToken.None);

        Assert.That(result.Ok, Is.True);
        Assert.That(yt.Calls.Single(), Does.Contain("--simulate"));
        Assert.That(yt.Calls.Single(), Does.Contain("-J"));
    }

    [Test]
    public async Task ProbeVideo_extraction_failure_fails()
    {
        var (probe, _, _) = Build(yt: (a, ct) =>
            Task.FromResult(new YtDlpRunResult(1, string.Empty, "Sign in to confirm you're not a bot")));

        var result = await probe.ProbeVideoAsync("https://www.youtube.com/watch?v=abc123", CancellationToken.None);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Does.Contain("not a bot"));
    }

    [Test]
    public async Task ProbeVideo_timeout_is_warning_not_failure()
    {
        // Tiny probe timeout keeps the test fast: the runner outlives it.
        var fastProbe = new YtDlpProbe(new FakeYtDlpRunner
        {
            BufferBehavior = DelayResult(5_000)
        }, new FakeYtDlpRunner(), "/opt/bin/yt-dlp", TimeSpan.FromMilliseconds(50));

        var result = await fastProbe.ProbeVideoAsync("https://www.youtube.com/watch?v=abc123", CancellationToken.None);

        Assert.That(result.Ok, Is.True);
        Assert.That(result.IsWarning, Is.True);
        Assert.That(result.Error, Does.Contain("timed out"));
    }

    [Test]
    public async Task Cancellation_propagates()
    {
        var (probe, _, _) = Build(yt: DelayResult(5_000));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // TaskCanceledException (a kind of OperationCanceledException) is acceptable.
        Assert.That(async () => await probe.CheckYtDlpAsync(cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    /// <summary>Runner behavior that delays then returns an empty success (honors cancellation).</summary>
    private static Func<IReadOnlyList<string>, CancellationToken, Task<YtDlpRunResult>> DelayResult(int ms) =>
        async (args, ct) =>
        {
            await Task.Delay(ms, ct);
            return new YtDlpRunResult(0, string.Empty, string.Empty);
        };
}
