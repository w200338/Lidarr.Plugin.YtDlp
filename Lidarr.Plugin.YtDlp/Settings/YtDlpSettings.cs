namespace Lidarr.Plugin.YtDlp.Settings;

/// <summary>
/// Plugin settings, shared by the indexer and the download client (Lidarr plugins
/// expose one flat settings namespace per plugin).
/// </summary>
public sealed class YtDlpSettings : IEquatable<YtDlpSettings>
{
    public const string DefaultSponsorBlockEndpoint = "https://sponsor.ajay.app";

    public string YtDlpPath { get; set; } = string.Empty;

    /// <summary>Raw additional yt-dlp command line arguments (appended to every command).</summary>
    public string AdditionalArgs { get; set; } = string.Empty;

    public bool UseSponsorBlock { get; set; }

    public string SponsorBlockApiEndpoint { get; set; } = DefaultSponsorBlockEndpoint;

    public ReencodeFormat Reencode { get; set; } = ReencodeFormat.Disabled;

    /// <summary>Minimum seconds to wait between consecutive downloads (bot-detection spacing).</summary>
    public double MinDownloadDelaySeconds { get; set; } = 5;

    /// <summary>Maximum seconds to wait between consecutive downloads (bot-detection spacing).</summary>
    public double MaxDownloadDelaySeconds { get; set; } = 30;

    /// <summary>Maximum number of results returned by a single search (1..100).</summary>
    public int SearchLimit { get; set; } = 25;

    public bool Equals(YtDlpSettings? other)
    {
        return other is not null
            && YtDlpPath == other.YtDlpPath
            && AdditionalArgs == other.AdditionalArgs
            && UseSponsorBlock == other.UseSponsorBlock
            && SponsorBlockApiEndpoint == other.SponsorBlockApiEndpoint
            && Reencode == other.Reencode
            && MinDownloadDelaySeconds == other.MinDownloadDelaySeconds
            && MaxDownloadDelaySeconds == other.MaxDownloadDelaySeconds
            && SearchLimit == other.SearchLimit;
    }

    public override bool Equals(object? obj) => Equals(obj as YtDlpSettings);

    public override int GetHashCode() => HashCode.Combine(
        YtDlpPath, AdditionalArgs, UseSponsorBlock, SponsorBlockApiEndpoint,
        Reencode, MinDownloadDelaySeconds, MaxDownloadDelaySeconds, SearchLimit);

    public YtDlpSettings Clone() => new()
    {
        YtDlpPath = YtDlpPath,
        AdditionalArgs = AdditionalArgs,
        UseSponsorBlock = UseSponsorBlock,
        SponsorBlockApiEndpoint = SponsorBlockApiEndpoint,
        Reencode = Reencode,
        MinDownloadDelaySeconds = MinDownloadDelaySeconds,
        MaxDownloadDelaySeconds = MaxDownloadDelaySeconds,
        SearchLimit = SearchLimit
    };
}
