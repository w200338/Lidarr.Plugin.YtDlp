namespace Lidarr.Plugin.YtDlp.Runner;

/// <summary>Outcome of one yt-dlp invocation. ExitCode -1 = the process could not start.</summary>
public sealed record YtDlpRunResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool IsSuccess => ExitCode == 0;

    public static YtDlpRunResult StartFailed(string reason) => new(-1, string.Empty, reason);
}

/// <summary>Line callbacks for streaming execution (stdout/stderr, already line-split).</summary>
public sealed record YtDlpStreamHandlers(Action<string> OnStdOutLine, Action<string> OnStdErrLine)
{
    public static YtDlpStreamHandlers Ignore { get; } = new(_ => { }, _ => { });
}

/// <summary>
/// Abstraction over "run yt-dlp with these arguments". Production implementation is
/// <see cref="CliWrapYtDlpRunner"/>; tests use fakes.
/// </summary>
public interface IYtDlpRunner
{
    /// <summary>
    /// Runs yt-dlp and waits for completion, capturing full stdout/stderr.
    /// Throws <see cref="OperationCanceledException"/> when <paramref name="ct"/> fires.
    /// Never throws for process-level failures — they are reported via the result.
    /// </summary>
    Task<YtDlpRunResult> RunBufferedAsync(IReadOnlyList<string> args, CancellationToken ct);

    /// <summary>
    /// Runs yt-dlp streaming line callbacks. Returns when the process exits.
    /// Throws <see cref="OperationCanceledException"/> when <paramref name="ct"/> fires.
    /// </summary>
    Task<YtDlpRunResult> RunStreamingAsync(IReadOnlyList<string> args, YtDlpStreamHandlers handlers, CancellationToken ct);
}
