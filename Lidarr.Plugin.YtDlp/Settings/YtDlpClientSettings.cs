using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace Lidarr.Plugin.YtDlp.Settings;

/// <summary>
/// Host download client settings.
///
/// Carries every download-side field (they used to sit in the shared base
/// class, which wrongly exposed "Search Limit" in this form — an indexer-only
/// concern — v0.2.3). SearchLimit is gone from here; it now lives on
/// <see cref="YtDlpIndexerSettings"/>.
///
/// Existing installed configs keep working: the host deserializes the stored
/// settings JSON into this POCO by property name, so the removed field is
/// simply ignored and the kept ones retain their saved values.
/// </summary>
public class YtDlpClientSettings : YtDlpSettings, IProviderConfig, IEquatable<YtDlpClientSettings>
{
    private static readonly YtDlpClientSettingsValidator Validator = new();

    [FieldDefinition(1, Label = "Additional Arguments", Type = FieldType.Textbox, Advanced = true,
        HelpText = "Extra raw yt-dlp arguments appended to every command. Quote values that contain spaces.")]
    public string AdditionalArgs { get; set; } = string.Empty;

    [FieldDefinition(2, Label = "Use SponsorBlock", Type = FieldType.Checkbox,
        HelpText = "Remove non-music segments (intro, outro, self-promotion, ...) using the SponsorBlock API. Requires ffmpeg on PATH.")]
    public bool UseSponsorBlock { get; set; }

    [FieldDefinition(3, Label = "SponsorBlock API Endpoint", Type = FieldType.Textbox, Advanced = true,
        HelpText = "Base URL of the SponsorBlock API (defaults to the public instance).")]
    public string SponsorBlockApiEndpoint { get; set; } = YtDlpSettings.DefaultSponsorBlockEndpoint;

    [FieldDefinition(4, Label = "Re-encode", Type = FieldType.Select, SelectOptions = typeof(ReencodeFormat),
        HelpText = "Re-encode downloaded audio to a fixed format using ffmpeg (Disabled keeps the source audio). Requires ffmpeg on PATH.")]
    public ReencodeFormat Reencode { get; set; } = ReencodeFormat.Disabled;

    [FieldDefinition(5, Label = "Min Delay", Type = FieldType.Number, Unit = "seconds", Advanced = true,
        HelpText = "Minimum seconds to wait before the next download (bot-detection spacing).")]
    public double MinDownloadDelaySeconds { get; set; } = 5;

    [FieldDefinition(6, Label = "Max Delay", Type = FieldType.Number, Unit = "seconds", Advanced = true,
        HelpText = "Maximum seconds to wait before the next download (bot-detection spacing).")]
    public double MaxDownloadDelaySeconds { get; set; } = 30;

    [FieldDefinition(7, Label = "Download Folder", Type = FieldType.Path,
        HelpText = "Root folder for downloads. Lidarr imports files from per-artist/album subfolders here and can remove them after import.")]
    public string DownloadPath { get; set; } = string.Empty;

    public NzbDroneValidationResult Validate() => new(Validator.Validate(this));

    public bool Equals(YtDlpClientSettings? other) =>
        other is not null &&
        YtDlpPath == other.YtDlpPath &&
        AdditionalArgs == other.AdditionalArgs &&
        UseSponsorBlock == other.UseSponsorBlock &&
        SponsorBlockApiEndpoint == other.SponsorBlockApiEndpoint &&
        Reencode == other.Reencode &&
        MinDownloadDelaySeconds == other.MinDownloadDelaySeconds &&
        MaxDownloadDelaySeconds == other.MaxDownloadDelaySeconds &&
        DownloadPath == other.DownloadPath;

    public override bool Equals(object? obj) => obj is YtDlpClientSettings other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        YtDlpPath, AdditionalArgs, UseSponsorBlock, SponsorBlockApiEndpoint,
        Reencode, MinDownloadDelaySeconds, MaxDownloadDelaySeconds, DownloadPath);
}
