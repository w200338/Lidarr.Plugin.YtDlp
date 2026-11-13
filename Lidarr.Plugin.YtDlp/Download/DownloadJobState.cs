namespace Lidarr.Plugin.YtDlp.Download;

public enum YtDlpJobStatus
{
    Queued = 0,
    Downloading = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}

/// <summary>
/// Internal state of one queued/running/finished download. Deliberately free of
/// host types (the old Abstractions <c>StreamingDownloadItem</c> is gone) so it
/// stays unit-testable; the download client maps it to host
/// <see cref="NzbDrone.Core.Download.DownloadClientItem"/> values in GetItems().
///
/// All mutable members are written under <c>lock (this)</c> by the downloader.
/// </summary>
public sealed class DownloadJobState
{
    public required string Id { get; init; }
    public required YtDlpAlbumRef Album { get; init; }
    public required string OutputPath { get; init; }

    /// <summary>Host-parseable "<c>Artist - Album</c>" title used for import matching and the UI.</summary>
    public required string Title { get; init; }

    public CancellationTokenSource Cts { get; init; } = new();

    public YtDlpJobStatus Status { get; private set; } = YtDlpJobStatus.Queued;
    public double Progress { get; private set; }
    public string? CurrentTrack { get; private set; }

    /// <summary>Bytes of the file currently downloading (0 when idle/unknown).</summary>
    public long CurrentFileTotalBytes { get; private set; }

    /// <summary>Bytes remaining of the file currently downloading.</summary>
    public long CurrentFileRemainingBytes { get; private set; }

    /// <summary>Estimated remaining time of the file currently downloading.</summary>
    public TimeSpan? CurrentTrackEta { get; private set; }
    public bool Success { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;
    public DateTime StartedAt { get; private set; } = DateTime.MinValue;
    public DateTime LastUpdated { get; private set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; private set; }

    /// <summary>Set by the client's MarkItemAsImported; informational only.</summary>
    public bool Imported { get; private set; }

    public bool IsCompleted =>
        Status is YtDlpJobStatus.Completed or YtDlpJobStatus.Failed or YtDlpJobStatus.Cancelled;

    public void MarkDownloading()
    {
        lock (this)
        {
            Status = YtDlpJobStatus.Downloading;
            StartedAt = DateTime.UtcNow;
            LastUpdated = StartedAt;
            Progress = 0;
        }
    }

    public void UpdateProgress(double progress, string? currentTrack)
    {
        UpdateProgress(progress, currentTrack, 0, 0, null);
    }

    public void UpdateProgress(double progress, string? currentTrack, long fileTotalBytes, long fileRemainingBytes, TimeSpan? trackEta)
    {
        lock (this)
        {
            if (IsCompleted)
            {
                return;
            }

            Progress = progress;
            CurrentTrack = currentTrack;
            CurrentFileTotalBytes = fileTotalBytes;
            CurrentFileRemainingBytes = fileRemainingBytes;
            CurrentTrackEta = trackEta;
            LastUpdated = DateTime.UtcNow;
        }
    }

    public void MarkTerminal(YtDlpJobStatus status, bool success, string? error)
    {
        lock (this)
        {
            Status = status;
            Success = success;
            ErrorMessage = error;
            CompletedAt = DateTime.UtcNow;
            LastUpdated = CompletedAt.Value;

            if (status == YtDlpJobStatus.Completed)
            {
                Progress = 100;
            }
        }
    }

    public void MarkImported()
    {
        lock (this)
        {
            Imported = true;
        }
    }
}
