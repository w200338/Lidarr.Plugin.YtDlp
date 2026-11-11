using System.Text.RegularExpressions;

namespace Lidarr.Plugin.YtDlp.Download;

/// <summary>
/// Stateful parser for yt-dlp progress output (stderr). Tracks, per album:
/// how many file downloads started/completed and the current track's percent,
/// so the client can report a smooth 0-100 album-level progress value.
///
/// Line shapes consumed (yt-dlp, --newline mode):
///   [download]   5.3% of ~ 12.50MiB at 2.50MiB/s ETA 00:05
///   [download] 100% of  10.00MiB in 00:04 at 2.50MiB/s
///   [download] Destination: /data/album/01 - Song Name.webm
///   [ExtractAudio] Destination: /data/album/01 - Song Name.mp3
///   [ExtractAudio] Converting audio to mp3
///   [Merger] Merging formats into "/data/album/01 - Song Name.webm"
///   ERROR: [youtube] ...
///
/// Everything else (warnings, per-format resolution lines, ...) is ignored.
/// </summary>
public sealed class YtDlpProgressParser
{
    private static readonly Regex PercentRegex =
        new(@"^\[download\]\s+(\d{1,3}(?:\.\d+)?)%", RegexOptions.Compiled);

    private static readonly Regex DestinationRegex =
        new(@"^\[(?:download|ExtractAudio|Merger)\]\s+Destination:\s+(.+?)\s*$", RegexOptions.Compiled);

    /// <summary>Number of distinct files whose download started.</summary>
    public int FilesStarted { get; private set; }

    /// <summary>Number of distinct files whose download reached 100%.</summary>
    public int FilesCompleted { get; private set; }

    /// <summary>Percent (0-100) of the file currently downloading.</summary>
    public double CurrentTrackPercent { get; private set; }

    /// <summary>Human-readable name of the file currently being processed (stem).</summary>
    public string? CurrentTrack { get; private set; }

    /// <summary>
    /// Album-level progress 0-100. With <paramref name="totalTracks"/> known:
    /// (completed + currentPercent/100) / total, clamped. Without a total:
    /// the current track's percent.
    /// </summary>
    public double AlbumPercent(int? totalTracks)
    {
        // Fraction of the album done, in track units:
        //  - current track at 100% counts as the completed integer tracks only
        //    (FilesCompleted), so we never double-count a finished track;
        //  - in-progress track contributes its partial percent.
        double fraction = CurrentTrackPercent >= 100
            ? FilesCompleted
            : FilesCompleted + Math.Clamp(CurrentTrackPercent, 0, 100) / 100.0;

        if (totalTracks is > 0)
        {
            return Math.Clamp(fraction / totalTracks.Value * 100.0, 0, 100);
        }

        return Math.Clamp(CurrentTrackPercent, 0, 100);
    }

    /// <summary>Feeds one output line; returns true if any state changed.</summary>
    public bool Feed(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        var changed = false;

        var percentMatch = PercentRegex.Match(line);
        if (percentMatch.Success && double.TryParse(percentMatch.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var percent))
        {
            if (FilesStarted == 0)
            {
                // First percent line of the album before any Destination line
                // (yt-dlp prints Destination first, but be defensive).
                FilesStarted = 1;
                changed = true;
            }

            if (percent > CurrentTrackPercent || (percent >= 100 && FilesCompleted < FilesStarted))
            {
                CurrentTrackPercent = percent;
                changed = true;

                if (percent >= 100 && FilesCompleted < FilesStarted)
                {
                    FilesCompleted++;
                }
            }

            // A new file starts when percent drops back down.
            if (percent < CurrentTrackPercent && FilesCompleted >= FilesStarted)
            {
                FilesStarted++;
                CurrentTrackPercent = percent;
                changed = true;
            }
        }

        var destMatch = DestinationRegex.Match(line);
        if (destMatch.Success)
        {
            var path = destMatch.Groups[1].Value.Trim().Trim('"');
            var stem = Path.GetFileNameWithoutExtension(path);
            if (!string.Equals(stem, CurrentTrack, StringComparison.Ordinal))
            {
                CurrentTrack = stem;
                changed = true;
            }

            // A Destination line marks the start of a new file if the previous one
            // was not tracked yet. (yt-dlp prints it before the first percent line.)
            if (FilesStarted == 0)
            {
                FilesStarted = 1;
                changed = true;
            }
        }

        return changed;
    }
}
