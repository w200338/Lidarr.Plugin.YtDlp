using System.Text.Json;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Manifest;
using Lidarr.Plugin.YtDlp.Download;
using Lidarr.Plugin.YtDlp.Indexing;
using Lidarr.Plugin.YtDlp.Runner;
using Lidarr.Plugin.YtDlp.Settings;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.YtDlp;

/// <summary>
/// Plugin entry point discovered by the Lidarr host via <c>plugin.json</c>
/// (main = this assembly). The download client is live (Phase 3); the indexer
/// arrives in Phase 4 (factory returns null = capability unavailable).
/// </summary>
public sealed class YtDlpPlugin : IPlugin
{
    private readonly object _stateLock = new();
    private IPluginContext? _context;
    private YtDlpSettingsProvider? _settingsProvider;

    public PluginManifest Manifest { get; }

    public ISettingsProvider SettingsProvider
    {
        get
        {
            lock (_stateLock)
            {
                _settingsProvider ??= new YtDlpSettingsProvider();
                return _settingsProvider;
            }
        }
    }

    public YtDlpPlugin()
    {
        Manifest = LoadManifest(Path.Combine(AppContext.BaseDirectory, "plugin.json"));
    }

    /// <summary>Test hook: build the plugin around a specific plugin.json path.</summary>
    public YtDlpPlugin(string manifestPath)
    {
        Manifest = LoadManifest(manifestPath);
    }

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_stateLock)
        {
            _context = context;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IIndexer?> CreateIndexerAsync(CancellationToken cancellationToken = default)
    {
        // Phase 4: return the yt-dlp search indexer. Structural guards mirror the
        // download client (Phase 5 adds the live yt-dlp probe).
        lock (_stateLock)
        {
            if (SettingsProvider is not YtDlpSettingsProvider provider
                || provider.Current is not { } settings
                || string.IsNullOrWhiteSpace(settings.YtDlpPath)
                || settings.MinDownloadDelaySeconds > settings.MaxDownloadDelaySeconds)
            {
                return ValueTask.FromResult<IIndexer?>(null);
            }

            var runner = new CliWrapYtDlpRunner(settings.YtDlpPath);
            var logger = _context?.LoggerFactory?.CreateLogger<YtDlpIndexer>();
            return ValueTask.FromResult<IIndexer?>(
                new YtDlpIndexer(settings, runner, logger));
        }
    }

    public ValueTask<IDownloadClient?> CreateDownloadClientAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            // Settings flow: the host calls Apply on our stable SettingsProvider
            // instance (see ISettingsProvider); the factory then reads the
            // validated snapshot from it. IPluginContext carries no settings.
            if (SettingsProvider is not YtDlpSettingsProvider provider
                || provider.Current is not { } settings
                || string.IsNullOrWhiteSpace(settings.YtDlpPath))
            {
                return ValueTask.FromResult<IDownloadClient?>(null);
            }

            var runner = new CliWrapYtDlpRunner(settings.YtDlpPath);
            return ValueTask.FromResult<IDownloadClient?>(new YtDlpDownloadClient(settings, runner));
        }
    }

    /// <summary>Logger from the host context (null before InitializeAsync).</summary>
    public ILogger? Logger
    {
        get
        {
            lock (_stateLock)
            {
                return _context?.LoggerFactory?.CreateLogger("Lidarr.Plugin.YtDlp");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            _context = null;
        }

        return ValueTask.CompletedTask;
    }

    internal static PluginManifest LoadManifest(string manifestPath)
    {
        try
        {
            return PluginManifest.Load(manifestPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or JsonException or DirectoryNotFoundException)
        {
            // Expected in partial dev environments — fall back to a minimal manifest
            // so the plugin still loads and surfaces a usable identity.
            return new PluginManifest
            {
                Id = "ytdlp",
                Name = "YtDlp",
                Version = "0.1.0",
                ApiVersion = "1.x",
                Main = "Lidarr.Plugin.YtDlp.dll",
                RequiredSettings = new[] { "ytDlpPath" }
            };
        }
    }
}
