using NzbDrone.Core.Annotations;

namespace Lidarr.Plugin.YtDlp.Settings;

/// <summary>
/// Fields shared by the indexer and the download client.
///
/// Keep this surface minimal: the host schema builder reflects over every
/// public <c>[FieldDefinition]</c> property of the concrete settings type —
/// inherited ones included — so anything declared here is rendered in BOTH
/// the indexer form and the download client form.
///
/// (v0.2.3 bug: SearchLimit/delays/SponsorBlock/re-encode all lived here,
/// which is why "Search Limit" showed up in the download client settings.
/// v0.2.4: each provider keeps only the fields it actually uses; the shared
/// base is just the yt-dlp path, the one thing both providers need.)
/// </summary>
public abstract class YtDlpSettings
{
    public const string DefaultSponsorBlockEndpoint = "https://sponsor.ajay.app";

    [FieldDefinition(0, Label = "yt-dlp Path", Type = FieldType.Textbox, HelpText = "Path to the yt-dlp executable, or 'yt-dlp' if it is on PATH.")]
    public string YtDlpPath { get; set; } = string.Empty;
}
