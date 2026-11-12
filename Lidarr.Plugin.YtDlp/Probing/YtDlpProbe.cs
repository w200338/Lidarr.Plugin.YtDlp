using System.Text.RegularExpressions;
using Lidarr.Plugin.YtDlp.Commands;
using Lidarr.Plugin.YtDlp.Runner;
using NLog;

namespace Lidarr.Plugin.YtDlp.Probing;

/// <summary>
/// External capability checks for the yt-dlp/ffmpeg binaries and a video
/// extraction probe. Process execution goes through <see cref="IYtDlpRunner"/>
/// so tests can script every outcome; the ffmpeg check runs a second,
/// path-bound runner (yt-dlp's runner is bound to the yt-dlp binary).
/// </summary>
public interface IYtDlpProbe
{
    /// <summary>Runs <c>yt-dlp --version</c>; returns the parsed version on success.</summary>
    Task<ProbeCheckResult> CheckYtDlpAsync(CancellationToken ct);

    /// <summary>Runs <c>ffmpeg -version</c> (PATH-only; no setting by design).</summary>
    Task<ProbeCheckResult> CheckFfmpegAsync(CancellationToken ct);

    /// <summary>
    /// Runs a --simulate -J extraction probe on a real URL — an early
    /// bot-detection/network signal. Timeouts are warnings, not failures.
    /// </summary>
    Task<ProbeCheckResult> ProbeVideoAsync(string url, CancellationToken ct);
}

public sealed class YtDlpProbe : IYtDlpProbe
{
    private static readonly Regex VersionShape = new(@"^\d{4}\.\d{1,2}\.\d{1,2}", RegexOptions.Compiled);

    private readonly IYtDlpRunner _ytDlp;
    private readonly IYtDlpRunner _ffmpeg;
    private readonly string _ytDlpPath;
    private readonly YtDlpCommandBuilder _builder = new();
    private readonly TimeSpan _videoProbeTimeout;
    private readonly Logger? _logger;

    public YtDlpProbe(IYtDlpRunner ytDlp, IYtDlpRunner ffmpeg, string ytDlpPath,
        TimeSpan? videoProbeTimeout = null, Logger? logger = null)
    {
        _ytDlp = ytDlp ?? throw new ArgumentNullException(nameof(ytDlp));
        _ffmpeg = ffmpeg ?? throw new ArgumentNullException(nameof(ffmpeg));
        _ytDlpPath = string.IsNullOrWhiteSpace(ytDlpPath) ? string.Empty : ytDlpPath.Trim();
        _videoProbeTimeout = videoProbeTimeout ?? TimeSpan.FromSeconds(30);
        _logger = logger;
    }

    public async Task<ProbeCheckResult> CheckYtDlpAsync(CancellationToken ct)
    {
        YtDlpRunResult result;
        try
        {
            result = await _ytDlp.RunBufferedAsync(_builder.BuildVersionArgs(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Debug(ex, "yt-dlp --version launch failed");
            return ProbeCheckResult.Fail(BuildMissingBinaryMessage());
        }

        if (result.ExitCode != 0)
        {
            return ProbeCheckResult.Fail(
                $"yt-dlp at '{_ytDlpPath}' exited with code {result.ExitCode} " +
                $"{(string.IsNullOrWhiteSpace(result.StandardError) ? "" : "(" + Tail(result.StandardError) + ")")}. " +
                "Check the path or install yt-dlp from https://github.com/yt-dlp/yt-dlp/releases.");
        }

        var line = FirstLine(result.StandardOutput);
        if (VersionShape.IsMatch(line))
        {
            return ProbeCheckResult.Pass(line);
        }

        return ProbeCheckResult.Warn(
            $"unexpected yt-dlp --version output '{Truncate(line)}'; assuming the binary is fine.");
    }

    public async Task<ProbeCheckResult> CheckFfmpegAsync(CancellationToken ct)
    {
        YtDlpRunResult result;
        try
        {
            result = await _ffmpeg.RunBufferedAsync(new[] { "-version" }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ProbeCheckResult.Fail(BuildFfmpegMissingMessage());
        }

        if (result.ExitCode != 0)
        {
            return ProbeCheckResult.Fail(BuildFfmpegMissingMessage());
        }

        return ProbeCheckResult.Pass(FirstLine(result.StandardOutput));
    }

    public async Task<ProbeCheckResult> ProbeVideoAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_videoProbeTimeout);

        try
        {
            var result = await _ytDlp.RunBufferedAsync(_builder.BuildProbeArgs(url), cts.Token).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return ProbeCheckResult.Fail(
                    $"yt-dlp could not extract {url}: {Tail(result.StandardError)}");
            }

            return ProbeCheckResult.Pass();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ProbeCheckResult.Warn(
                $"video probe for {url} timed out after {(int)_videoProbeTimeout.TotalSeconds}s " +
                "(slow network or bot-detection throttling).");
        }
    }

    private string BuildMissingBinaryMessage() =>
        $"yt-dlp not found or not runnable at '{_ytDlpPath}'. " +
        "Install it from https://github.com/yt-dlp/yt-dlp/releases or set the full path in the settings.";

    private static string BuildFfmpegMissingMessage() =>
        "ffmpeg not found on PATH. It is required for re-encoding and SponsorBlock trimming. " +
        "Install it (e.g. 'sudo apt install ffmpeg' or 'brew install ffmpeg') and try again.";

    private static string FirstLine(string s)
    {
        foreach (var line in s.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line.Trim();
            }
        }

        return string.Empty;
    }

    private static string Tail(string s)
    {
        s = s.Trim();
        return s.Length <= 300 ? s : s[^300..];
    }

    private static string Truncate(string s) => s.Length <= 120 ? s : s[..120];
}
