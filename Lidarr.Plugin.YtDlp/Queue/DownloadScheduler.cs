using System.Threading.Channels;

namespace Lidarr.Plugin.YtDlp.Queue;

/// <summary>A unit of work for the <see cref="DownloadScheduler"/>.</summary>
public sealed class ScheduledJob
{
    public required string Id { get; init; }
    public required CancellationTokenSource Cts { get; init; }

    /// <summary>Executes the job. Must honor <see cref="Cts"/>.</summary>
    public required Func<CancellationToken, Task> Run { get; init; }
}

/// <summary>
/// The "space out downloads" engine: an unbounded single-reader queue with one
/// pump task that executes jobs EXACTLY one at a time, pausing a random number
/// of seconds in [minDelay, maxDelay] between consecutive jobs.
///
/// Spacing rules (per research: bot-detection mitigation):
/// - the FIRST job after the scheduler starts runs immediately;
/// - the pause is measured from the moment the previous job FINISHED;
/// - if the scheduler sat idle for longer than the max delay, the pause is
///   skipped (the "spacing budget" was already spent by the idle time).
///
/// Injectable seams (clock, random, delay) keep this fully unit-testable.
/// </summary>
public sealed class DownloadScheduler : IAsyncDisposable
{
    private readonly Channel<ScheduledJob> _channel;
    private readonly TimeSpan _minDelay;
    private readonly TimeSpan _maxDelay;
    private readonly Func<double, double, double> _randomSeconds;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayFor;
    private readonly Func<DateTime> _now;

    private readonly object _gate = new();
    private DateTime _lastJobFinishedAt;
    private int _jobsRun;
    private bool _stopping;
    private Task? _pump;
    private CancellationTokenSource? _stopCts;
    private volatile CancellationTokenSource? _activeCts;

    public DownloadScheduler(
        TimeSpan minDelay,
        TimeSpan maxDelay,
        Func<double, double, double>? randomSeconds = null,
        Func<TimeSpan, CancellationToken, Task>? delayFor = null,
        Func<DateTime>? now = null)
    {
        if (minDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minDelay));
        }

        if (maxDelay < minDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDelay), "maxDelay must be >= minDelay");
        }

        _minDelay = minDelay;
        _maxDelay = maxDelay;
        var rng = new Random();
        _randomSeconds = randomSeconds ?? ((min, max) => min + rng.NextDouble() * (max - min));
        _delayFor = delayFor ?? ((TimeSpan t, CancellationToken ct) => Task.Delay(t, ct));
        _now = now ?? (() => DateTime.UtcNow);

        _channel = Channel.CreateUnbounded<ScheduledJob>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public int PendingCount => _channel.Reader.Count;

    /// <summary>Records that a job finished (called by the pump).</summary>
    internal void MarkJobFinished()
    {
        lock (_gate)
        {
            _jobsRun++;
            _lastJobFinishedAt = _now();
        }
    }

    public void Enqueue(ScheduledJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (_stopping)
        {
            // Scheduler already stopped — refuse.
            throw new InvalidOperationException("Scheduler has been stopped.");
        }

        _channel.Writer.TryWrite(job);
    }

    /// <summary>Starts the pump if not already running. Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_pump != null)
            {
                return;
            }

            _stopCts = new CancellationTokenSource();
            _pump = PumpAsync(_stopCts.Token);
        }
    }

    /// <summary>
    /// Stops the pump. Queued jobs are never started and their CTSs are canceled
    /// (so the jobs' owners can mark them Cancelled). When <paramref name="cancelPending"/>
    /// is true the currently running job is canceled too; otherwise it finishes first.
    /// </summary>
    public async Task StopAsync(bool cancelPending)
    {
        Task? pump;
        CancellationTokenSource? stopCts;
        CancellationTokenSource? activeCts;

        lock (_gate)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
            _channel.Writer.TryComplete();
            pump = _pump;
            stopCts = _stopCts;
        }

        if (cancelPending)
        {
            // Cancel the in-flight job, if any. (Queued jobs are canceled by the pump
            // as it drains them — the channel cannot hand us its queued items back.)
            activeCts = _activeCts;
            try
            {
                activeCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        stopCts?.Cancel();

        if (pump != null)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch
            {
                // Pump faults are non-fatal for shutdown.
            }
        }
    }

    /// <summary>Number of seconds the pump should pause before the next job (0 = no pause).</summary>
    internal async Task<TimeSpan> GetSpacingDelayAsync(CancellationToken jobCt)
    {
        bool skip;
        lock (_gate)
        {
            skip = _jobsRun == 0 || _now() - _lastJobFinishedAt > _maxDelay;
        }

        if (skip)
        {
            return TimeSpan.Zero;
        }

        var seconds = _randomSeconds(_minDelay.TotalSeconds, _maxDelay.TotalSeconds);
        var delay = TimeSpan.FromSeconds(seconds);
        try
        {
            await _delayFor(delay, jobCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (jobCt.IsCancellationRequested)
        {
            // Job canceled while waiting for spacing — caller skips it.
        }

        return delay;
    }

    private async Task PumpAsync(CancellationToken stopCt)
    {
        try
        {
            await foreach (var job in _channel.Reader.ReadAllAsync(stopCt).ConfigureAwait(false))
            {
                // NOTE: a cancelled stopCt does NOT stop ReadAllAsync from returning
                // items already in the queue — so we must check _stopping ourselves
                // and cancel jobs we decide not to run.
                if (_stopping || job.Cts.IsCancellationRequested)
                {
                    // Cancelled while queued (or stop requested): skip without running.
                    CancelQuietly(job);
                    continue;
                }

                await GetSpacingDelayAsync(job.Cts.Token).ConfigureAwait(false);

                if (_stopping || job.Cts.IsCancellationRequested)
                {
                    CancelQuietly(job);
                    continue;
                }

                _activeCts = job.Cts;
                try
                {
                    await job.Run(job.Cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Canceled job — expected terminal path, nothing to do.
                }
                catch
                {
                    // A failed job must never block the next one. Job-level error
                    // reporting is the job's responsibility (it writes to its item).
                }
                finally
                {
                    _activeCts = null;
                    MarkJobFinished();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        finally
        {
            _activeCts = null;
        }
    }

    private static void CancelQuietly(ScheduledJob job)
    {
        try
        {
            job.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(cancelPending: true).ConfigureAwait(false);
    }
}
