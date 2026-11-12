using System.IO.Compression;
using System.Reflection;
using Lidarr.Plugin.YtDlp.Settings;
using NLog;
using NSubstitute;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser;

namespace Lidarr.Plugin.YtDlp.Tests.Packaging;

/// <summary>
/// Validates the shippable artifact produced by the Release build:
///   artifacts/net8.0.zip == { Lidarr.Plugin.YtDlp.dll (merged, single file) }
///
/// Native-model invariants (v3.1.6.5079 host):
///  - the host loads ONLY Lidarr.Plugin.*.dll from {AppData}/plugins/... — no
///    plugin.json, no sidecars;
///  - CliWrap is the only internalized dependency (the host supplies its own
///    FluentValidation/NLog/Newtonsoft/Lidarr.* assemblies, so those must stay
///    EXTERNAL references — merging them would split type identity);
///  - the merged DLL must not redefine any host-contract type (the one expected
///    exception is our custom download-protocol marker in NzbDrone.Core.Indexers,
///    same pattern as the reference native plugin Tubifarry).
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

    private static void RequireReleaseBuild()
    {
        if (!File.Exists(MergedDll))
        {
            Assert.Ignore("Release build not present — run 'dotnet build -c Release' first (CI does this in the build job).");
        }
    }

    // ----- artifact surface -----

    [Test]
    public void Zip_contains_exactly_the_merged_dll_at_root()
    {
        RequireReleaseBuild();
        if (!File.Exists(ZipPath))
        {
            Assert.Ignore("net8.0.zip not present — produced by the Release build (CI build job).");
        }

        using var zip = ZipFile.OpenRead(ZipPath);
        var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.That(names, Is.EquivalentTo(new[] { "Lidarr.Plugin.YtDlp.dll" }),
            "the stable host loads exactly one Lidarr.Plugin.*.dll per folder — nothing else may ship");

        // No manifest: the native host does not read plugin.json.
        Assert.That(names, Is.Not.Contains("plugin.json"));

        // The zipped DLL must be byte-identical to the merged build output.
        var zippedDll = zip.GetEntry("Lidarr.Plugin.YtDlp.dll")!;
        using var src = File.OpenRead(MergedDll);
        using var dst = zippedDll.Open();
        Assert.That(Hash(dst), Is.EqualTo(Hash(src)),
            "zipped DLL differs from the merged Release build output");
    }

    [Test]
    public void Release_dll_is_actually_merged()
    {
        RequireReleaseBuild();

        // Pre-merge plugin build is ~100 KB; the merged artifact absorbs CliWrap
        // and lands around 450 KB. If this drops, the merge target quietly failed.
        var size = new FileInfo(MergedDll).Length;
        Assert.That(size, Is.GreaterThan(150_000), $"merged DLL unexpectedly small ({size} B) — was the merge skipped?");

        var refs = Assembly.LoadFile(MergedDll).GetReferencedAssemblies();
        Assert.That(refs.Select(r => r.Name), Is.Not.Contains("CliWrap"),
            "CliWrap must be internalized, not referenced");
    }

    // ----- external reference surface -----

    [Test]
    public void Merged_dll_keeps_host_supplied_dependencies_external()
    {
        RequireReleaseBuild();

        var refs = Assembly.LoadFile(MergedDll).GetReferencedAssemblies().Select(r => r.Name).ToList();

        // The host (v3.1.6.5079) supplies these; our override signatures expose
        // host-visible types from them (ValidationResult in Test(), NLog Logger in
        // constructors), so they must resolve to the host's copies.
        Assert.That(refs, Does.Contain("Lidarr.Core"));
        Assert.That(refs, Does.Contain("Lidarr.Common"));
        Assert.That(refs, Does.Contain("NLog"));
        Assert.That(refs, Does.Contain("FluentValidation"));

        // Only the .NET 8 framework + the host-supplied set may remain.
        foreach (var name in refs)
        {
            var n = name ?? string.Empty;
            var ok = n.StartsWith("System.", StringComparison.Ordinal)
                     || n.StartsWith("Microsoft.", StringComparison.Ordinal)
                     || n.StartsWith("netstandard", StringComparison.Ordinal)
                     || n is "Lidarr.Core" or "Lidarr.Common" or "Lidarr.Http"
                        or "NLog" or "FluentValidation" or "Newtonsoft.Json"
                        or "WindowsBase" or "mscorlib" or "System.Runtime" or "System.Private.CoreLib";
            Assert.That(ok, Is.True, $"unexpected external reference: {name}");
        }
    }

    // ----- type identity regression guards -----

    [Test]
    public void Merged_dll_does_not_define_host_contract_types()
    {
        RequireReleaseBuild();

        // The stable host's PluginLoadContext loads the plugin into a collectible
        // ALC that resolves the host assemblies (Lidarr.Core/Common/Http,
        // NzbDrone.* namespaces), NLog, FluentValidation and Newtonsoft.Json to the
        // host's own copies. If the merged plugin redefined any of those types,
        // every signature crossing the boundary (IIndexer, ValidationResult,
        // Logger, IDownloadProtocol, ...) would split type identity and throw
        // MissingMethodException / TypeLoadException at runtime.
        //
        // NOTE: the plugin's own root namespace is Lidarr.Plugin.YtDlp — that is
        // the plugin's code and is expected (Tubifarry does the same). The host
        // namespaces to police are the ones the host assemblies define.
        var asm = Assembly.LoadFile(MergedDll);

        var leaked = SafeGetTypes(asm)
            .Where(t =>
                t.Namespace?.StartsWith("NzbDrone.", StringComparison.Ordinal) == true
                || t.Namespace?.StartsWith("NLog", StringComparison.Ordinal) == true
                || t.Namespace?.StartsWith("FluentValidation", StringComparison.Ordinal) == true
                || t.Namespace?.StartsWith("Newtonsoft.Json", StringComparison.Ordinal) == true
                || t.Namespace?.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) == true)
            .Select(t => $"{t.Namespace}.{t.Name}")
            .ToList();

        // Expected exception: the custom download-protocol marker (Tubifarry pattern).
        leaked.Remove("NzbDrone.Core.Indexers.YtDlpDownloadProtocol");

        Assert.That(leaked, Is.Empty,
            "merged plugin must not define host-supplied types, but found: " + string.Join(", ", leaked.Take(10)));
    }

    [Test]
    public void Merged_dll_contains_internalized_cliwrap_types()
    {
        RequireReleaseBuild();

        var types = SafeGetTypes(Assembly.LoadFile(MergedDll));

        // RenameInternalized keeps the namespace but prefixes type names with a
        // per-assembly GUID (e.g. CliWrap.Command → CliWrap.<guid>Command).
        Assert.That(types.Any(t => t.Namespace == "CliWrap" && t.Name.Contains('<')),
            Is.True, "CliWrap types were not merged into the plugin assembly");
    }

    // ----- type-load smoke tests (what the host's DI does at startup) -----

    [Test]
    public void Merged_plugin_types_resolve_against_host_contracts()
    {
        RequireReleaseBuild();

        var asm = Assembly.LoadFile(MergedDll);

        var plugin = asm.GetType("Lidarr.Plugin.YtDlp.YtDlpPlugin");
        var indexer = asm.GetType("Lidarr.Plugin.YtDlp.Indexing.YtDlpIndexer");
        var client = asm.GetType("Lidarr.Plugin.YtDlp.Download.YtDlpDownloadClient");
        var protocol = asm.GetType("NzbDrone.Core.Indexers.YtDlpDownloadProtocol");

        Assert.Multiple(() =>
        {
            Assert.That(plugin, Is.Not.Null, "YtDlpPlugin missing from merged DLL");
            Assert.That(indexer, Is.Not.Null, "YtDlpIndexer missing from merged DLL");
            Assert.That(client, Is.Not.Null, "YtDlpDownloadClient missing from merged DLL");
            Assert.That(protocol, Is.Not.Null, "YtDlpDownloadProtocol missing from merged DLL");
        });

        // The test host loaded Lidarr.Core from the ext/Lidarr project references
        // (same identity the Lidarr host would use), so assignability proves the
        // merged DLL keeps its host references intact.
        Assert.Multiple(() =>
        {
            Assert.That(typeof(NzbDrone.Core.Plugins.Plugin).IsAssignableFrom(plugin!), Is.True);
            Assert.That(typeof(IIndexer).IsAssignableFrom(indexer!), Is.True);
            Assert.That(typeof(IDownloadClient).IsAssignableFrom(client!), Is.True);
            Assert.That(typeof(IDownloadProtocol).IsAssignableFrom(protocol!), Is.True);
        });
    }

    [Test]
    public void Merged_providers_instantiate_and_bind_host_base_classes()
    {
        // Forces JIT of the full base-class chain + every override signature —
        // the exact failure mode of the old "merged a host-shared assembly" bug
        // only shows up when a type is actually created/called.
        RequireReleaseBuild();

        var asm = Assembly.LoadFile(MergedDll);
        var logger = LogManager.GetLogger("test");

        var indexer = Activator.CreateInstance(
            asm.GetType("Lidarr.Plugin.YtDlp.Indexing.YtDlpIndexer")!,
            Substitute.For<IIndexerStatusService>(),
            Substitute.For<IConfigService>(),
            Substitute.For<IParsingService>(),
            logger);

        var client = Activator.CreateInstance(
            asm.GetType("Lidarr.Plugin.YtDlp.Download.YtDlpDownloadClient")!,
            Substitute.For<IConfigService>(),
            Substitute.For<NzbDrone.Common.Disk.IDiskProvider>(),
            Substitute.For<NzbDrone.Core.RemotePathMappings.IRemotePathMappingService>(),
            Substitute.For<NzbDrone.Core.Localization.ILocalizationService>(),
            logger);

        Assert.Multiple(() =>
        {
            Assert.That(indexer, Is.InstanceOf<IIndexer>());
            Assert.That(client, Is.InstanceOf<IDownloadClient>());
            Assert.That(((IIndexer)indexer!).Protocol, Is.EqualTo(nameof(YtDlpDownloadProtocol)));
            Assert.That(((IDownloadClient)client!).Protocol, Is.EqualTo(nameof(YtDlpDownloadProtocol)));
        });
    }

    [Test]
    public void Merged_settings_types_validate_through_host_fluentvalidation()
    {
        // Validation path exercises FluentValidation type identity across the
        // ALC boundary (NzbDroneValidationResult wraps host FluentValidation).
        RequireReleaseBuild();

        var asm = Assembly.LoadFile(MergedDll);
        var settingsType = asm.GetType("Lidarr.Plugin.YtDlp.Settings.YtDlpIndexerSettings")!;
        var settings = (NzbDrone.Core.ThingiProvider.IProviderConfig)Activator.CreateInstance(settingsType)!;

        var validate = settingsType.GetMethod("Validate")!;
        var result = (FluentValidation.Results.ValidationResult)validate.Invoke(settings, null)!;

        // Default settings have no yt-dlp path → must produce a failure, and the
        // result type must be the host's FluentValidation (identity check).
        Assert.That(result.IsValid, Is.False, "empty settings should not pass validation");
        Assert.That(result, Is.InstanceOf<NzbDrone.Core.Validation.NzbDroneValidationResult>());
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
