using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Validation;

namespace Lidarr.Plugin.YtDlp.Settings;

/// <summary>
/// Host indexer settings (IIndexerSettings requires BaseUrl + EarlyReleaseLimit;
/// neither has a meaningful value for a process-driven indexer, so BaseUrl stays
/// hidden/unused and EarlyReleaseLimit is exposed as an advanced option).
/// </summary>
public class YtDlpIndexerSettings : YtDlpSettings, IIndexerSettings
{
    private static readonly YtDlpSettingsValidator Validator = new();

    public YtDlpIndexerSettings()
    {
        BaseUrl = string.Empty;
    }

    [FieldDefinition(8, Label = "Early Download Limit", Type = FieldType.Number, Unit = "days", Advanced = true,
        HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit.")]
    public int? EarlyReleaseLimit { get; set; }

    public string BaseUrl { get; set; } = string.Empty;

    public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
}
