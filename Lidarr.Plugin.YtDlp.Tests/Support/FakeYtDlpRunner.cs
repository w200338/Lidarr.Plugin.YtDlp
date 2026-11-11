using Lidarr.Plugin.YtDlp.Runner;

namespace Lidarr.Plugin.YtDlp.Tests.Support;

/// <summary>
/// In-memory IYtDlpRunner for unit tests: records every call and replays
/// canned results. Streaming calls feed the handlers from a scriptable list.
/// </summary>
public sealed class FakeYtDlpRunner : IYtDlpRunner
{
    public List<IReadOnlyList<string>> Calls { get; } = new();

    /// <summary>Buffered behavior; defaults to success with empty output.</summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<YtDlpRunResult>> BufferBehavior
        { get; set; }
        = (args, ct) => Task.FromResult(new YtDlpRunResult(0, string.Empty, string.Empty));

    /// <summary>Stdout lines to emit for the next streaming call (consumed in order).</summary>
    public Queue<(string StdOut, string StdErr)> StreamLines { get; } = new();

    /// <summary>Exit code for the next streaming call (default 0).</summary>
    public int StreamExitCode { get; set; }

    /// <summary>Exception to throw from the next streaming call (cancellation etc.).</summary>
    public Exception? StreamException { get; set; }

    public Task<YtDlpRunResult> RunBufferedAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        Calls.Add(args);
        return BufferBehavior(args, ct);
    }

    public async Task<YtDlpRunResult> RunStreamingAsync(IReadOnlyList<string> args, YtDlpStreamHandlers handlers, CancellationToken ct)
    {
        Calls.Add(args);
        if (StreamException != null)
        {
            throw StreamException;
        }

        while (StreamLines.Count > 0)
        {
            var (outLine, errLine) = StreamLines.Dequeue();
            if (outLine != null)
            {
                handlers.OnStdOutLine(outLine);
            }

            if (errLine != null)
            {
                handlers.OnStdErrLine(errLine);
            }

            ct.ThrowIfCancellationRequested();
        }

        await Task.Yield();
        return new YtDlpRunResult(StreamExitCode, string.Empty, string.Empty);
    }
}
