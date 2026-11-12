using NzbDrone.Core.Annotations;

namespace Lidarr.Plugin.YtDlp.Settings;

/// <summary>
/// Shared yt-dlp settings surface, used by both host provider settings classes
/// (<see cref="YtDlpIndexerSettings"/> and <see cref="YtDlpClientSettings"/>).
///
/// The host schema builder reads [FieldDefinition] attributes from the concrete
/// settings type (including inherited public instance properties), so the shared
/// fields are annotated here. Field order 0..7 is shared; the concrete classes add
/// their own fields at order 8+.
/// </summary>
public class YtDlpSettings : IEquatable<YtDlpSettings>
{
    public const string DefaultSponsorBlockEndpoint = "https://sponsor.ajay.app";

    [FieldDefinition(0, Label = "yt-dlp Path", Type = FieldType.Textbox, HelpText = "Path to the yt-dlp executable, or 'yt-dlp' if it is on PATH.")]
    public string YtDlpPath { get; set; } = string.Empty;

    [FieldDefinition(1, Label = "Additional Arguments", Type = FieldType.Textbox, Advanced = true,
        HelpText = "Extra raw yt-dlp arguments appended to every command. Quote values that contain spaces.")]
    public string AdditionalArgs { get; set; } = string.Empty;

    [FieldDefinition(2, Label = "Use SponsorBlock", Type = FieldType.Checkbox,
        HelpText = "Remove non-music segments (intro, outro, self-promotion, ...) using the SponsorBlock API. Requires ffmpeg on PATH.")]
    public bool UseSponsorBlock { get; set; }

    [FieldDefinition(3, Label = "SponsorBlock API Endpoint", Type = FieldType.Textbox, Advanced = true,
        HelpText = "Base URL of the SponsorBlock API (defaults to the public instance).")]
    public string SponsorBlockApiEndpoint { get; set; } = DefaultSponsorBlockEndpoint;

    [FieldDefinition(4, Label = "Re-encode", Type = FieldType.Select, SelectOptions = typeof(ReencodeFormat),
        HelpText = "Re-encode downloaded audio to a fixed format using ffmpeg (Disabled keeps the source audio). Requires ffmpeg on PATH.")]
    public ReencodeFormat Reencode { get; set; } = ReencodeFormat.Disabled;

    [FieldDefinition(5, Label = "Min Delay", Type = FieldType.Number, Unit = "seconds", Advanced = true,
        HelpText = "Minimum seconds to wait before the next download (bot-detection spacing).")]
    public double MinDownloadDelaySeconds { get; set; } = 5;

    [FieldDefinition(6, Label = "Max Delay", Type = FieldType.Number, Unit = "seconds", Advanced = true,
        HelpText = "Maximum seconds to wait before the next download (bot-detection spacing).")]
    public double MaxDownloadDelaySeconds { get; set; } = 30;

    [FieldDefinition(7, Label = "Search Limit", Type = FieldType.Number, Advanced = true,
        HelpText = "Maximum number of results returned by a single search (1..100).")]
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
}
