using Lidarr.Plugin.YtDlp.Settings;

namespace Lidarr.Plugin.YtDlp.Commands;

/// <summary>
/// Pure builder that turns (intent + settings) into exact yt-dlp argument lists.
/// No process interaction — 100% unit-testable. The argument list is handed to
/// CliWrap's <c>WithArguments(IEnumerable&lt;string&gt;)</c> which handles quoting/escaping,
/// so values are emitted as raw separate tokens (never pre-quoted).
/// </summary>
public sealed class YtDlpCommandBuilder
{
    /// <summary>
    /// Pipe-delimited print template for search/album listing.
    /// Field order is the parse contract: id | url | title | channel | duration | type.
    /// (The parser splits from both ends because title may contain '|'.)
    /// </summary>
    public const string PrintTemplate = "%(id)s|%(url)s|%(title)s|%(channel)s|%(duration)s|%(type)s";

    public IReadOnlyList<string> BuildVersionArgs() =>
    [
        "--version"
    ];

    /// <summary>YouTube video search (ytsearchN: prefix) as flat print lines.</summary>
    public IReadOnlyList<string> BuildSearchArgs(string query, int limit) =>
        BuildFlatPrintArgs($"ytsearch{ClampLimit(limit)}:{query}");

    /// <summary>YouTube Music album search (albums section of the music search page).</summary>
    public IReadOnlyList<string> BuildAlbumSearchArgs(string query, int limit)
    {
        var url = $"https://music.youtube.com/search?q={Uri.EscapeDataString(query)}#albums";
        return BuildFlatPrintArgs(url);
    }

    private static List<string> BuildFlatPrintArgs(string searchTarget) =>
    [
        "--ignore-config",
        "--no-warnings",
        "--no-color",
        "--flat-playlist",
        "--print",
        PrintTemplate,
        searchTarget
    ];

    /// <summary>
    /// Downloads a single video (watch URL) as one audio file.
    /// <paramref name="outputTemplate"/> is the yt-dlp -o template (usually
    /// "<album dir>/%(title)s.%(ext)s").
    /// </summary>
    public IReadOnlyList<string> BuildSingleDownloadArgs(
        string watchUrl,
        string outputTemplate,
        YtDlpDownloadOptions options)
    {
        var args = CommonArgs();
        args.AddRange(BuildFormatArgs(options.Reencode));
        args.Add("--no-playlist");
        if (options.UseSponsorBlock)
        {
            args.AddRange(SponsorBlockArgs(options.SponsorBlockEndpoint));
        }

        args.Add("-o");
        args.Add(outputTemplate);
        args.AddRange(PolitenessArgs());
        args.AddRange(options.ExtraArgs);
        args.Add("--");
        args.Add(watchUrl);
        return args;
    }

    /// <summary>
    /// Downloads a whole playlist into <paramref name="outputDir"/> with
    /// position-prefixed file names; the download archive + break-on-existing make
    /// re-runs of an interrupted/queued playlist idempotent.
    /// </summary>
    public IReadOnlyList<string> BuildPlaylistDownloadArgs(
        string playlistUrl,
        string outputDir,
        string archiveFile,
        YtDlpDownloadOptions options)
    {
        var args = CommonArgs();
        args.AddRange(BuildFormatArgs(options.Reencode));
        if (options.UseSponsorBlock)
        {
            args.AddRange(SponsorBlockArgs(options.SponsorBlockEndpoint));
        }

        args.Add("-o");
        args.Add($"{TrailingSlash(outputDir)}%(playlist_index)02d - %(title)s.%(ext)s");
        args.Add("--download-archive");
        args.Add(archiveFile);
        args.Add("--break-on-existing");
        args.AddRange(PolitenessArgs());
        args.AddRange(options.ExtraArgs);
        args.Add("--");
        args.Add(playlistUrl);
        return args;
    }

    /// <summary>Simulated full-info probe of a single video (JSON on stdout, nothing downloaded).</summary>
    public IReadOnlyList<string> BuildProbeArgs(string url) =>
    [
        "--ignore-config",
        "--no-warnings",
        "--no-color",
        "--simulate",
        "-J",
        "--no-playlist",
        "--",
        url
    ];

    private static List<string> CommonArgs() =>
    [
        "--ignore-config",
        "--no-warnings",
        "--no-color",
        "--newline"
    ];

    private static IEnumerable<string> BuildFormatArgs(ReencodeFormat reencode)
    {
        var formatName = ReencodeAudioFormats.GetYtDlpFormat(reencode);
        if (formatName == null)
        {
            // Keep the best available audio container as-is.
            return new List<string> { "-f", "ba/b", "--embed-metadata" };
        }

        return new List<string>
        {
            "-f", "ba/b", "-x",
            "--audio-format", formatName,
            "--audio-quality", "0", // 0 = best
            "--embed-metadata"
        };
    }

    private static IEnumerable<string> SponsorBlockArgs(string endpoint)
    {
        return new List<string>
        {
            "--sponsorblock-remove", SponsorBlockCategories.NonMusicJoined,
            "--sponsorblock-api", endpoint
        };
    }

    /// <summary>
    /// Politeness baseline. Inter-download spacing is enforced by the plugin's
    /// scheduler (Phase 3), not by yt-dlp's sleep-interval.
    /// </summary>
    private static IEnumerable<string> PolitenessArgs()
    {
        return new List<string>
        {
            "--sleep-requests", "1",
            "--retries", "10",
            "--retry-sleep", "exp=2:32"
        };
    }

    private static int ClampLimit(int limit) => Math.Clamp(limit, 1, 100);

    private static string TrailingSlash(string path) =>
        path.EndsWith('/') || path.EndsWith('\\') ? path : path + "/";
}

/// <summary>Download-relevant settings slice handed to the command builder.</summary>
public sealed record YtDlpDownloadOptions
{
    public ReencodeFormat Reencode { get; init; } = ReencodeFormat.Disabled;
    public bool UseSponsorBlock { get; init; }
    public string SponsorBlockEndpoint { get; init; } = YtDlpSettings.DefaultSponsorBlockEndpoint;

    /// <summary>Already-tokenized user arguments (see ShellTokenizer).</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
}
