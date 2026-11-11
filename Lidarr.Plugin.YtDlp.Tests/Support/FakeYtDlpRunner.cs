using System.Threading;
using Lidarr.Plugin.YtDlp.Runner;

namespace Lidarr.Plugin.YtDlp.Tests.Support;

/// <summary>
/// In-memory IYtDlpRunner for unit tests: records every call and replays
/// canned results. Streaming calls feed the handlers from a scriptable list.
/// </summary>
public sealed class FakeYtDlpRunner : IYtDlpRunner
{
    private readonly List<IReadOnlyList<string>> _calls = new();
    private readonly object _callsGate = new();

    /// <summary>Thread-safe snapshot of all calls so far (in order).</summary>
    public IReadOnlyList<IReadOnlyList<string>> Calls
    {
        get
        {
            lock (_callsGate)
            {
                return _calls.ToList();
            }
        }
    }

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

    /// <summary>When set, streaming calls await this before emitting lines (to hold a job in-flight).</summary>
    public TaskCompletionSource? StreamBlock { get; set; }

    /// <summary>Incremented when a streaming call reaches the point where it starts emitting lines.</summary>
    public int StreamStarted { get; private set; }

    public Task<YtDlpRunResult> RunBufferedAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        RecordCall(args);
        return BufferBehavior(args, ct);
    }

    public async Task<YtDlpRunResult> RunStreamingAsync(IReadOnlyList<string> args, YtDlpStreamHandlers handlers, CancellationToken ct)
    {
        RecordCall(args);

        // One-shot: only the next streaming call throws.
        var ex = StreamException;
        StreamException = null;
        if (ex != null)
        {
            throw ex;
        }

        if (StreamBlock != null)
        {
            // Honor cancellation while blocked, like the real runner does when the
            // job is canceled (CliWrap escalates to SIGKILL; here we just abort).
            using var reg = ct.Register(() => StreamBlock.TrySetCanceled(ct));
            try
            {
                await StreamBlock.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new OperationCanceledException(ct);
            }
        }

        Interlocked.Increment(ref _streamStarted);
        ct.ThrowIfCancellationRequested();

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

    private void RecordCall(IReadOnlyList<string> args)
    {
        lock (_callsGate)
        {
            _calls.Add(args);
        }
    }

    private int _streamStarted;
}
