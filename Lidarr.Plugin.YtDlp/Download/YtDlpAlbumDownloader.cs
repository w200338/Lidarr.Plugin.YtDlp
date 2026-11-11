using System.Text.Json;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.YtDlp.ArgParsing;
using Lidarr.Plugin.YtDlp.Commands;
using Lidarr.Plugin.YtDlp.Runner;
using Lidarr.Plugin.YtDlp.Settings;

namespace Lidarr.Plugin.YtDlp.Download;

public enum YtDlpDownloadOutcome
{
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Runs the per-album yt-dlp pipeline: probe (metadata) → streaming download
/// with progress parsing. Mutates the <see cref="StreamingDownloadItem"/>
/// (progress, current track, terminal state) as it goes — the item is the
/// host-visible state, so all updates go through it directly.
/// </summary>
public sealed class YtDlpAlbumDownloader
{
    private const int StdErrTailLines = 15;
    private const int ErrorMaxChars = 2000;
    private static readonly TimeSpan ProgressThrottle = TimeSpan.FromMilliseconds(500);

    private readonly IYtDlpRunner _runner;
    private readonly YtDlpSettings _settings;
    private readonly YtDlpCommandBuilder _builder = new();

    public YtDlpAlbumDownloader(IYtDlpRunner runner, YtDlpSettings settings)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<YtDlpDownloadOutcome> DownloadAsync(
        YtDlpAlbumRef album,
        string outputPath,
        StreamingDownloadItem item,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);

        Directory.CreateDirectory(outputPath);

        item.Status = StreamingDownloadStatus.Downloading;
        item.StartedAt = DateTime.UtcNow;
        item.LastUpdated = DateTime.UtcNow;
        item.Progress = 0;

        var url = YtDlpAlbumId.ToUrl(album);
        var (probeOk, totalTracks) = await ProbeAlbumAsync(album, url, item, ct).ConfigureAwait(false);
        _ = probeOk; // probe is best-effort metadata; download proceeds regardless

        var options = new YtDlpDownloadOptions
        {
            Reencode = _settings.Reencode,
            UseSponsorBlock = _settings.UseSponsorBlock,
            SponsorBlockEndpoint = _settings.SponsorBlockApiEndpoint,
            // additionalArgs is validated at settings time; tolerate a parse miss here.
            ExtraArgs = ShellTokenizer.TryParse(_settings.AdditionalArgs, out var tokens, out _) ? tokens : Array.Empty<string>()
        };

        var args = album.Kind == YtDlpAlbumKind.SingleTrack
            ? _builder.BuildSingleDownloadArgs(url, Path.Combine(outputPath, "%(title)s.%(ext)s"), options)
            : _builder.BuildPlaylistDownloadArgs(url, outputPath,
                Path.Combine(outputPath, ".ytdlp-archive-" + album.Id + ".txt"), options);

        var parser = new YtDlpProgressParser();
        var stderrTail = new Queue<string>(StdErrTailLines);
        var lastUpdate = DateTime.MinValue;

        YtDlpRunResult result;
        try
        {
            result = await _runner.RunStreamingAsync(args, new YtDlpStreamHandlers(
                    _ => { },
                    line =>
                    {
                        lock (item)
                        {
                            stderrTail.Enqueue(line);
                            while (stderrTail.Count > StdErrTailLines)
                            {
                                stderrTail.Dequeue();
                            }

                            if (parser.Feed(line) && DateTime.UtcNow - lastUpdate >= ProgressThrottle)
                            {
                                item.Progress = parser.AlbumPercent(totalTracks);
                                item.CurrentTrack = parser.CurrentTrack;
                                item.LastUpdated = DateTime.UtcNow;
                                lastUpdate = item.LastUpdated;
                            }
                        }
                    }),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The runner only throws OCE on the cancellation path (see IYtDlpRunner
            // contract), so any OCE here means "canceled" — including escalations
            // and internal runner timeouts that surface as cancellation.
            MarkTerminal(item, StreamingDownloadStatus.Cancelled, success: false, "Download was canceled.");
            return YtDlpDownloadOutcome.Cancelled;
        }

        // Final un-throttled sync of parser state into the item.
        lock (item)
        {
            item.CurrentTrack = parser.CurrentTrack;
            item.Progress = parser.AlbumPercent(totalTracks);
            item.LastUpdated = DateTime.UtcNow;
        }

        if (result.ExitCode == 0)
        {
            MarkTerminal(item, StreamingDownloadStatus.Completed, success: true, null);
            return YtDlpDownloadOutcome.Completed;
        }

        var message = string.Join(Environment.NewLine, stderrTail).Trim();
        if (message.Length > ErrorMaxChars)
        {
            message = message[^ErrorMaxChars..];
        }

        MarkTerminal(item, StreamingDownloadStatus.Failed, success: false,
            $"yt-dlp exited with code {result.ExitCode}. {message}".Trim());
        return YtDlpDownloadOutcome.Failed;
    }

    /// <summary>
    /// Best-effort metadata probe via `--simulate -J` (flat for playlists so it
    /// stays fast). Populates item.Album and returns the total track count when
    /// known; failures are non-fatal (album stays null, count null).
    /// </summary>
    private async Task<(bool Ok, int? TotalTracks)> ProbeAlbumAsync(YtDlpAlbumRef album, string url, StreamingDownloadItem item, CancellationToken ct)
    {
        var probeArgs = album.Kind == YtDlpAlbumKind.SingleTrack
            ? _builder.BuildProbeArgs(url)
            : _builder.BuildPlaylistProbeArgs(url);

        var result = await _runner.RunBufferedAsync(probeArgs, ct).ConfigureAwait(false);
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return (false, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            var root = doc.RootElement;

            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            var channel = root.TryGetProperty("channel", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : root.TryGetProperty("uploader", out var u) ? u.GetString() : null;

            if (string.IsNullOrWhiteSpace(title))
            {
                return (false, null);
            }

            int trackCount;
            if (album.Kind == YtDlpAlbumKind.SingleTrack)
            {
                trackCount = 1;
            }
            else if (root.TryGetProperty("playlist_count", out var pc) && pc.TryGetInt32(out var count))
            {
                trackCount = count;
            }
            else
            {
                trackCount = 0;
            }

            item.Album = new StreamingAlbum
            {
                Id = album.Id,
                Title = title,
                Artist = new StreamingArtist { Id = channel ?? string.Empty, Name = channel ?? "Unknown Artist" },
                Type = album.Kind == YtDlpAlbumKind.SingleTrack
                    ? StreamingAlbumType.Single
                    : StreamingAlbumType.Album,
                TrackCount = trackCount
            };

            return (true, trackCount > 0 ? trackCount : null);
        }
        catch (JsonException)
        {
            return (false, null);
        }
    }

    private static void MarkTerminal(StreamingDownloadItem item, StreamingDownloadStatus status, bool success, string? error)
    {
        lock (item)
        {
            item.Status = status;
            item.IsCompleted = true;
            item.Success = success;
            item.CompletedAt = DateTime.UtcNow;
            item.LastUpdated = item.CompletedAt.Value;
            item.ErrorMessage = error;

            if (status == StreamingDownloadStatus.Completed)
            {
                item.Progress = 100;
            }
        }
    }
}
