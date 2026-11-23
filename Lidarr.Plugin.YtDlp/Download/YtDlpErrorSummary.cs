using System.Text.RegularExpressions;

namespace Lidarr.Plugin.YtDlp.Download;

/// <summary>
/// A single yt-dlp error extracted from its output: an optional video id
/// (e.g. <c>Knt8RVzBiuU</c> from <c>[youtube] Knt8RVzBiuU:</c>) plus the
/// human-readable message.
/// </summary>
/// <param name="VideoId">The video id when the error line carried one
/// (e.g. <c>[youtube] Knt8RVzBiuU:</c>); <c>null</c> when the error is not
/// tied to a specific video (e.g. <c>ERROR: RequestBlocked: bot check</c>).</param>
/// <param name="Message">The error message text after the optional
/// extractor/video-id prefix.</param>
public sealed record YtDlpErrorItem(string? VideoId, string Message);

/// <summary>
/// Aggregate of the errors yt-dlp reported for one failed or partial download.
/// <see cref="TotalErrors"/> counts every distinct error; <see cref="Errors"/>
/// holds at most the first <c>maxListed</c> of them (first-seen order) for
/// display.
/// </summary>
/// <param name="TotalErrors">Number of distinct errors after deduplication.</param>
/// <param name="Errors">At most the first <c>maxListed</c> distinct errors, in
/// first-seen order.</param>
public sealed record YtDlpErrorSummary(int TotalErrors, IReadOnlyList<YtDlpErrorItem> Errors)
{
    // Anchored at the start of the line so lines that merely contain "ERROR:"
    // later in the text are ignored. Optional "[extractor] <id>:" prefix
    // captures the extractor name (any chars except "]") and the video id up to
    // the first ":"; the rest is the message.
    private static readonly Regex ErrorLineRegex = new(
        @"^ERROR:\s*(?:\[[^\]]+\]\s*([^:\s][^:]*):\s*)?(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses yt-dlp error lines into a <see cref="YtDlpErrorSummary"/>.
    ///
    /// Handles both shapes:
    ///   <c>ERROR: [youtube] Knt8RVzBiuU: Video unavailable.</c>
    ///   <c>ERROR: RequestBlocked: bot check</c>
    /// Identical (VideoId, Message) pairs are deduplicated (first-seen wins);
    /// <see cref="TotalErrors"/> counts all distinct errors while <see cref="Errors"/>
    /// retains at most <paramref name="maxListed"/> of them.
    /// </summary>
    /// <param name="lines">yt-dlp output lines (from both stdout and stderr).</param>
    /// <param name="maxListed">Maximum number of distinct errors to retain in
    /// <see cref="Errors"/>; <see cref="TotalErrors"/> still counts every one.</param>
    /// <returns>A summary counting every distinct error and listing at most
    /// <paramref name="maxListed"/> of them, in first-seen order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxListed"/> is negative.</exception>
    public static YtDlpErrorSummary Parse(IEnumerable<string> lines, int maxListed = 3)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (maxListed < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxListed), maxListed, "maxListed must be >= 0.");
        }

        // YtDlpErrorItem is a record, so value equality dedupes identical
        // (VideoId, Message) pairs without a custom key.
        var seen = new HashSet<YtDlpErrorItem>();
        var listed = new List<YtDlpErrorItem>();

        foreach (var raw in lines)
        {
            if (raw is null)
            {
                continue;
            }

            var match = ErrorLineRegex.Match(raw);
            if (!match.Success)
            {
                continue;
            }

            string? videoId = match.Groups[1].Success ? match.Groups[1].Value : null;
            var item = new YtDlpErrorItem(videoId, match.Groups[2].Value);

            if (!seen.Add(item))
            {
                continue; // identical (VideoId, Message) pair already counted
            }

            if (listed.Count < maxListed)
            {
                listed.Add(item);
            }
        }

        return new YtDlpErrorSummary(seen.Count, listed);
    }
}
