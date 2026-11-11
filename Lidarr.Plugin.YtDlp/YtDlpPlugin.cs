using System.Text.Json;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Manifest;
using Lidarr.Plugin.YtDlp.Download;
using Lidarr.Plugin.YtDlp.Indexing;
using Lidarr.Plugin.YtDlp.Probing;
using Lidarr.Plugin.YtDlp.Runner;
using Lidarr.Plugin.YtDlp.Settings;
using Lidarr.Plugin.YtDlp.SponsorBlock;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.YtDlp;

/// <summary>
/// Plugin entry point discovered by the Lidarr host via <c>plugin.json</c>
/// (main = this assembly). Indexer (Phase 4) and download client (Phase 3)
/// are live; connection testing (Phase 5) runs once at load (async) and on
/// every settings save (synchronous hook with per-path caching).
/// </summary>
public sealed class YtDlpPlugin : IPlugin
{
    /// <summary>Bounded guard for the synchronous live test inside Validate/Apply.</summary>
    private static readonly TimeSpan SyncTestTimeout = TimeSpan.FromSeconds(15);

    private readonly object _stateLock = new();
    private readonly Dictionary<string, IYtDlpRunner> _runnerByPath = new(StringComparer.Ordinal);
    private IPluginContext? _context;
    private YtDlpSettingsProvider? _settingsProvider;
    private PluginValidationResult? _lastConnectionTest;

    public PluginManifest Manifest { get; }

    public ISettingsProvider SettingsProvider
    {
        get
        {
            lock (_stateLock)
            {
                _settingsProvider ??= CreateSettingsProvider();
                return _settingsProvider;
            }
        }
    }

    /// <summary>
    /// Result of the most recent live connection test (null before
    /// InitializeAsync has run with a configured path, or if the path was empty).
    /// </summary>
    public PluginValidationResult? LastConnectionTest
    {
        get { lock (_stateLock) { return _lastConnectionTest; } }
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

    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_stateLock)
        {
            _context = context;
        }

        // One-shot live check at load (async; no video probe — that can take
        // up to 30s and belongs to the settings "Test", not to plugin load).
        if (SettingsProvider is YtDlpSettingsProvider { Current: { YtDlpPath.Length: > 0 } settings })
        {
            try
            {
                var result = await RunLiveTestAsync(settings, probeUrl: null, cancellationToken).ConfigureAwait(false);
                lock (_stateLock)
                {
                    _lastConnectionTest = result;
                }

                if (result.IsValid)
                {
                    Logger?.LogInformation("yt-dlp connection test passed.");
                }
                else
                {
                    foreach (var error in result.Errors)
                    {
                        Logger?.LogWarning("yt-dlp connection test: {Error}", error);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "yt-dlp connection test failed to run");
            }
        }
    }

    public ValueTask<IIndexer?> CreateIndexerAsync(CancellationToken cancellationToken = default)
    {
        // Structural guards mirror the download client (Phase 5 adds the live probe).
        lock (_stateLock)
        {
            if (TryGetConfiguredSettings(out var settings))
            {
                return ValueTask.FromResult<IIndexer?>(
                    new YtDlpIndexer(settings, GetRunner(settings.YtDlpPath),
                        _context?.LoggerFactory?.CreateLogger<YtDlpIndexer>()));
            }

            return ValueTask.FromResult<IIndexer?>(null);
        }
    }

    public ValueTask<IDownloadClient?> CreateDownloadClientAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (TryGetConfiguredSettings(out var settings))
            {
                return ValueTask.FromResult<IDownloadClient?>(
                    new YtDlpDownloadClient(settings, GetRunner(settings.YtDlpPath)));
            }

            return ValueTask.FromResult<IDownloadClient?>(null);
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
            _lastConnectionTest = null;
        }

        return ValueTask.CompletedTask;
    }

    // ----- internals -----

    private bool TryGetConfiguredSettings(out YtDlpSettings settings)
    {
        settings = default!;
        if (SettingsProvider is not YtDlpSettingsProvider provider
            || provider.Current is not { } current
            || string.IsNullOrWhiteSpace(current.YtDlpPath)
            || current.MinDownloadDelaySeconds > current.MaxDownloadDelaySeconds)
        {
            return false;
        }

        settings = current;
        return true;
    }

    /// <summary>One <see cref="IYtDlpRunner"/> per binary path (shared by indexer + client).</summary>
    private IYtDlpRunner GetRunner(string ytDlpPath)
    {
        if (!_runnerByPath.TryGetValue(ytDlpPath, out var runner))
        {
            runner = new CliWrapYtDlpRunner(ytDlpPath);
            _runnerByPath[ytDlpPath] = runner;
        }

        return runner;
    }

    private YtDlpSettingsProvider CreateSettingsProvider()
    {
        var provider = new YtDlpSettingsProvider();
        provider.ExternalConnectionTest = settings =>
        {
            // The host calls Validate synchronously — bound the live test so a
            // wedged binary can't hang a settings save. Timeout → warning only.
            using var cts = new CancellationTokenSource(SyncTestTimeout);
            try
            {
                return RunLiveTestAsync(settings, probeUrl: null, cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return PluginValidationResult.Success(
                    new[] { $"connection test timed out after {(int)SyncTestTimeout.TotalSeconds}s; please verify the settings manually." });
            }
        };

        return provider;
    }

    /// <summary>
    /// Builds a fresh probe chain for the given settings (runners are bound to
    /// the configured binary path, so they must track setting changes) and runs
    /// the full test: yt-dlp → ffmpeg (when needed) → SponsorBlock (when enabled).
    /// </summary>
    internal static async Task<PluginValidationResult> RunLiveTestAsync(
        YtDlpSettings settings, string? probeUrl, CancellationToken ct)
    {
        using var sponsorBlock = new SponsorBlockClient();
        var probe = new YtDlpProbe(
            new CliWrapYtDlpRunner(settings.YtDlpPath),
            new CliWrapYtDlpRunner("ffmpeg"),
            settings.YtDlpPath);
        var tester = new YtDlpConnectionTester(probe, sponsorBlock);
        return await tester.TestAsync(settings, probeUrl, ct).ConfigureAwait(false);
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
