using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Validation;

namespace Lidarr.Plugin.YtDlp.Settings;

/// <summary>
/// Host indexer settings.
///
/// Only fields the indexer actually uses: the yt-dlp path (inherited) and
/// the search limit. Download-side fields (delays, SponsorBlock, re-encode)
/// belong to the download client — in v0.2.3 they leaked into this form via
/// the shared base class, the same bug that leaked SearchLimit into the
/// client form.
///
/// IIndexerSettings requires BaseUrl + EarlyReleaseLimit; BaseUrl has no
/// meaningful value for a process-driven indexer (stays hidden/unused) and
/// EarlyReleaseLimit is exposed as an advanced option.
/// </summary>
public class YtDlpIndexerSettings : YtDlpSettings, IIndexerSettings, IEquatable<YtDlpIndexerSettings>
{
    private static readonly YtDlpIndexerSettingsValidator Validator = new();

    public YtDlpIndexerSettings()
    {
        BaseUrl = string.Empty;
    }

    [FieldDefinition(1, Label = "Search Limit", Type = FieldType.Number, Advanced = true,
        HelpText = "Maximum number of results returned by a single search (1..100).")]
    public int SearchLimit { get; set; } = 25;

    [FieldDefinition(2, Label = "Early Download Limit", Type = FieldType.Number, Unit = "days", Advanced = true,
        HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit.")]
    public int? EarlyReleaseLimit { get; set; }

    public string BaseUrl { get; set; } = string.Empty;

    public NzbDroneValidationResult Validate() => new(Validator.Validate(this));

    public bool Equals(YtDlpIndexerSettings? other) =>
        other is not null &&
        YtDlpPath == other.YtDlpPath &&
        SearchLimit == other.SearchLimit &&
        EarlyReleaseLimit == other.EarlyReleaseLimit &&
        BaseUrl == other.BaseUrl;

    public override bool Equals(object? obj) => obj is YtDlpIndexerSettings other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(YtDlpPath, SearchLimit, EarlyReleaseLimit, BaseUrl);
}
