using FluentValidation;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace Lidarr.Plugin.YtDlp.Settings;

/// <summary>Host download client settings.</summary>
public class YtDlpClientSettings : YtDlpSettings, IProviderConfig
{
    private static readonly YtDlpClientSettingsValidator Validator = new();

    [NzbDrone.Core.Annotations.FieldDefinition(8, Label = "Download Folder", Type = NzbDrone.Core.Annotations.FieldType.Path,
        HelpText = "Root folder for downloads. Lidarr imports files from per-artist/album subfolders here and can remove them after import.")]
    public string DownloadPath { get; set; } = string.Empty;

    public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
}

/// <summary>
/// Shared yt-dlp rules (via <see cref="YtDlpSettingsValidator.ApplySharedRules{T}"/>)
/// plus the client-only download path rule.
/// </summary>
public class YtDlpClientSettingsValidator : AbstractValidator<YtDlpClientSettings>
{
    public YtDlpClientSettingsValidator()
    {
        YtDlpSettingsValidator.ApplySharedRules(this);

        RuleFor(s => s.DownloadPath)
            .NotEmpty().WithMessage("Download folder is required.")
            .MaximumLength(512).WithMessage("Download folder path is too long.")
            .IsValidPath().WithMessage("Download folder is not a valid path.");
    }
}
