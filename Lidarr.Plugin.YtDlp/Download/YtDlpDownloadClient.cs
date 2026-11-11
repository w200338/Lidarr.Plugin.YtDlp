using System.Collections.Concurrent;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.YtDlp.Queue;
using Lidarr.Plugin.YtDlp.Runner;
using Lidarr.Plugin.YtDlp.Settings;

namespace Lidarr.Plugin.YtDlp.Download;

/// <summary>
/// Lidarr <see cref="IDownloadClient"/> over yt-dlp.
///
/// Design note (deliberate, per Phase 3 plan): does NOT derive from
/// <c>BaseStreamingDownloadClient</c> — that base models an HTTP-streaming
/// provider (Tidal/MusicBrainz-shaped). This client is process-driven: one
/// yt-dlp download at a time, spaced out. We reuse the base's *ideas*
/// (item lifecycle, remove/cancel semantics) directly, which keeps the class
/// small and every behavior unit-testable.
///
/// Lifecycle:
///  Enqueue → item(Queued) → scheduler spacing → item(Downloading) →
///  Completed | Failed | Cancelled (terminal, kept visible for a grace period
///  so the host can inspect the result, then pruned).
/// </summary>
public sealed class YtDlpDownloadClient : IDownloadClient
{
    private sealed class Job
    {
        public required StreamingDownloadItem Item { get; init; }
        public required CancellationTokenSource Cts { get; init; }
    }

    /// <summary>Terminal items stay visible this long so the host can read the outcome.</summary>
    private static readonly TimeSpan TerminalGracePeriod = TimeSpan.FromMinutes(5);

    private readonly YtDlpSettings _settings;
    private readonly IYtDlpRunner _runner;
    private readonly YtDlpAlbumDownloader _downloader;
    private readonly DownloadScheduler _scheduler;
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly object _enqueueLock = new();

