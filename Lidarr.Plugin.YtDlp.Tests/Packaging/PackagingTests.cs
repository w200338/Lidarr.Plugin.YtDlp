using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Lidarr.Plugin.Abstractions.Contracts;

namespace Lidarr.Plugin.YtDlp.Tests.Packaging;

/// <summary>
/// Validates the shippable artifact produced by the Release build:
///   artifacts/net8.0.zip == { Lidarr.Plugin.YtDlp.dll (merged), plugin.json }
/// and that the merged DLL's external references are limited to the .NET 8
/// framework plus host-shared assemblies (see PluginLoadContext in the
/// Abstractions submodule) — everything else must be merged in.
/// </summary>
public class PackagingTests
{
    /// <summary>Repo root, derived from the test assembly location (…/Tests/bin/{cfg}/net8.0/).</summary>
    private static string RepoRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string ReleaseDir =>
        Path.Combine(RepoRoot, "Lidarr.Plugin.YtDlp", "bin", "Release", "net8.0");

    private static string MergedDll => Path.Combine(ReleaseDir, "Lidarr.Plugin.YtDlp.dll");

    private static string ZipPath => Path.Combine(RepoRoot, "artifacts", "net8.0.zip");

    private static string PluginJson => Path.Combine(RepoRoot, "plugin.json");

    /// <summary>Tidalarr expected-contents forbidden list + ours (merged, never shipped).</summary>
    private static readonly string[] ForbiddenSidecars =
    {
        "FluentValidation.dll",
        "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
        "Microsoft.Extensions.Logging.Abstractions.dll",
        "Microsoft.Extensions.Caching.Abstractions.dll",
        "Microsoft.Extensions.Caching.Memory.dll",
        "Microsoft.Extensions.Options.dll",
        "Microsoft.Extensions.Primitives.dll",
        "NLog.dll",
        "System.Text.Json.dll",
        "Newtonsoft.Json.dll",
        "Lidarr.Core.dll", "Lidarr.Http.dll", "Lidarr.Api.V1.dll", "Lidarr.Common.dll", "Lidarr.Host.dll",
        "NzbDrone.Common.dll", "NzbDrone.Core.dll", "NzbDrone.SignalR.dll",
        "Lidarr.Plugin.Abstractions.dll", "Lidarr.Plugin.Common.dll",
        // Not on the Tidalarr list, but the single-DLL target requires these merged.
        "CliWrap.dll"
    };

    private static void RequireReleaseBuild()
    {
        if (!File.Exists(MergedDll))
        {
            Assert.Ignore("Release build not present — run 'dotnet build -c Release' first (CI does this in the build job).");
        }
    }

    [Test]
    public void Release_output_contains_merged_dll_and_manifest()
    {
        RequireReleaseBuild();

        // The Release output dir legitimately contains pre-merge build sidecars
        // (they never reach the zip); the artifact surface is asserted on the zip.
        Assert.That(File.Exists(MergedDll), Is.True);
        Assert.That(File.Exists(Path.Combine(ReleaseDir, "plugin.json")), Is.True);

        // The merged DLL must be bigger than a bare plugin build (deps absorbed).
        var size = new FileInfo(MergedDll).Length;
        Assert.That(size, Is.GreaterThan(500_000), $"merged DLL unexpectedly small ({size} B)");
    }

    [Test]
    public void Merged_dll_references_only_framework_and_host_shared_assemblies()
    {
        RequireReleaseBuild();

        var asm = Assembly.LoadFile(MergedDll);
        var refs = asm.GetReferencedAssemblies();

        // Merged deps must not remain external references.
        Assert.That(refs, Is.Not.Empty);
        foreach (var r in refs)
        {
            Assert.That(
                r.Name,
                Is.Not.EqualTo("CliWrap").And.Not.EqualTo("FluentValidation").And.Not.EqualTo("Lidarr.Plugin.Common"),
                $"{r.Name} must be merged into the plugin DLL, not referenced");
        }

        // Contract + logging must stay host-shared (PluginLoadContext shares them).
        Assert.That(refs.Select(r => r.Name), Does.Contain("Lidarr.Plugin.Abstractions"));
        Assert.That(refs.Select(r => r.Name), Does.Contain("Microsoft.Extensions.Logging.Abstractions"));

        // Everything else must be .NET framework / netstandard.
        foreach (var r in refs)
        {
            var name = r.Name ?? string.Empty;
            var ok = name.StartsWith("System.", StringComparison.Ordinal)
                     || name.StartsWith("Microsoft.", StringComparison.Ordinal)
                     || name.StartsWith("netstandard", StringComparison.Ordinal)
                     || name is "Lidarr.Plugin.Abstractions" or "Microsoft.Extensions.Logging.Abstractions"
                     || name is "WindowsBase" or "mscorlib" or "System.Runtime" or "System.Private.CoreLib";
            Assert.That(ok, Is.True, $"unexpected external reference: {r}");
        }
    }

    [Test]
    public void Merged_dll_contains_internalized_dependency_types()
    {
        RequireReleaseBuild();

        var asm = Assembly.LoadFile(MergedDll);
        var types = SafeGetTypes(asm);

        // RenameInternalized prefixes the internalized type names with a per-assembly GUID
        // (e.g. CliWrap.Command → CliWrap.<guid>Command) while keeping the namespace, so
        // match by namespace + name suffix — GUID-independent.
        Assert.That(types.Any(t => t.Namespace == "CliWrap" && t.Name.EndsWith("Command", StringComparison.Ordinal)),
            Is.True, "CliWrap types were not merged into the plugin assembly");
        Assert.That(types.Any(t => t.Namespace == "FluentValidation" && t.Name.EndsWith("AbstractValidator`1", StringComparison.Ordinal)),
            Is.True, "FluentValidation types were not merged into the plugin assembly");
    }

