using System.Collections.Concurrent;

namespace Lidarr.Plugin.YtDlp.Runner;

/// <summary>
/// Per-path cache of production runners.
///
/// The host DI container cannot construct <see cref="CliWrapYtDlpRunner"/> — the
/// executable path is only known once provider settings are loaded, and
/// <c>AutoAddServices</c> would otherwise try to resolve <see cref="IYtDlpRunner"/>
/// and fail on the required constructor argument. Providers therefore obtain a
/// runner from here instead of via DI.
/// </summary>
public static class YtDlpRunners
{
    private static readonly ConcurrentDictionary<string, IYtDlpRunner> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Replaceable for tests (swap in a fake runner factory).</summary>
    public static Func<string, IYtDlpRunner> Factory { get; set; } = static path => new CliWrapYtDlpRunner(path);

    public static IYtDlpRunner For(string? path)
    {
        var key = string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim();
        return Cache.GetOrAdd(key, Factory);
    }

    internal static void ClearCache() => Cache.Clear();
}
