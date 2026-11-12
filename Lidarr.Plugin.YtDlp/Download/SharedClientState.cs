using System.Collections.Concurrent;
using Lidarr.Plugin.YtDlp.Queue;

namespace Lidarr.Plugin.YtDlp.Download;

/// <summary>
/// One shared job store + scheduler per download-client definition.
///
/// WHY THIS EXISTS (do not "simplify" back to instance fields): the host
/// resolves provider instances BY CONCRETE TYPE (ProviderFactory.GetInstance
/// → container.GetRequiredService(typeof(YtDlpDownloadClient))) and the
/// host's DI rules register concrete plugin types as TRANSIENT (Bootstrap
/// → AutoAddServices: interfaces singleton, concretes transient). So every
/// host call - Download, GetItems, RemoveItem, MarkItemAsImported, Test -
/// may hit a FRESH YtDlpDownloadClient instance. Job state cannot live on
/// the instance; it lives here, keyed by the client definition id, which
/// the host stamps onto every resolved instance (Definition.Id).
///
/// Definition id 0 (unsaved definition, e.g. the settings Test modal)
/// never shares state - those instances are throwaway.
/// </summary>
public sealed class SharedClientState
{
    private readonly object _schedulerGate = new();
    private DownloadScheduler? _scheduler;

    public int DefinitionId { get; }

    public ConcurrentDictionary<string, DownloadJobState> Jobs { get; } = new();

    /// <summary>Serializes enqueue decisions (duplicate detection + insert) per client.</summary>
    public object EnqueueLock { get; } = new();

    public SharedClientState(int definitionId)
    {
        DefinitionId = definitionId;
    }

    public DownloadScheduler GetScheduler(Func<DownloadScheduler> create)
    {
        lock (_schedulerGate)
        {
            return _scheduler ??= create();
        }
    }
}

/// <summary>Process-wide registry of <see cref="SharedClientState"/> per client definition id.</summary>
public static class SharedClientStates
{
    private static readonly ConcurrentDictionary<int, SharedClientState> _states = new();

    /// <summary>Shared state for a saved client definition; null for id 0 (test/unsaved instances).</summary>
    public static SharedClientState? For(int definitionId) =>
        definitionId > 0
            ? _states.GetOrAdd(definitionId, id => new SharedClientState(id))
            : null;
}
