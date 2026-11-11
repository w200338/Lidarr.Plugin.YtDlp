using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.YtDlp.Probing;
using Lidarr.Plugin.YtDlp.Settings;
using Lidarr.Plugin.YtDlp.SponsorBlock;

namespace Lidarr.Plugin.YtDlp.Tests.Probing;

internal sealed class FakeProbe : IYtDlpProbe
{
    private int _ytDlpCalls;
    private int _ffmpegCalls;
    private int _videoCalls;

    public ProbeCheckResult YtDlp { get; set; } = ProbeCheckResult.Pass("2025.01.15");
    public ProbeCheckResult Ffmpeg { get; set; } = ProbeCheckResult.Pass("ffmpeg version 7");
    public ProbeCheckResult Video { get; set; } = ProbeCheckResult.Pass();

    public int YtDlpCalls => Volatile.Read(ref _ytDlpCalls);
    public int FfmpegCalls => Volatile.Read(ref _ffmpegCalls);
    public int VideoCalls => Volatile.Read(ref _videoCalls);

    public Task<ProbeCheckResult> CheckYtDlpAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _ytDlpCalls);
        return Task.FromResult(YtDlp);
    }

    public Task<ProbeCheckResult> CheckFfmpegAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _ffmpegCalls);
        return Task.FromResult(Ffmpeg);
    }

    public Task<ProbeCheckResult> ProbeVideoAsync(string url, CancellationToken ct)
    {
        Interlocked.Increment(ref _videoCalls);
        return Task.FromResult(Video);
    }
}

internal sealed class FakeSbClient : ISponsorBlockClient
{
    private int _calls;

    public ProbeCheckResult Result { get; set; } = ProbeCheckResult.Pass();
    public int Calls => Volatile.Read(ref _calls);

    public Task<ProbeCheckResult> CheckEndpointAsync(string endpoint, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(Result);
    }

    public Task<IReadOnlyList<SponsorBlockSegment>> GetSegmentsAsync(
        string endpoint, string videoId, IReadOnlyList<string> categories, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SponsorBlockSegment>>(Array.Empty<SponsorBlockSegment>());
}

public class YtDlpConnectionTesterTests
{
    private static (YtDlpConnectionTester Tester, FakeProbe Probe, FakeSbClient Sb) Build(
        YtDlpSettings? settings = null)
    {
        var probe = new FakeProbe();
        var sb = new FakeSbClient();
        return (new YtDlpConnectionTester(probe, sb), probe, sb);
    }

    private static YtDlpSettings Settings(ReencodeFormat reencode = ReencodeFormat.Disabled, bool sponsorBlock = false) =>
        new()
        {
            YtDlpPath = "yt-dlp",
            Reencode = reencode,
            UseSponsorBlock = sponsorBlock,
            SponsorBlockApiEndpoint = YtDlpSettings.DefaultSponsorBlockEndpoint
        };

    [Test]
    public async Task All_good_minimal_settings_passes_without_ffmpeg_or_sb_checks()
    {
        var (tester, probe, sb) = Build();

        var result = await tester.TestAsync(Settings());

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Warnings, Is.Empty);
        Assert.That(probe.YtDlpCalls, Is.EqualTo(1));
        Assert.That(probe.FfmpegCalls, Is.EqualTo(0), "no reencode and no SponsorBlock → ffmpeg not needed");
        Assert.That(sb.Calls, Is.EqualTo(0));
    }

    [Test]
    public async Task Reencode_enabled_triggers_ffmpeg_check()
    {
        var (tester, probe, _) = Build();

        _ = await tester.TestAsync(Settings(reencode: ReencodeFormat.Mp3));

        Assert.That(probe.FfmpegCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task SponsorBlock_enabled_triggers_ffmpeg_and_endpoint_checks()
    {
        var (tester, probe, sb) = Build();

        _ = await tester.TestAsync(Settings(sponsorBlock: true));

        Assert.That(probe.FfmpegCalls, Is.EqualTo(1));
        Assert.That(sb.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task Binary_failure_reports_and_skips_dependent_checks()
    {
        var (tester, probe, sb) = Build();
        probe.YtDlp = ProbeCheckResult.Fail("yt-dlp not found or not runnable at '/x'.");

        var result = await tester.TestAsync(Settings(reencode: ReencodeFormat.Opus, sponsorBlock: true));

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Count.EqualTo(1));
        Assert.That(result.Errors[0], Does.Contain("yt-dlp not found"));
        Assert.That(probe.FfmpegCalls, Is.EqualTo(0), "dependent checks skipped after binary failure");
        Assert.That(sb.Calls, Is.EqualTo(0));
    }

    [Test]
    public async Task Ffmpeg_failure_aggregates_with_passing_binary()
    {
        var (tester, probe, _) = Build();
        probe.Ffmpeg = ProbeCheckResult.Fail("ffmpeg not found on PATH.");

        var result = await tester.TestAsync(Settings(reencode: ReencodeFormat.Aac));

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Count.EqualTo(1));
        Assert.That(result.Errors[0], Does.Contain("ffmpeg"));
    }

    [Test]
    public async Task Sb_endpoint_failure_aggregates()
    {
        var (tester, _, sb) = Build();
        sb.Result = ProbeCheckResult.Fail("SponsorBlock endpoint unreachable: Name or service not known");

        var result = await tester.TestAsync(Settings(sponsorBlock: true));

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors[0], Does.Contain("SponsorBlock"));
    }

    [Test]
    public async Task Multiple_failures_all_reported()
    {
        var (tester, probe, sb) = Build();
        probe.Ffmpeg = ProbeCheckResult.Fail("ffmpeg broken.");
        sb.Result = ProbeCheckResult.Fail("sb down.");

        var result = await tester.TestAsync(Settings(reencode: ReencodeFormat.Opus, sponsorBlock: true));

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Video_probe_warning_is_not_a_failure()
    {
        var (tester, probe, _) = Build();
        probe.Video = ProbeCheckResult.Warn("video probe timed out after 30s (slow network or bot-detection throttling).");

        var result = await tester.TestAsync(Settings(), probeUrl: "https://www.youtube.com/watch?v=abc123");

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Warnings, Has.Count.EqualTo(1));
        Assert.That(result.Warnings[0], Does.Contain("timed out"));
        Assert.That(probe.VideoCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task Video_probe_failure_is_a_failure()
    {
        var (tester, probe, _) = Build();
        probe.Video = ProbeCheckResult.Fail("yt-dlp could not extract url: bot check");

        var result = await tester.TestAsync(Settings(), probeUrl: "https://www.youtube.com/watch?v=abc123");

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors[0], Does.Contain("bot check"));
    }

    [Test]
    public async Task No_probe_url_skips_video_probe()
    {
        var (tester, probe, _) = Build();

        _ = await tester.TestAsync(Settings());

        Assert.That(probe.VideoCalls, Is.EqualTo(0));
    }
}
