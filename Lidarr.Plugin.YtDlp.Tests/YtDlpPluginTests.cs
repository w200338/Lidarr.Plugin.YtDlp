using NUnit.Framework;

// NOTE: no `using NzbDrone.Core.Plugins;` — the enclosing Lidarr.Plugin namespace
// shadows the bare identifier `Plugin`; the base type is fully qualified below.

namespace Lidarr.Plugin.YtDlp.Tests;

/// <summary>
/// The native host discovers plugins by scanning the assembly for subclasses of
/// <c>NzbDrone.Core.Plugins.Plugin</c> (System → Plugins UI + update checks via
/// GithubUrl). This fixture pins the descriptor contract.
/// </summary>
[TestFixture]
public class YtDlpPluginTests
{
    [Test]
    public void Descriptor_is_a_host_plugin_with_expected_identity()
    {
        // Fully qualified on purpose: the enclosing Lidarr.Plugin namespace
        // shadows a bare "Plugin" identifier.
        var plugin = new YtDlpPlugin();

        Assert.Multiple(() =>
        {
            Assert.That(plugin, Is.InstanceOf<NzbDrone.Core.Plugins.Plugin>());
            Assert.That(plugin.Name, Is.EqualTo("YtDlp"));
            Assert.That(plugin.Owner, Is.EqualTo("w200338"));
        });
    }

    [Test]
    public void Descriptor_github_url_points_at_the_mirror_repo()
    {
        var plugin = new YtDlpPlugin();

        // The host's update check and the "install by GitHub URL" flow use this;
        // the referenced release must contain an asset whose name includes "net8.0.zip".
        Assert.That(plugin.GithubUrl, Is.EqualTo("https://github.com/w200338/lidarr.plugin.ytdlp"));
    }

    [Test]
    public void Protocol_marker_implements_host_idownloadprotocol_with_expected_type_name()
    {
        var protocolType = typeof(NzbDrone.Core.Indexers.YtDlpDownloadProtocol);

        // IDownloadProtocol is an empty marker; the host identifies the protocol
        // by the TYPE NAME (indexer/client Protocol property = nameof(type)), and
        // delay profiles store that name. Pin both.
        Assert.Multiple(() =>
        {
            Assert.That(typeof(NzbDrone.Core.Indexers.IDownloadProtocol).IsAssignableFrom(protocolType), Is.True);
            Assert.That(protocolType.Name, Is.EqualTo("YtDlpDownloadProtocol"));
        });
    }
}
