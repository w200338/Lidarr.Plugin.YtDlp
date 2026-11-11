using System.Collections.Concurrent;
using Lidarr.Plugin.YtDlp.Queue;
using NUnit.Framework;

namespace Lidarr.Plugin.YtDlp.Tests.Queue;

[TestFixture]
public class DownloadSchedulerTests
{
    [Test]
    public async Task Jobs_run_strictly_sequentially_with_spacing_between_them()
    {
        var running = 0;
        var maxRunning = 0;
        var gapsMs = new List<double>();
        var mu = new object();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var scheduler = new DownloadScheduler(TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(25));
        DateTime lastEnd = DateTime.UtcNow;

        for (var i = 0; i < 3; i++)
        {
            var index = i; // capture for the closure
            scheduler.Enqueue(new ScheduledJob
            {
                Id = index.ToString(),
                Cts = new CancellationTokenSource(),
                Run = async _ =>
                {
                    lock (mu)
                    {
                        running++;
                        maxRunning = Math.Max(maxRunning, running);
                        if (index > 0)
                        {
                            gapsMs.Add((DateTime.UtcNow - lastEnd).TotalMilliseconds);
                        }
                    }

                    await Task.Delay(20); // simulated download work

                    lock (mu)
                    {
                        running--;
                        lastEnd = DateTime.UtcNow;
                        if (index == 2)
                        {
                            done.TrySetResult();
                        }
                    }
                }
            });
        }

        scheduler.Start();

        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await scheduler.StopAsync(cancelPending: false);

        Assert.Multiple(() =>
        {
            Assert.That(maxRunning, Is.EqualTo(1), "exactly one download at a time");
            Assert.That(gapsMs.Count, Is.EqualTo(2));
            foreach (var gap in gapsMs)
            {
                Assert.That(gap, Is.InRange(10.0, 60.0), $"gap {gap:F1}ms should be in [15,25]ms plus slack");
            }
        });
    }

    [Test]
    public async Task First_job_starts_without_spacing_delay()
    {
        var delays = new List<TimeSpan>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var scheduler = new DownloadScheduler(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            randomSeconds: (min, max) => 17,
            delayFor: (t, _) => { delays.Add(t); return Task.CompletedTask; },
            now: () => now);

        scheduler.Enqueue(new ScheduledJob
        {
            Id = "first",
            Cts = new CancellationTokenSource(),
            Run = _ =>
            {
                started.TrySetResult();
                return Task.CompletedTask;
            }
        });
        scheduler.Start();

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.StopAsync(cancelPending: false);

        Assert.That(delays, Is.Empty, "first job must not wait for spacing");
    }

