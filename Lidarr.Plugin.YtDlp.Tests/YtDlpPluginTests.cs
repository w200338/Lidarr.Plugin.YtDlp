using Lidarr.Plugin.Abstractions.Capabilities;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.YtDlp;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Lidarr.Plugin.YtDlp.Tests;

[TestFixture]
public class YtDlpPluginTests
{
    [Test]
    public void Manifest_loads_plugin_json_from_output_directory()
    {
        var plugin = new YtDlpPlugin();

        Assert.Multiple(() =>
        {
            Assert.That(plugin.Manifest.Id, Is.EqualTo("ytdlp"));
            Assert.That(plugin.Manifest.Name, Is.EqualTo("YtDlp"));
            Assert.That(plugin.Manifest.ApiVersion, Is.EqualTo("1.x"));
            Assert.That(plugin.Manifest.Main, Is.EqualTo("Lidarr.Plugin.YtDlp.dll"));
            Assert.That(plugin.Manifest.Version, Does.Match(@"^\d+\.\d+\.\d+$"));
            Assert.That(plugin.Manifest.SupportsCapability(PluginCapability.ProvidesIndexer), Is.True);
            Assert.That(plugin.Manifest.SupportsCapability(PluginCapability.ProvidesDownloadClient), Is.True);
            Assert.That(plugin.Manifest.RequiredSettings, Does.Contain("ytDlpPath"));
        });
    }

    [Test]
    public void Manifest_falls_back_to_minimal_manifest_when_file_missing()
    {
        var plugin = new YtDlpPlugin("/nonexistent/plugin.json");

        Assert.Multiple(() =>
        {
            Assert.That(plugin.Manifest.Id, Is.EqualTo("ytdlp"));
            Assert.That(plugin.Manifest.ApiVersion, Is.EqualTo("1.x"));
            Assert.That(plugin.Manifest.Main, Is.EqualTo("Lidarr.Plugin.YtDlp.dll"));
        });
    }

    [Test]
    public void SettingsProvider_is_stable_across_accesses()
    {
        var plugin = new YtDlpPlugin();

        Assert.That(plugin.SettingsProvider, Is.SameAs(plugin.SettingsProvider));
        Assert.That(plugin.SettingsProvider.Describe().Count, Is.EqualTo(8));
    }

    [Test]
    public async Task InitializeAsync_stores_context_and_exposes_logger()
    {
        var context = Substitute.For<IPluginContext>();
        context.LoggerFactory.Returns(LoggerFactory.Create(_ => { }));

        var plugin = new YtDlpPlugin();
        var loggerBefore = plugin.Logger;
        await plugin.InitializeAsync(context);

        Assert.That(loggerBefore, Is.Null, "no logger before InitializeAsync");
        Assert.That(plugin.Logger, Is.Not.Null);
    }

    [Test]
    public void InitializeAsync_rejects_null_context()
    {
        var plugin = new YtDlpPlugin();

        Assert.That(() => plugin.InitializeAsync(null!).GetAwaiter().GetResult(), Throws.ArgumentNullException);
    }

    [Test]
    public async Task CreateIndexerAndDownloadClient_return_null_before_their_phases()
    {
        var plugin = new YtDlpPlugin();
        var context = Substitute.For<IPluginContext>();
        context.LoggerFactory.Returns(LoggerFactory.Create(_ => { }));
        await plugin.InitializeAsync(context);

        Assert.That(await plugin.CreateIndexerAsync(), Is.Null);
        Assert.That(await plugin.CreateDownloadClientAsync(), Is.Null);
    }

    [Test]
    public async Task DisposeAsync_clears_context()
    {
        var context = Substitute.For<IPluginContext>();
        context.LoggerFactory.Returns(LoggerFactory.Create(_ => { }));
        var plugin = new YtDlpPlugin();
        await plugin.InitializeAsync(context);
        await plugin.DisposeAsync();

        Assert.That(plugin.Logger, Is.Null);
    }
}