    public YtDlpDownloadClient(YtDlpSettings settings, IYtDlpRunner runner)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _downloader = new YtDlpAlbumDownloader(runner, settings);
        _scheduler = new DownloadScheduler(
            TimeSpan.FromSeconds(settings.MinDownloadDelaySeconds),
            TimeSpan.FromSeconds(settings.MaxDownloadDelaySeconds));
    }

    public ValueTask<PluginValidationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Structural check only; full external verification (yt-dlp --version,
        // ffmpeg, SponsorBlock reachability) is the Phase 5 connection test.
        if (string.IsNullOrWhiteSpace(_settings.YtDlpPath))
        {
            return ValueTask.FromResult(
                PluginValidationResult.Failure(new[] { "ytDlpPath is not configured." }));
        }

        if (_settings.MaxDownloadDelaySeconds < _settings.MinDownloadDelaySeconds)
        {
            return ValueTask.FromResult(
                PluginValidationResult.Failure(new[] { "maxDownloadDelaySeconds must be >= minDownloadDelaySeconds." }));
        }

        return ValueTask.FromResult(PluginValidationResult.Success());
    }

    public ValueTask<string> EnqueueAlbumDownloadAsync(string albumId, string outputPath, CancellationToken cancellationToken = default)
    {
        if (!YtDlpAlbumId.TryParse(albumId, out var album))
        {
            throw new ArgumentException(
                $"Invalid album id '{albumId}'. Expected 'yt:<videoId>' or 'playlist:<playlistId>'.",
                nameof(albumId));
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("outputPath is required.", nameof(outputPath));
        }

        var normalizedId = album.ToString();

        lock (_enqueueLock)
        {
            // Duplicate enqueue of the same album+path while not terminal →
            // return the existing id (documented choice: no double downloads).
            foreach (var job in _jobs.Values)
            {
                if (job.Item.IsCompleted)
                {
                    continue;
                }

                if (string.Equals(job.Item.AlbumId, normalizedId, StringComparison.Ordinal)
                    && string.Equals(job.Item.OutputPath, outputPath, StringComparison.Ordinal))
                {
                    return ValueTask.FromResult(job.Item.Id!);
                }
            }

            var item = new StreamingDownloadItem
            {
                Id = Guid.NewGuid().ToString("N"),
                AlbumId = normalizedId,
                OutputPath = outputPath,
                Status = StreamingDownloadStatus.Queued,
                Progress = 0,
                StartedAt = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow,
                CancellationToken = new CancellationTokenSource()
            };

            var newJob = new Job { Item = item, Cts = item.CancellationToken! };
            _jobs[item.Id] = newJob;

            _scheduler.Enqueue(new ScheduledJob
            {
                Id = item.Id,
                Cts = newJob.Cts,
                Run = token => RunJobAsync(newJob, album, outputPath, token)
            });
            _scheduler.Start();

            return ValueTask.FromResult(item.Id);
        }
    }

    public ValueTask<bool> RemoveDownloadAsync(string downloadId, bool deleteData = false, CancellationToken cancellationToken = default)
    {
        if (!_jobs.TryRemove(downloadId, out var job))
        {
            return ValueTask.FromResult(false);
        }

        try
        {
            job.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }

        if (deleteData && !string.IsNullOrWhiteSpace(job.Item.OutputPath))
        {
            DeleteOutputBestEffort(job.Item.OutputPath);
        }

        return ValueTask.FromResult(true);
    }

    public ValueTask<IReadOnlyList<StreamingDownloadItem>> GetActiveDownloadsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var snapshot = _jobs.Values
            .Where(j => !j.Item.IsCompleted
                        || (j.Item.CompletedAt != null && now - j.Item.CompletedAt.Value < TerminalGracePeriod))
            .Select(j => j.Item)
            .ToList();

        PruneExpiredTerminalJobs(now);
        return ValueTask.FromResult<IReadOnlyList<StreamingDownloadItem>>(snapshot);
    }

    public ValueTask<StreamingDownloadItem?> GetDownloadAsync(string downloadId, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(_jobs.TryGetValue(downloadId, out var job) ? job.Item : null);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var job in _jobs.Values)
        {
            try
            {
                job.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // already disposed
            }
        }

        _jobs.Clear();
        await _scheduler.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RunJobAsync(Job job, YtDlpAlbumRef album, string outputPath, CancellationToken token)
    {
        try
        {
            await _downloader.DownloadAsync(album, outputPath, job.Item, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            MarkCancelled(job.Item);
        }
        catch (Exception ex)
        {
            // Defense in depth: the downloader maps failures to item state;
            // anything escaping here is unexpected. Fail the job, keep queue alive.
            MarkFailed(job.Item, $"Unexpected error: {ex.Message}");
        }
    }

    private static void MarkCancelled(StreamingDownloadItem item)
    {
        lock (item)
        {
            item.Status = StreamingDownloadStatus.Cancelled;
            item.IsCompleted = true;
            item.Success = false;
            item.CompletedAt = DateTime.UtcNow;
            item.LastUpdated = item.CompletedAt.Value;
            item.ErrorMessage ??= "Download was canceled.";
        }
    }

    private static void MarkFailed(StreamingDownloadItem item, string error)
    {
        lock (item)
        {
            item.Status = StreamingDownloadStatus.Failed;
            item.IsCompleted = true;
            item.Success = false;
            item.CompletedAt = DateTime.UtcNow;
            item.LastUpdated = item.CompletedAt.Value;
            item.ErrorMessage = error;
        }
    }

    private void PruneExpiredTerminalJobs(DateTime now)
    {
        foreach (var job in _jobs.Values)
        {
            if (job.Item.IsCompleted
                && job.Item.CompletedAt != null
                && now - job.Item.CompletedAt.Value >= TerminalGracePeriod)
            {
                _jobs.TryRemove(job.Item.Id!, out _);
            }
        }
    }

    private static void DeleteOutputBestEffort(string outputPath)
    {
        try
        {
            if (Directory.Exists(outputPath))
            {
                Directory.Delete(outputPath, recursive: true);
            }
        }
        catch
        {
            // Best effort — host may already own/be reading the path.
        }
    }
}
