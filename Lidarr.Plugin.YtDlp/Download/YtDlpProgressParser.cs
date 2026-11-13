using System.Text.RegularExpressions;

namespace Lidarr.Plugin.YtDlp.Download;

/// <summary>
/// Stateful parser for yt-dlp progress output. Tracks, per album:
/// how many file downloads started/completed, the current track's percent,
/// the current file's total/remaining bytes and ETA, so the client can
/// report a smooth 0-100 progress bar (host queue: 100 - sizeleft/size).
///
/// The caller feeds lines from BOTH stdout and stderr: current yt-dlp
/// versions write everything (including [download] progress) to stdout,
/// while older builds used stderr. The regexes are prefix-anchored, so
/// cross-stream lines are harmless.
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

    /// <summary>
    /// The "of ~ 12.50MiB" (or "of 12.50MiB") part of a progress line: the
    /// total size of the file currently downloading.
    /// </summary>
    private static readonly Regex SizeRegex =
        new(@"^\[download\]\s+\d{1,3}(?:\.\d+)?%\s+of\s+(?:~\s*)?(?<value>[\d.]+)\s*(?<unit>[A-Za-z]{1,3})", RegexOptions.Compiled);

    /// <summary>The "ETA 00:05" / "ETA 1:02:03" part of a progress line.</summary>
    private static readonly Regex EtaRegex =
        new(@"ETA\s+(?<h>\d{1,2}:)?(?<m>\d{1,2}):(?<s>\d{2})", RegexOptions.Compiled);

    private static readonly Dictionary<string, double> ByteUnitFactors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["B"] = 1,
        ["KB"] = 1000,
        ["KIB"] = 1024,
        ["MB"] = 1000d * 1000,
        ["MIB"] = 1024d * 1024,
        ["GB"] = 1000d * 1000 * 1000,
        ["GIB"] = 1024d * 1024 * 1024,
    };

    /// <summary>Number of distinct files whose download started.</summary>
    public int FilesStarted { get; private set; }

    /// <summary>Number of distinct files whose download reached 100%.</summary>
    public int FilesCompleted { get; private set; }

    /// <summary>Percent (0-100) of the file currently downloading.</summary>
    public double CurrentTrackPercent { get; private set; }

    /// <summary>Human-readable name of the file currently being processed (stem).</summary>
    public string? CurrentTrack { get; private set; }

    /// <summary>Total size in bytes of the file currently downloading (0 until known).</summary>
    public long CurrentFileTotalBytes { get; private set; }

    /// <summary>Estimated remaining time for the file currently downloading.</summary>
    public TimeSpan? CurrentTrackEta { get; private set; }

    /// <summary>
    /// Remaining bytes of the file currently downloading, derived from its
    /// total size and current percent. 0 when the file size is unknown.
    /// </summary>
    public long CurrentFileRemainingBytes
    {
        get
        {
            if (CurrentFileTotalBytes <= 0)
            {
                return 0;
            }

            var done = Math.Clamp(CurrentTrackPercent, 0, 100) / 100.0;
            return (long)Math.Round(CurrentFileTotalBytes * (1.0 - done));
        }
    }

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
        var percent = 0.0;
        var hasPercent = percentMatch.Success && double.TryParse(percentMatch.Groups[1].Value,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out percent);
        if (hasPercent)
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
                    CurrentTrackEta = null;
                }
            }

            // A new file starts when percent drops back down.
            if (percent < CurrentTrackPercent && FilesCompleted >= FilesStarted)
            {
                FilesStarted++;
                CurrentTrackPercent = percent;
                CurrentFileTotalBytes = 0;
                changed = true;
            }
        }

        var sizeMatch = SizeRegex.Match(line);
        if (sizeMatch.Success &&
            double.TryParse(sizeMatch.Groups["value"].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var sizeValue) &&
            ByteUnitFactors.TryGetValue(sizeMatch.Groups["unit"].Value, out var factor))
        {
            var total = (long)Math.Round(sizeValue * factor);
            if (total > 0 && total != CurrentFileTotalBytes)
            {
                CurrentFileTotalBytes = total;
                changed = true;
            }
        }

        var etaMatch = EtaRegex.Match(line);
        if (hasPercent)
        {
            if (etaMatch.Success)
            {
                var hours = etaMatch.Groups["h"].Success
                    ? int.Parse(etaMatch.Groups["h"].Value.TrimEnd(':'), System.Globalization.CultureInfo.InvariantCulture)
                    : 0;
                var minutes = int.Parse(etaMatch.Groups["m"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var seconds = int.Parse(etaMatch.Groups["s"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var eta = new TimeSpan(hours, minutes, seconds);
                if (eta != CurrentTrackEta)
                {
                    CurrentTrackEta = eta;
                    changed = true;
                }
            }
            else if (percent >= 100 && CurrentTrackEta is not null)
            {
                CurrentTrackEta = null;
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
