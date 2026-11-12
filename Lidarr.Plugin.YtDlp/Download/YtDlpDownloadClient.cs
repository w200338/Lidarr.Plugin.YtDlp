using System.Collections.Concurrent;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.Validation;
using Lidarr.Plugin.YtDlp.Probing;
using Lidarr.Plugin.YtDlp.Queue;
using Lidarr.Plugin.YtDlp.Runner;
using Lidarr.Plugin.YtDlp.Settings;
using FluentValidation.Results;

namespace Lidarr.Plugin.YtDlp.Download;

/// <summary>
/// Native host <see cref="IDownloadClient"/> over yt-dlp.
///
/// Process-driven (one yt-dlp download at a time, spaced out), so it wraps the
/// existing single-flight <see cref="DownloadScheduler"/> instead of an HTTP
/// queue. The host sees the standard contract:
///
///  - <see cref="Download"/> enqueues a job and returns a stable <c>DownloadId</c>.
///  - <see cref="GetItems"/> maps jobs to <see cref="DownloadClientItem"/> values
///    (host DownloadItemStatus; <c>Title</c> is the host-parseable
///    "<c>Artist - Album</c>" string used for import).
///  - <see cref="RemoveItem"/> cancels (if running) and drops the job;
///    <c>deleteData</c> also deletes the output folder (host helper).
///  - <see cref="MarkItemAsImported"/> records the import (keeps the item
///    visible until the host removes it or the grace period expires).
///
/// Terminal jobs stay visible for a grace period so the host can read the
/// outcome (Completed → import; Failed → failure history).
/// </summary>
public class YtDlpDownloadClient : DownloadClientBase<YtDlpClientSettings>
{
    /// <summary>Bounded timeout for the settings Test() connection check.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Terminal items stay visible this long so the host can read the outcome.</summary>
    private static readonly TimeSpan TerminalGracePeriod = TimeSpan.FromMinutes(5);

    // Job state is shared per client definition id, NOT held on the instance:
    // the host resolves provider instances by concrete type and registers
    // concrete plugin types as TRANSIENT, so every host call may hit a fresh
    // instance. See SharedClientState for the full rationale.
    private SharedClientState? _testState;

    private SharedClientState State
    {
        get
        {
            var shared = SharedClientStates.For(Definition?.Id ?? 0);
            if (shared is not null)
            {
                return shared;
            }

            return _testState ??= new SharedClientState(0);
        }
    }

    public YtDlpDownloadClient(
        IConfigService configService,
        IDiskProvider diskProvider,
        IRemotePathMappingService remotePathMappingService,
        ILocalizationService localizationService,
        Logger logger)
        : base(configService, diskProvider, remotePathMappingService, localizationService, logger)
    {
    }

    public override string Name => "YouTube (yt-dlp)";

    public override string Protocol => nameof(YtDlpDownloadProtocol);

    /// <summary>Test seam: replace the scheduler factory (spacing, execution) in unit tests.</summary>
    internal Func<TimeSpan, TimeSpan, DownloadScheduler>? SchedulerFactoryOverride { get; set; }

    private DownloadScheduler CreateScheduler()
    {
        return SchedulerFactoryOverride is not null
            ? SchedulerFactoryOverride(
                TimeSpan.FromSeconds(Settings.MinDownloadDelaySeconds),
                TimeSpan.FromSeconds(Settings.MaxDownloadDelaySeconds))
            : new DownloadScheduler(
                TimeSpan.FromSeconds(Settings.MinDownloadDelaySeconds),
                TimeSpan.FromSeconds(Settings.MaxDownloadDelaySeconds));
    }