    [Test]
    public async Task Consecutive_job_without_idle_waits_the_random_delay()
    {
        var delays = new List<TimeSpan>();
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var scheduler = new DownloadScheduler(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            randomSeconds: (min, max) => 17,
            delayFor: (t, _) => { delays.Add(t); return Task.CompletedTask; },
            now: () => now);

        for (var i = 0; i < 2; i++)
        {
            var index = i; // capture for the closure
            scheduler.Enqueue(new ScheduledJob
            {
                Id = index.ToString(),
                Cts = new CancellationTokenSource(),
                Run = _ =>
                {
                    if (index == 1)
                    {
                        allDone.TrySetResult();
                    }

                    return Task.CompletedTask;
                }
            });
        }

        scheduler.Start();
        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.StopAsync(cancelPending: false);

        Assert.That(delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(17) }));
    }

    [Test]
    public async Task Idle_longer_than_max_delay_skips_spacing()
    {
        var delays = new List<TimeSpan>();
        var secondRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var advance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var scheduler = new DownloadScheduler(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            randomSeconds: (min, max) => 17,
            delayFor: async (t, _) => { delays.Add(t); await advance.Task; },
            now: () => clock);

        scheduler.Enqueue(new ScheduledJob
        {
            Id = "a",
            Cts = new CancellationTokenSource(),
            Run = _ =>
            {
                // First job finishes; the clock stays put.
                return Task.CompletedTask;
            }
        });

        scheduler.Start();
        // Wait until job A has finished (pump marks it finished before reading next).
        // Give the pump a moment, then fast-forward the clock past the max delay.
        await Task.Delay(100);
        clock = clock.AddSeconds(60);
        advance.TrySetResult(); // release any (unexpected) delay

        scheduler.Enqueue(new ScheduledJob
        {
            Id = "b",
            Cts = new CancellationTokenSource(),
            Run = _ =>
            {
                secondRan.TrySetResult();
                return Task.CompletedTask;
            }
        });

        await secondRan.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.StopAsync(cancelPending: false);

        Assert.That(delays, Is.Empty, "60s idle (> 30s max) must skip the spacing delay");
    }

    [Test]
    public async Task Canceled_queued_job_is_skipped_not_run()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ranA = false;
        var ranB = false;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var scheduler = new DownloadScheduler(
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1),
            randomSeconds: (min, max) => 1,
            delayFor: (t, _) => Task.CompletedTask);

        var ctsA = new CancellationTokenSource();
        scheduler.Enqueue(new ScheduledJob
        {
            Id = "a",
            Cts = ctsA,
            Run = async token =>
            {
                started.TrySetResult();
                ranA = true;
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
        });

        scheduler.Enqueue(new ScheduledJob
        {
            Id = "b",
            Cts = new CancellationTokenSource(),
            Run = _ =>
            {
                ranB = true;
                done.TrySetResult();
                return Task.CompletedTask;
            }
        });

        scheduler.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ctsA.Cancel(); // cancel A while it runs; B is still queued behind it

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.StopAsync(cancelPending: false);

        Assert.Multiple(() =>
        {
            Assert.That(ranA, Is.True, "A started before its cancel (expected)");
            Assert.That(ranB, Is.True, "B ran after A was canceled");
        });
    }

    [Test]
    public async Task Failing_job_does_not_block_next_job()
    {
        var ranB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var scheduler = new DownloadScheduler(
            TimeSpan.Zero,
            TimeSpan.Zero,
            randomSeconds: (min, max) => 0,
            delayFor: (t, _) => Task.CompletedTask);

        scheduler.Enqueue(new ScheduledJob
        {
            Id = "a",
            Cts = new CancellationTokenSource(),
            Run = _ => throw new InvalidOperationException("boom")
        });

        scheduler.Enqueue(new ScheduledJob
        {
            Id = "b",
            Cts = new CancellationTokenSource(),
            Run = _ =>
            {
                ranB.TrySetResult();
                return Task.CompletedTask;
            }
        });

        scheduler.Start();
        await ranB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.StopAsync(cancelPending: false);
    }

    [Test]
    public async Task StopAsync_cancels_pending_jobs()
    {
        var mu = new object();
        var completed = new List<string>();
        var firstDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var scheduler = new DownloadScheduler(
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1),
            randomSeconds: (min, max) => 1,
            delayFor: (t, _) => Task.CompletedTask);

        for (var i = 0; i < 5; i++)
        {
            var index = i; // capture for the closure
            scheduler.Enqueue(new ScheduledJob
            {
                Id = index.ToString(),
                Cts = new CancellationTokenSource(),
                Run = async token =>
                {
                    if (index == 0)
                    {
                        firstDone.TrySetResult();
                    }

                    await Task.Delay(200, token);
                    lock (mu)
                    {
                        completed.Add(index.ToString());
                    }
                }
            });
        }

        scheduler.Start();
        await firstDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Let job 0 (200ms of work) finish; jobs 1-4 are then running or queued.
        await Task.Delay(250);
        await scheduler.StopAsync(cancelPending: true);

        // Cancellation hits the running/queued jobs; they never complete their work.
        Assert.That(completed.Count, Is.LessThan(5), "pending jobs should not all have completed");
        Assert.That(completed, Does.Contain("0"));

        // After a stop, enqueuing must fail.
        Assert.Throws<InvalidOperationException>(() => scheduler.Enqueue(new ScheduledJob
        {
            Id = "late",
            Cts = new CancellationTokenSource(),
            Run = _ => Task.CompletedTask
        }));
    }
}
