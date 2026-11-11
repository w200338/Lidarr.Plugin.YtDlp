namespace Lidarr.Plugin.YtDlp.Probing;

/// <summary>
/// Outcome of one external connection check (binary, ffmpeg, SponsorBlock endpoint,
/// video probe). Ok=true with IsWarning means "usable, but worth telling the user".
/// Error carries the user-readable message either way.
/// </summary>
public sealed record ProbeCheckResult(bool Ok, string? Value = null, string? Error = null, bool IsWarning = false)
{
    public static ProbeCheckResult Pass(string? value = null) => new(true, value);

    public static ProbeCheckResult Warn(string message) => new(true, null, message, true);

    public static ProbeCheckResult Fail(string error) => new(false, null, error);
}
