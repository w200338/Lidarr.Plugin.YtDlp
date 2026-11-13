using System.Text.Json;
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
/// with progress parsing. Mutates the <see cref="DownloadJobState"/> (progress,
/// current track, terminal state) as it goes — the state is the
/// client-visible state, so all updates go through it directly.
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
        DownloadJobState state,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);

        Directory.CreateDirectory(outputPath);

        state.MarkDownloading();

        var url = YtDlpAlbumId.ToUrl(album);
        var (probeOk, totalTracks) = await ProbeAlbumAsync(album, url, ct).ConfigureAwait(false);
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
        var lineTail = new Queue<string>(StdErrTailLines);
        var lastUpdate = DateTime.MinValue;

        // Current yt-dlp versions write everything — including the [download]
        // progress lines — to STDOUT, while older builds used stderr. Feed the
        // parser from both streams (the regexes are prefix-anchored, so
        // cross-stream lines are harmless) and keep a tail of both for error
        // messages.
        void HandleLine(string line)
        {
            lineTail.Enqueue(line);
            while (lineTail.Count > StdErrTailLines)
            {
                lineTail.Dequeue();
            }

            if (parser.Feed(line) && DateTime.UtcNow - lastUpdate >= ProgressThrottle)
            {
                state.UpdateProgress(parser.AlbumPercent(totalTracks), parser.CurrentTrack,
                    parser.CurrentFileTotalBytes, parser.CurrentFileRemainingBytes, parser.CurrentTrackEta);
                lastUpdate = DateTime.UtcNow;
            }
        }

        YtDlpRunResult result;
        try
        {
            result = await _runner.RunStreamingAsync(args, new YtDlpStreamHandlers(
                    line => HandleLine(line),
                    line => HandleLine(line)),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The runner only throws OCE on the cancellation path (see IYtDlpRunner
            // contract), so any OCE here means "canceled" — including escalations
            // and internal runner timeouts that surface as cancellation.
            state.MarkTerminal(YtDlpJobStatus.Cancelled, success: false, "Download was canceled.");
            return YtDlpDownloadOutcome.Cancelled;
        }

        // Final un-throttled sync of parser state into the state object.
        state.UpdateProgress(parser.AlbumPercent(totalTracks), parser.CurrentTrack,
            parser.CurrentFileTotalBytes, parser.CurrentFileRemainingBytes, parser.CurrentTrackEta);

        if (result.ExitCode == 0)
        {
            state.MarkTerminal(YtDlpJobStatus.Completed, success: true, null);
            return YtDlpDownloadOutcome.Completed;
        }

        var message = string.Join(Environment.NewLine, lineTail).Trim();
        if (message.Length > ErrorMaxChars)
        {
            message = message[^ErrorMaxChars..];
        }

        state.MarkTerminal(YtDlpJobStatus.Failed, success: false,
            $"yt-dlp exited with code {result.ExitCode}. {message}".Trim());
        return YtDlpDownloadOutcome.Failed;
    }

    /// <summary>
    /// Best-effort metadata probe via `--simulate -J` (flat for playlists so it
    /// stays fast). Returns the total track count when known; failures are
    /// non-fatal (count null).
    /// </summary>
    private async Task<(bool Ok, int? TotalTracks)> ProbeAlbumAsync(YtDlpAlbumRef album, string url, CancellationToken ct)
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

            var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
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

            return (true, trackCount > 0 ? trackCount : null);
        }
        catch (JsonException)
        {
            return (false, null);
        }
    }
}
