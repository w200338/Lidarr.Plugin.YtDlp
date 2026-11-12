using NzbDrone.Core.Plugins;

namespace Lidarr.Plugin.YtDlp;

/// <summary>
/// UI plugin descriptor (System → Plugins page). The host discovers this type
/// via DI (AutoAddServices registers every exported IPlugin implementation)
/// and uses <see cref="NzbDrone.Core.Plugins.Plugin.GithubUrl"/> for the
/// GitHub-based update check, so it must point at the GitHub mirror of the
/// repository.
///
/// NOTE: the base type is aliased because our own "Lidarr.Plugin" namespace
/// prefix shadows the bare "Plugin" name inside this namespace tree.
/// </summary>
public class YtDlpPlugin : NzbDrone.Core.Plugins.Plugin
{
    public override string Name => "YtDlp";

    public override string Owner => "w200338";

    public override string GithubUrl => "https://github.com/w200338/lidarr.plugin.ytdlp";
}