    [Test]
    public void Merged_dll_does_not_define_host_shared_types()
    {
        RequireReleaseBuild();

        // Regression guard: the host's PluginLoadContext shares Lidarr.Plugin.Abstractions,
        // Microsoft.Extensions.Logging.Abstractions and M.E.DependencyInjection.Abstractions
        // with the plugin's collectible ALC. If the merged plugin dll ever defines any of
        // their types (e.g. because a fallback merge swallowed those sidecars), runtime
        // type identity splits across the ALC boundary and every constructor or method
        // taking/returning a shared type throws MissingMethodException.
        var asm = Assembly.LoadFile(MergedDll);
        var leaked = SafeGetTypes(asm)
            .Where(t => t.Namespace?.StartsWith("Lidarr.Plugin.Abstractions", StringComparison.Ordinal) == true
                      || t.Namespace?.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) == true)
            .Select(t => $"{t.Namespace}.{t.Name}")
            .ToList();

        Assert.That(leaked, Is.Empty,
            $"merged plugin must not define host-shared types, but found: {string.Join(", ", leaked.Take(10))}");
    }

    [Test]
    public void Plugin_entry_type_implements_host_shared_iplugin()
    {
        RequireReleaseBuild();

        var asm = Assembly.LoadFile(MergedDll);
        var pluginType = asm.GetType("Lidarr.Plugin.YtDlp.YtDlpPlugin");
        Assert.That(pluginType, Is.Not.Null, "entry point type missing from merged DLL");
        // The test host already loaded Lidarr.Plugin.Abstractions (same identity the
        // host would use), so assignability proves the merged DLL keeps its
        // host-shared reference instead of merging Abstractions.
        Assert.That(typeof(IPlugin).IsAssignableFrom(pluginType!), Is.True);
    }

    [Test]
    public void Zip_contains_exactly_plugin_dll_and_manifest_at_root()
    {
        RequireReleaseBuild();
        if (!File.Exists(ZipPath))
        {
            Assert.Ignore("net8.0.zip not present — produced by the Release build (CI build job).");
        }

        using var zip = ZipFile.OpenRead(ZipPath);
        var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.That(names, Is.EquivalentTo(new[] { "Lidarr.Plugin.YtDlp.dll", "plugin.json" }));

        // The zipped DLL must be byte-identical to the merged build output.
        var zippedDll = zip.GetEntry("Lidarr.Plugin.YtDlp.dll")!;
        using var src = File.OpenRead(MergedDll);
        using var dst = zippedDll.Open();
        Assert.That(Hash(dst), Is.EqualTo(Hash(src)),
            "zipped DLL differs from the merged Release build output");
    }

    [Test]
    public void Zip_contains_no_forbidden_sidecars()
    {
        RequireReleaseBuild();
        if (!File.Exists(ZipPath))
        {
            Assert.Ignore("net8.0.zip not present — produced by the Release build (CI build job).");
        }

        using var zip = ZipFile.OpenRead(ZipPath);
        var names = zip.Entries.Select(e => Path.GetFileName(e.FullName)).ToList();
        foreach (var forbidden in ForbiddenSidecars)
        {
            Assert.That(names, Is.Not.Contains(forbidden), $"{forbidden} must not ship next to the plugin DLL");
        }
    }

    [Test]
    public void Manifest_is_consistent_with_the_artifact()
    {
        RequireReleaseBuild();
        Assert.That(File.Exists(PluginJson), Is.True, "plugin.json missing from repo root");

        using var doc = JsonDocument.Parse(File.ReadAllText(PluginJson));
        var root = doc.RootElement;

        Assert.That(root.GetProperty("id").GetString(), Is.EqualTo("ytdlp"));
        Assert.That(root.GetProperty("name").GetString(), Is.EqualTo("YtDlp"));
        Assert.That(root.GetProperty("version").GetString()!, Does.Match(@"^\d+\.\d+\.\d+$"));
        // Must equal the Abstractions major line (currently 1.x).
        Assert.That(root.GetProperty("apiVersion").GetString()!, Does.Match(@"^\d+\.x$"));
        Assert.That(root.GetProperty("main").GetString(), Is.EqualTo("Lidarr.Plugin.YtDlp.dll"));
        Assert.That(root.GetProperty("commonVersion").GetString()!.Length, Is.GreaterThan(0));
        Assert.That(root.GetProperty("minHostVersion").GetString()!, Does.Match(@"^\d+\.\d+\.\d+\.\d+$"));
        Assert.That(root.GetProperty("targetFramework").GetString(), Is.EqualTo("net8.0"));

        var capabilities = root.GetProperty("capabilities").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.That(capabilities, Is.EquivalentTo(new[] { "search", "download" }));

        var required = root.GetProperty("requiredSettings").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.That(required, Does.Contain("ytDlpPath"));
    }

    [Test]
    public void Manifest_main_dll_is_the_merged_release_build()
    {
        RequireReleaseBuild();

        using var doc = JsonDocument.Parse(File.ReadAllText(PluginJson));
        var main = doc.RootElement.GetProperty("main").GetString()!;
        Assert.That(main, Is.EqualTo("Lidarr.Plugin.YtDlp.dll"));
        Assert.That(File.Exists(Path.Combine(ReleaseDir, main)), Is.True, "manifest 'main' missing from Release output");
    }

    // GetTypes can hit ReflectionTypeLoadException for types whose base types
    // can't be resolved in the LoadFile context; salvage what we can.
    private static IReadOnlyList<Type> SafeGetTypes(Assembly asm)
    {
        try
        {
            return asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Select(t => t!).ToList();
        }
    }

    private static byte[] Hash(Stream stream)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return sha.ComputeHash(stream);
    }
}
