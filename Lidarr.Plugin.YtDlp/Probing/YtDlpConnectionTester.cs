using Lidarr.Plugin.YtDlp.Settings;
using Lidarr.Plugin.YtDlp.SponsorBlock;

namespace Lidarr.Plugin.YtDlp.Probing;

/// <summary>
/// Composes the individual external checks into one user-readable
/// <see cref="ConnectionTestResult"/>. All checks that can run independently
/// do run — a failing binary doesn't suppress the ffmpeg/SponsorBlock findings
/// (Tubifarry pattern), but checks that depend on a working yt-dlp binary are
/// skipped after it fails (their failures would be noise).
/// </summary>
public sealed class YtDlpConnectionTester
{
    private readonly IYtDlpProbe _probe;
    private readonly ISponsorBlockClient _sponsorBlock;

    public YtDlpConnectionTester(IYtDlpProbe probe, ISponsorBlockClient sponsorBlock)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _sponsorBlock = sponsorBlock ?? throw new ArgumentNullException(nameof(sponsorBlock));
    }

    /// <param name="probeUrl">
    /// Optional real video URL for the extraction probe (early bot-detection
    /// signal). Timeouts surface as warnings, never as failures.
    /// </param>
    public async Task<ConnectionTestResult> TestAsync(
        YtDlpSettings settings, string? probeUrl = null, CancellationToken ct = default)
    {
        var errors = new List<ConnectionTestError>();
        var warnings = new List<string>();

        var yt = await _probe.CheckYtDlpAsync(ct).ConfigureAwait(false);
        Append(yt, "YtDlpPath", errors, warnings);

        if (yt.Ok)
        {
            if (settings.Reencode != ReencodeFormat.Disabled || settings.UseSponsorBlock)
            {
                var ffmpeg = await _probe.CheckFfmpegAsync(ct).ConfigureAwait(false);
                Append(ffmpeg, "Reencode", errors, warnings);
            }

            if (settings.UseSponsorBlock)
            {
                var sb = await _sponsorBlock.CheckEndpointAsync(settings.SponsorBlockApiEndpoint, ct).ConfigureAwait(false);
                Append(sb, "SponsorBlockApiEndpoint", errors, warnings);
            }

            if (!string.IsNullOrWhiteSpace(probeUrl))
            {
                var video = await _probe.ProbeVideoAsync(probeUrl.Trim(), ct).ConfigureAwait(false);
                Append(video, "YtDlpPath", errors, warnings);
            }
        }

        return new ConnectionTestResult(errors, warnings);
    }

    private static void Append(ProbeCheckResult check, string property, List<ConnectionTestError> errors, List<string> warnings)
    {
        if (check.Ok)
        {
            if (check.IsWarning && !string.IsNullOrEmpty(check.Error))
            {
                warnings.Add(check.Error);
            }
        }
        else if (!string.IsNullOrEmpty(check.Error))
        {
            errors.Add(new ConnectionTestError(property, check.Error));
        }
    }
}