    public override Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer)
    {
        ArgumentNullException.ThrowIfNull(remoteAlbum);

        var release = remoteAlbum.Release ?? throw new ReleaseDownloadException(null!, "Release is null.");

        if (!YtDlpAlbumId.TryFromUrl(release.DownloadUrl, out var album))
        {
            throw new ReleaseDownloadException(release, "Unsupported YouTube download URL: {0}", release.DownloadUrl);
        }

        var artistName = string.IsNullOrWhiteSpace(remoteAlbum.Artist?.Name)
            ? (string.IsNullOrWhiteSpace(release.Artist) ? "Unknown Artist" : release.Artist)
            : remoteAlbum.Artist.Name;

        var albumTitle = ResolveAlbumTitle(remoteAlbum, release);
        var title = $"{artistName} - {albumTitle}";

        var directory = Path.Combine(Settings.DownloadPath, SanitizeDirectoryName(title));
        Directory.CreateDirectory(directory);

        return Task.FromResult(Enqueue(album, directory, title));
    }

    public override IEnumerable<DownloadClientItem> GetItems()
    {
        var now = DateTime.UtcNow;
        var jobs = State.Jobs;
        var items = jobs.Values
            .Where(j => !j.IsCompleted
                        || (j.CompletedAt.HasValue && now - j.CompletedAt.Value < TerminalGracePeriod))
            .Select(MapItem)
            .ToList();

        PruneExpiredTerminalJobs(now);
        return items;
    }

    public override void RemoveItem(DownloadClientItem item, bool deleteData)
    {
        if (item == null)
        {
            return;
        }

        if (State.Jobs.TryRemove(item.DownloadId, out var job))
        {
            try
            {
                job.Cts.Cancel(); // no-op for terminal jobs
            }
            catch (ObjectDisposedException)
            {
                // already disposed
            }

            if (deleteData)
            {
                DeleteItemData(item);
            }
        }
    }

    public override DownloadClientInfo GetStatus()
    {
        return new DownloadClientInfo
        {
            IsLocalhost = false,
            OutputRootFolders = new List<OsPath>
            {
                new(string.IsNullOrWhiteSpace(Settings.DownloadPath) ? "." : Settings.DownloadPath)
            }
        };
    }

    public override void MarkItemAsImported(DownloadClientItem item)
    {
        if (item == null)
        {
            return;
        }

        if (State.Jobs.TryGetValue(item.DownloadId, out var job))
        {
            job.MarkImported();
        }
    }

    protected override void Test(List<ValidationFailure> failures)
    {
        if (string.IsNullOrWhiteSpace(Settings.DownloadPath))
        {
            failures.Add(new NzbDroneValidationFailure("DownloadPath", "Download folder is required."));
            return;
        }

        // Best effort: create the root folder if it does not exist yet so the
        // test (and first download) can succeed on a fresh setup.
        try
        {
            Directory.CreateDirectory(Settings.DownloadPath);
        }
        catch (Exception ex)
        {
            failures.Add(new NzbDroneValidationFailure("DownloadPath", "Could not create download folder: " + ex.Message));
            return;
        }

        var folderFailure = TestFolder(Settings.DownloadPath, "DownloadPath");
        if (folderFailure != null)
        {
            failures.Add(folderFailure);
        }

        using var cts = new CancellationTokenSource(TestTimeout);
        var result = YtDlpConnectionTest.Run(Settings, cts.Token);

        foreach (var error in result.Errors)
        {
            failures.Add(new ValidationFailure(error.Property, error.Message));
        }

        foreach (var warning in result.Warnings)
        {
            _logger.Warn("[{0}] {1}", Name, warning);
        }
    }

    // ----- internals -----

    private string Enqueue(YtDlpAlbumRef album, string outputPath, string title)
    {
        var normalizedAlbum = album.ToString();
        var shared = State;

        lock (shared.EnqueueLock)
        {
            // Duplicate enqueue of the same album+path while not terminal →
            // return the existing id (no double downloads).
            foreach (var job in shared.Jobs.Values)
            {
                if (job.IsCompleted)
                {
                    continue;
                }

                if (string.Equals(album.ToString(), normalizedAlbum, StringComparison.Ordinal)
                    && string.Equals(job.OutputPath, outputPath, StringComparison.Ordinal))
                {
                    return job.Id;
                }
            }

            var state = new DownloadJobState
            {
                Id = Guid.NewGuid().ToString("N"),
                Album = album,
                OutputPath = outputPath,
                Title = title
            };
            shared.Jobs[state.Id] = state;

            var scheduler = shared.GetScheduler(CreateScheduler);
            scheduler.Enqueue(new ScheduledJob
            {
                Id = state.Id,
                Cts = state.Cts,
                Run = token => RunJobAsync(state, token)
            });
            scheduler.Start();

            return state.Id;
        }
    }

    private async Task RunJobAsync(DownloadJobState state, CancellationToken token)
    {
        var runner = YtDlpRunners.For(Settings.YtDlpPath);
        var downloader = new YtDlpAlbumDownloader(runner, Settings);

        try
        {
            await downloader.DownloadAsync(state.Album, state.OutputPath, state, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            state.MarkTerminal(YtDlpJobStatus.Cancelled, success: false, "Download was canceled.");
        }
        catch (Exception ex)
        {
            // Defense in depth: the downloader maps failures to state; anything
            // escaping here is unexpected. Fail the job, keep the queue alive.
            state.MarkTerminal(YtDlpJobStatus.Failed, success: false, "Unexpected error: " + ex.Message);
            _logger.Error(ex, "[{0}] Job {1} failed unexpectedly", Name, state.Id);
        }
    }

    private DownloadClientItem MapItem(DownloadJobState job)
    {
        return new DownloadClientItem
        {
            DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false),
            DownloadId = job.Id,
            Category = string.Empty,
            Title = job.Title,
            TotalSize = 0,
            RemainingSize = 0,
            OutputPath = new OsPath(job.OutputPath),
            Message = job.Status switch
            {
                YtDlpJobStatus.Queued => "Queued",
                YtDlpJobStatus.Downloading => job.CurrentTrack is null ? "Downloading" : $"Downloading: {job.CurrentTrack}",
                YtDlpJobStatus.Completed => "Completed",
                YtDlpJobStatus.Cancelled => "Canceled",
                _ => job.ErrorMessage ?? "Failed"
            },
            Status = job.Status switch
            {
                YtDlpJobStatus.Queued => DownloadItemStatus.Queued,
                YtDlpJobStatus.Downloading => DownloadItemStatus.Downloading,
                YtDlpJobStatus.Completed => DownloadItemStatus.Completed,
                _ => DownloadItemStatus.Failed
            },
            IsEncrypted = false,
            CanMoveFiles = false,
            CanBeRemoved = true,
            Removed = false
        };
    }

    /// <summary>
    /// Album title for the output folder / item title: prefer what the indexer
    /// recorded on the release, then the host's matched album, then parse the
    /// composed "<c>Artist - Album</c>" title.
    /// </summary>
    internal static string ResolveAlbumTitle(RemoteAlbum remoteAlbum, ReleaseInfo release)
    {
        if (!string.IsNullOrWhiteSpace(release.Album))
        {
            return release.Album;
        }

        var matched = remoteAlbum.Albums?.FirstOrDefault();
        if (matched is not null && !string.IsNullOrWhiteSpace(matched.Title))
        {
            return matched.Title;
        }

        var title = release.Title ?? string.Empty;
        var dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            return title[(dash + 3)..].Trim();
        }

        return title.Length == 0 ? "Unknown Album" : title;
    }

    internal static string SanitizeDirectoryName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, ' ');
        }

        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ").Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(name) ? "download" : name;
    }

    private void PruneExpiredTerminalJobs(DateTime now)
    {
        var jobs = State.Jobs;
        foreach (var job in jobs.Values)
        {
            if (job.IsCompleted
                && job.CompletedAt.HasValue
                && now - job.CompletedAt.Value >= TerminalGracePeriod)
            {
                jobs.TryRemove(job.Id, out _);
            }
        }
    }
}
