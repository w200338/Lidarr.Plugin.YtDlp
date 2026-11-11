using System.Text.Json;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Manifest;
using Lidarr.Plugin.YtDlp.Settings;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.YtDlp;

/// <summary>
/// Plugin entry point discovered by the Lidarr host via <c>plugin.json</c>
/// (main = this assembly). Indexer and download client construction happens in
/// Phases 4/3 respectively; until then the factories return null, which the host
/// treats as "capability not available" for this plugin version.
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
        // Phase 4: return the yt-dlp search indexer.
        return ValueTask.FromResult<IIndexer?>(null);
    }

    public ValueTask<IDownloadClient?> CreateDownloadClientAsync(CancellationToken cancellationToken = default)
    {
        // Phase 3: return the yt-dlp download client (single-flight queue).
        return ValueTask.FromResult<IDownloadClient?>(null);
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
