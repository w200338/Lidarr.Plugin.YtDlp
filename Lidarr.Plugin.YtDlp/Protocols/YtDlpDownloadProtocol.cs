// The protocol marker intentionally lives in the host namespace (NzbDrone.Core.Indexers),
// matching the pattern of working native plugins (e.g. Tubifarry): the host discovers
// IDownloadProtocol implementations from plugin assemblies via DI and registers each
// one in the delay profiles under its type name.

namespace NzbDrone.Core.Indexers
{
    /// <summary>
    /// Custom download protocol for the yt-dlp download client.
    ///
    /// Important for end users: when this plugin is first loaded, Lidarr adds the
    /// "YtDlp" protocol to every EXISTING delay profile with Allowed=false (only
    /// freshly created profiles get it enabled). Until the user enables it in each
    /// delay profile, ProtocolSpecification permanently rejects every release from
    /// this plugin ("Protocol not allowed").
    /// </summary>
    public class YtDlpDownloadProtocol : IDownloadProtocol
    {
    }
}
