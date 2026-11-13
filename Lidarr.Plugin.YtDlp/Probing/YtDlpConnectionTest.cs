using Lidarr.Plugin.YtDlp.Runner;
using Lidarr.Plugin.YtDlp.Settings;
using Lidarr.Plugin.YtDlp.SponsorBlock;

namespace Lidarr.Plugin.YtDlp.Probing;

/// <summary>
/// Shared connection-test entry point for the indexer and the download client.
/// Builds the per-path runner (host DI cannot — the path is only known from
/// settings), the probe and the SponsorBlock client, then runs the tester.
/// </summary>
public static class YtDlpConnectionTest
{
    /// <summary>Override point for tests (replace the whole pipeline with a fake).</summary>
    internal static Func<YtDlpSettings, CancellationToken, Task<ConnectionTestResult>>? Override { get; set; }

    /// <param name="sponsorBlockEndpoint">
    /// Non-null (and non-empty) to also verify the SponsorBlock endpoint
    /// (download client only; the indexer passes null).
    /// </param>
    public static Task<ConnectionTestResult> RunAsync(
        YtDlpSettings settings, string? sponsorBlockEndpoint = null, CancellationToken ct = default)
    {
        return Override is not null
            ? Override(settings, ct)
            : CreateDefaultTester(settings).TestAsync(settings, sponsorBlockEndpoint, null, ct);
    }

    /// <summary>
    /// Synchronous wrapper for host contexts that are synchronous by contract
    /// (DownloadClientBase.Test(List&lt;ValidationFailure&gt;)). The async test is
    /// bounded (caller passes a limited CTS), so blocking is safe here.
    /// </summary>
    public static ConnectionTestResult Run(YtDlpSettings settings, string? sponsorBlockEndpoint = null, CancellationToken ct = default)
    {
        try
        {
            return RunAsync(settings, sponsorBlockEndpoint, ct).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return new ConnectionTestResult(
                new[] { new ConnectionTestError("YtDlpPath", "Connection test timed out.") },
                Array.Empty<string>());
        }
    }

    private static YtDlpConnectionTester CreateDefaultTester(YtDlpSettings settings)
    {
        var runner = YtDlpRunners.For(settings.YtDlpPath);
        // ffmpeg lives on PATH by design (Phase 5 decision); the runner is just
        // "run this binary with these args".
        var ffmpegRunner = YtDlpRunners.For("ffmpeg");
        var probe = new YtDlpProbe(runner, ffmpegRunner, settings.YtDlpPath);
        var sponsorBlock = new SponsorBlockClient();
        return new YtDlpConnectionTester(probe, sponsorBlock);
    }
}
