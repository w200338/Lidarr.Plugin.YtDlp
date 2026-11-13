using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CliWrap;
using CliWrap.Buffered;
using CliWrap.EventStream;

namespace Lidarr.Plugin.YtDlp.Runner;

/// <summary>
/// Production <see cref="IYtDlpRunner"/> backed by CliWrap 3.x.
///
/// Verified CliWrap 3.10 semantics this relies on:
/// - <c>ExecuteBufferedAsync/ListenAsync</c> take (forcefulCt, gracefulCt); a single-token
///   call is forceful-only.
/// - Forceful = SIGKILL to the entire process tree (ffmpeg children included);
///   graceful = SIGINT (yt-dlp handles it: stops, cleans up partial files).
/// - <c>ListenAsync</c> merges an event channel on top of any configured pipes and emits
///   <see cref="StandardOutputCommandEvent"/>, <see cref="StandardErrorCommandEvent"/>
///   (line-oriented via internal ReadLinesAsync) and a final <see cref="ExitedCommandEvent"/>.
/// - UTF-8 must be passed explicitly (default is the console encoding).
/// - <c>CommandResultValidation.None</c> keeps non-zero exits out of the exception path.
/// </summary>
public sealed class CliWrapYtDlpRunner : IYtDlpRunner
{
    /// <summary>Grace period between SIGINT and SIGKILL when a running yt-dlp is cancelled.</summary>
    private static readonly TimeSpan KillEscalationTimeout = TimeSpan.FromSeconds(10);

    private readonly string _ytDlpPath;

    public CliWrapYtDlpRunner(string ytDlpPath)
    {
        _ytDlpPath = ytDlpPath ?? throw new ArgumentNullException(nameof(ytDlpPath));
    }

    public async Task<YtDlpRunResult> RunBufferedAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            var result = await BuildCommand(args)
                .ExecuteBufferedAsync(Encoding.UTF8, ct)
                .ConfigureAwait(false);

            return new YtDlpRunResult(result.ExitCode, result.StandardOutput, result.StandardError);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return YtDlpRunResult.StartFailed(DescribeStartFailure(ex));
        }
    }

    public async Task<YtDlpRunResult> RunStreamingAsync(IReadOnlyList<string> args, YtDlpStreamHandlers handlers, CancellationToken ct)
    {
        // Escalation plan for cancellation:
        //  ct (job token) fires  -> SIGINT (graceful; yt-dlp stops cleanly)
        //  +10s still alive      -> SIGKILL entire process tree (ffmpeg included)
        using var forcefulCts = new CancellationTokenSource();
        using var escalation = ct.Register(() =>
        {
            try
            {
                forcefulCts.CancelAfter(KillEscalationTimeout);
            }
            catch (ObjectDisposedException)
            {
                // CTS disposed in a race — the process is already being torn down.
            }
        });

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var exitCode = -1;

        try
        {
            await foreach (var evt in BuildCommand(args)
                .ListenAsync(Encoding.UTF8, Encoding.UTF8, forcefulCts.Token, ct)
                .ConfigureAwait(false))
            {
                switch (evt)
                {
                    case StandardOutputCommandEvent @out:
                        stdout.Append(@out.Text).Append('\n');
                        // A single CliWrap event can contain several lines;
                        // the IYtDlpRunner contract promises line-split
                        // callbacks, so split explicitly.
                        foreach (var line in SplitLines(@out.Text))
                        {
                            handlers.OnStdOutLine(line);
                        }
                        break;
                    case StandardErrorCommandEvent @err:
                        stderr.Append(@err.Text).Append('\n');
                        foreach (var line in SplitLines(@err.Text))
                        {
                            handlers.OnStdErrLine(line);
                        }
                        break;
                    case ExitedCommandEvent exited:
                        exitCode = exited.ExitCode;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("yt-dlp run was canceled.", ct);
        }
        catch (OperationCanceledException) when (forcefulCts.IsCancellationRequested && ct.IsCancellationRequested)
        {
            // Escalation killed the process after SIGINT was ignored.
            throw new OperationCanceledException("yt-dlp run was canceled (forced after timeout).", ct);
        }
        catch (Exception ex)
        {
            return YtDlpRunResult.StartFailed(DescribeStartFailure(ex));
        }

        return new YtDlpRunResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            yield return line.TrimEnd('\r');
        }
    }

    private Command BuildCommand(IReadOnlyList<string> args) =>
        Cli.Wrap(_ytDlpPath)
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None);

    private static string DescribeStartFailure(Exception ex)
    {
        var inner = Unwrap(ex);
        return inner is Win32Exception or FileNotFoundException
            ? $"Could not start yt-dlp: {inner.Message.Trim()}"
            : $"yt-dlp execution failed: {inner.Message.Trim()}";
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is AggregateException agg && agg.InnerExceptions.Count == 1)
        {
            ex = agg.InnerExceptions[0];
        }

        return ex.InnerException ?? ex;
    }
}
