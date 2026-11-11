STATUS: COMPLETE

# Lidarr plugin authoring guide — research for `Lidarr.Plugin.YtDlp`

Research basis: `RicherTunes/Lidarr.Plugin.Common` docs + source (primary source, per task), the `RicherTunes/Tidalarr` reference plugin, the `Lidarr/Lidarr` repo (`develop` for native host classes, `plugins` branch for the host-side plugin subsystem), and public NuGet endpoint verification. No C# implementation code is included in this document; exact type, method, file, and error-message names are preserved as research findings.

Two distinct plugin models exist and must not be conflated:

- **External Abstractions model** (RicherTunes ecosystem; the model this plugin should target): the plugin implements contracts from the `Lidarr.Plugin.Abstractions` assembly (entry point `IPlugin` with `CreateIndexerAsync` / `CreateDownloadClientAsync`), uses helper bases from `Lidarr.Plugin.Common`, ships a `plugin.json` manifest, and is loaded by a plugin-enabled `.NET 8` Lidarr build (the `pr-plugins-3.x` Docker line, e.g. `ghcr.io/hotio/lidarr:nightly-3.1.3.4970`). Tidalarr is the working reference.
- **Native host-interface model** (public `Lidarr/Lidarr` `plugins` / `plugins-dev` branches): a plain class library implementing the host's own interfaces (`NzbDrone.Core.Download.IDownloadClient`, `NzbDrone.Core.Indexers.IIndexer`) is auto-registered into host DI at startup. Both public branches were checked (full recursive tree scans): neither contains any `Abstractions` or `Streaming` host-bridge files, so the abstractions bridge ships only in the (non-public) `pr-plugins-3.x` host builds. Native base classes below are documented for reference/adapter design, not as external-plugin targets.

---

## 1. TL;DR

- Build one class library, `net8.0` only (`net6.0` is retired), `AssemblyName` = `Lidarr.Plugin.YtDlp`, producing exactly one merged plugin DLL plus `plugin.json` — no sidecar plugin/host assemblies in the ZIP.
- Reference `Lidarr.Plugin.Abstractions` (contracts: `IPlugin`, `IIndexer`, `IDownloadClient`, `ISettingsProvider`) and `Lidarr.Plugin.Common` (bases: `BaseStreamingIndexer<TSettings>`, `BaseStreamingDownloadClient<TSettings>`, hosting `PluginLoader`, TestKit). These two packages are **not found on public nuget.org** (flat-container lookups return `BlobNotFound` / `The specified blob does not exist.`) — obtain them from the RicherTunes feed/source per the docs; treat "available on public NuGet" as unverified.
- Entry point: one class implementing `Lidarr.Plugin.Abstractions.Contracts.IPlugin` (Tidalarr pattern: `TidalarrPlugin : IPlugin`), returning the YtDlp indexer and download client from `CreateIndexerAsync()` / `CreateDownloadClientAsync()`.
- Downloader: derive from `Lidarr.Plugin.Common` `BaseStreamingDownloadClient<YtDlpDownloadClientSettings>`, implement the `IDownloadClient` contract by shelling out to the user's installed `yt-dlp`.
- Indexer: derive from `BaseStreamingIndexer<YtDlpIndexerSettings>`, implement the `IIndexer` contract by running `yt-dlp` search (`ytsearchN:` queries, `-J`/`--print json` output) and mapping results to `StreamingAlbum` / `StreamingTrack`.
- Install location: `{AppDataFolder}/plugins/{Owner}/{Name}` — Linux `~/.config/Lidarr/plugins/...`, Windows `C:\ProgramData\Lidarr/plugins/...`, macOS `~/Library/Application Support/Lidarr/plugins/...`; Docker (hotio): `/config/plugins/{Owner}/{Name}`. Host loads each plugin DLL into its own collectible `AssemblyLoadContext` at startup; a restart is required after install.
- Package as a GitHub release asset named `net8.0.zip` with a `Minimum Lidarr Version:` line in release notes; host UI (System → Plugins) or API installs it from a GitHub repo URL.
- Test with the Common TestKit: unit tests load the plugin through `Lidarr.Plugin.Abstractions.Hosting.PluginLoader` in a collectible ALC; Docker E2E (`Category=DockerE2E`, `LidarrContainerFixture`) requires the `.NET 8` plugins-branch image `nightly-3.1.3.4970` (`pr-plugins-3.x`); the `.NET 6` plugins-branch line (`pr-plugins-2.x`) crash-loops on plugin load.

---

## 2. Required NuGet packages & assembly references

### 2.1 Packages to reference

| Package | Role | Version guidance |
|---|---|---|
| `Lidarr.Plugin.Abstractions` | External plugin contracts: `IPlugin`, `IIndexer`, `IDownloadClient`, `ISettingsProvider`, `SettingDefinition`, `SettingDataType`, `PluginValidationResult`, `StreamingAlbum`, `StreamingTrack`, `StreamingDownloadItem`, hosting `PluginLoader`/`PluginLoadRequest`/`PluginLoadContext`, `PluginManifest` | `1.x` (active line; `apiVersion` in `plugin.json` must match its major version) |
| `Lidarr.Plugin.Common` | Helper bases (`BaseStreamingIndexer<T>`, `BaseStreamingDownloadClient<T>`, `BaseStreamingSettings`), host-bridge helpers, TestKit, packaging scripts | docs compatibility matrix: `1.17.x` active; Tidalarr pins `commonVersion: 1.18.0` — follow the latest published by RicherTunes |
| (test project only) TestKit from `Lidarr.Plugin.Common` (`Lidarr.Plugin.Common.TestKit`) | `LidarrContainerFixture`, `LidarrContainerOptions`, ALC fixtures, HTTP fault handlers | same version as Common |
| xUnit (+ usual test tooling) | Test framework; E2E facts gated by `[Trait("Category","DockerE2E")]` | any current |

**NuGet availability (verified):** `https://api.nuget.org/v3-flatcontainer/lidarr.plugin.common/index.json` and `https://api.nuget.org/v3-flatcontainer/lidarr.plugin.abstractions/index.json` both return the XML error `<Code>BlobNotFound</Code><Message>The specified blob does not exist.` — i.e. these packages are **not on public nuget.org** as checked. The `Lidarr.Plugin.Common` repo's own `NuGet.config` only lists `nuget.org` plus a Lidarr TagLib feed. Conclusion for `Lidarr.Plugin.YtDlp`: reference the packages via the feed/source mechanism the RicherTunes docs prescribe (build from source or their private feed); do not document public NuGet availability as a fact.

**Do NOT reference:** any native Lidarr assemblies (`Lidarr.Core`, `Lidarr.Host`, `Lidarr.Http`, `Lidarr.Api.V1`, `Lidarr.Common`, `NzbDrone.Common`, `NzbDrone.Core`, `NzbDrone.SignalR`) and no host-provided third-party libs (see 2.2).

### 2.2 Assembly layout of the shippable plugin (Tidalarr `packaging/expected-contents.txt`, authoritative)

`[REQUIRED]` in the plugin directory / ZIP:
- `Lidarr.Plugin.YtDlp.dll` (the single merged plugin assembly)
- `plugin.json`

`[FORBIDDEN]` sidecars (Tidalarr list; keep identical for YtDlp):
- `FluentValidation.dll`
- `Microsoft.Extensions.DependencyInjection.Abstractions.dll`
- `Microsoft.Extensions.Logging.Abstractions.dll`
- `Microsoft.Extensions.Caching.Abstractions.dll`
- `Microsoft.Extensions.Caching.Memory.dll`
- `Microsoft.Extensions.Options.dll`
- `Microsoft.Extensions.Primitives.dll`
- `NLog.dll`
- `System.Text.Json.dll`
- `Newtonsoft.Json.dll`
- `Lidarr.Core.dll`, `Lidarr.Http.dll`, `Lidarr.Api.V1.dll`, `Lidarr.Common.dll`, `Lidarr.Host.dll`
- `NzbDrone.Common.dll`, `NzbDrone.Core.dll`, `NzbDrone.SignalR.dll`
- `Lidarr.Plugin.Abstractions.dll`, `Lidarr.Plugin.Common.dll`

Consequence: everything the plugin needs (including the Abstractions/Common types and any third-party dependency) must be **internalized/merged into `Lidarr.Plugin.YtDlp.dll`** (Tidalarr achieves this with ILRepack-style merging; shipping the Abstractions/Common sidecars separately breaks type identity and can cause `COR_E_INVALIDOPERATION`).

### 2.3 Project file settings (Tidalarr `src/Tidalarr/Tidalarr.csproj` as template)

- `TargetFramework`: `net8.0`
- `AssemblyName`: `Lidarr.Plugin.YtDlp` (Tidalarr: `Lidarr.Plugin.Tidalarr`; manifest `main` field points at this DLL)
- `OutputPath`: `bin\` with `AppendTargetFrameworkToOutputPath=false`
- `CopyLocalLockFileAssemblies=true`
- `RunAnalyzersDuringBuild=true`; Tidalarr `NoWarn`: `IDE0052;IDE0060;LPC0001;LPC0002`
- Plugin package references use `PrivateAssets="all"` and `ExcludeAssets="runtime;native;contentfiles"` (docs examples), i.e. they are compile-time-only and get merged, never copied as sidecars
- `plugin.json` copied to output (`Content` + `CopyToOutputDirectory`)
- Tidalarr extra: `SkipHostBridge` MSBuild property (default `false`) that conditionally defines `SKIP_HOST_BRIDGE` — the host-bridge code path is opt-in at build time

---

## 3. Plugin project structure & naming conventions

### 3.1 Repo layout (model: `RicherTunes/Tidalarr`)

```
<repo-root>/
  plugin.json                     # manifest at repo root
  src/<Name>/<Name>.csproj        # AssemblyName = Lidarr.Plugin.YtDlp
  src/<Name>/Integration/         # IPlugin entry point, indexer, download client, settings
  src/<Name>/Integration/Adapters/     # (Tidalarr) adapter classes bridging plugin logic <-> contracts
  src/<Name>/Integration/LidarrNative/ # (Tidalarr) optional host-native bridge helpers
  packaging/expected-contents.txt # required/forbidden assembly list (packaging test source of truth)
  scripts/                        # e.g. extract-lidarr-assemblies.sh (Tidalarr shim delegates to Lidarr.Plugin.Common/scripts/extract-lidarr-assemblies.sh; options: --output-dir, --mode minimal|full, --no-tar-fallback, --plugin-name; writes ext/Lidarr/_output/net8.0 + MANIFEST.txt provenance)
  tests/                          # Unit / Integration / Packaging / Performance / Security categories
```

### 3.2 Naming conventions

- Assembly: `Lidarr.Plugin.<Name>.dll` (`Lidarr.Plugin.YtDlp.dll`).
- `plugin.json` `id`: lowercase `[a-z0-9-]+` → `ytdlp` (Tidalarr: `tidalarr`); folder under the host plugin root uses the **GitHub** `Owner/Name` (e.g. `RicherTunes/Tidalarr`), not the manifest id.
- `rootNamespace`: project namespace (Tidalarr: `Tidalarr`; for this plugin: `YtDlp` or `Lidarr.Plugin.YtDlp` — keep consistent with the entry-point class).
- Entry point class: one public class implementing `Lidarr.Plugin.Abstractions.Contracts.IPlugin` (Tidalarr: `TidalarrPlugin` in `src/Tidalarr/Integration/TidalarrPlugin.cs`). Alternatively the Common bridge base `StreamingPlugin<TModule, TSettings>` can be used as the entry-point base (docs mention both patterns; Tidalarr implements `IPlugin` directly).

### 3.3 `plugin.json` manifest (fields and rules)

Tidalarr reference values (exact):

| Field | Tidalarr value | Notes |
|---|---|---|
| `id` | `tidalarr` | lowercase `[a-z0-9-]+`; for this plugin: `ytdlp` |
| `name` | (display name) | human-readable |
| `version` | `1.3.0` | plugin version (semver) |
| `apiVersion` | `1.x` | pattern `^\d+\.x$`; **must equal the major version of `Lidarr.Plugin.Abstractions`**; loader rejects mismatches |
| `minHostVersion` | `3.1.2.4913` | enforced by loader; for this plugin use the minimum `pr-plugins-3.x` host version you support |
| `commonVersion` | `1.18.0` | optional; version of `Lidarr.Plugin.Common` the plugin was built against |
| `targetFramework` | `net8.0` | optional |
| `main` | `Lidarr.Plugin.Tidalarr.dll` | entry assembly; for this plugin `Lidarr.Plugin.YtDlp.dll` |
| `rootNamespace` | `Tidalarr` | optional |

Loader behavior (external `Lidarr.Plugin.Abstractions.Hosting.PluginLoader`, source-verified): manifest loaded from `Path.Combine(pluginDirectory, "plugin.json")` unless supplied via request; `manifest.EvaluateCompatibility(hostVersion, contractVersion)` runs first — on failure throws `InvalidOperationException` with message `Plugin '{manifest.Id}' is incompatible: {compatibility.Message}`; assembly resolved via `request.GetAssemblyPath(manifest)` — missing assembly throws `FileNotFoundException` with message `Plugin assembly '{assemblyPath}' not found.`; then a collectible `PluginLoadContext` is created. `PluginLoadRequest` properties: `PluginDirectory`, `PluginAssemblyFile`, `SharedAssemblies`, `HostVersion`, `ContractVersion`, `PluginContext`, `Manifest`; assembly-name fallback chain: `PluginAssemblyFile ?? manifest.EntryAssembly ?? manifest.Main`.

### 3.4 Discovery, installation & host loading (Lidarr `plugins` branch, source-verified)

**Install via host UI/API (System → Plugins):**
- User pastes a GitHub repo URL; `NzbDrone.Core.Plugins.PluginService.ParseRepositoryInput` → `(Owner, Name, Tree)`; `GetRemotePlugin` fetches `https://api.github.com/repos/{owner}/{name}/releases`.
- Asset selection is keyed on the host framework: `PluginService.Framework => $"net{_platformInfo.Version.Major}.0"` — on a `.NET 8` host the release must carry an asset containing `net8.0` (convention: `net8.0.zip`).
- The release body is checked for a `Minimum Lidarr Version: X.Y.Z.W` line and compared against the running host.
- `NzbDrone.Core.Plugins.InstallPluginService` (`IExecute<InstallPluginCommand>, IExecute<UninstallPluginCommand>`): downloads the ZIP to `{TempFolder}/plugins/{Name}.zip`, then `_archiveService.Extract(packageDestination, Path.Combine(PluginFolder(), package.Owner, package.Name))`; logs `Plugin [{Owner}/{Name}] v{Version} installed. Please restart Lidarr.` `PluginFolder()` = `IAppFolderInfo.GetPluginPath()` (host extension; its exact source file was not located in the public tree, but its value is confirmed by install/scan behavior and the Docker mount path below).
- Uninstall deletes the same `{pluginRoot}/{owner}/{name}` folder.
- **A restart is required** after install/uninstall; installed plugins are listed by `GET` on `[V1ApiController("system/plugins")]` (`src/Lidarr.Api.V1/System/Plugins/PluginController.cs`).

**Manual install path (same layout):**
- Root: `{AppDataFolder}/plugins`, one folder per `{Owner}/{Name}`:
  - Linux: `~/.config/Lidarr/plugins/{Owner}/{Name}/`
  - macOS: `~/Library/Application Support/Lidarr/plugins/{Owner}/{Name}/`
  - Windows: `C:\ProgramData\Lidarr\plugins/{Owner}/{Name}/`
  - `AppDataFolder` (`src/NzbDrone.Common/EnvironmentInfo/AppFolderInfo.cs`): non-Windows `Environment.SpecialFolder.ApplicationData` + `Lidarr`; Windows `Environment.SpecialFolder.CommonApplicationData` + `Lidarr`; overridable with the `--datadir` argument (`StartupContext.APPDATA`, logs `Data directory is being overridden to [{0}]`).
  - Docker (hotio image, `/config` is AppDataFolder): `/config/plugins/{Owner}/{Name}/` — exact mount used by the TestKit E2E, e.g. `/config/plugins/RicherTunes/Tidalarr`.
- Place `Lidarr.Plugin.YtDlp.dll` + `plugin.json` (+ any allowed sidecars) in that folder.

**Startup loading (`src/NzbDrone.Host/Bootstrap.cs`, `plugins` branch):**
1. `var pluginPaths = new AppFolderInfo(context).GetPluginAssemblies().ToList();` — enumerates plugin DLLs under the plugin root.
2. `(var plugins, pluginRef) = PluginLoader.LoadPlugins(pluginPaths);` — host loader `NzbDrone.Common.Composition.PluginLoader` (static; `LoadPlugins(IEnumerable<string>)` returns `(List<Assembly>, List<WeakReference>)`; each path loaded via `new PluginLoadContext(path)` = `AssemblyLoadContext(isCollectible: true)` + `AssemblyDependencyResolver(pluginPath)`).
3. Plugin assemblies are appended to the base assemblies (`Lidarr.Host`, `Lidarr.Core`, `Lidarr.SignalR`, `Lidarr.Api.V1`, `Lidarr.Http`, `Lidarr.Common`, OS-specific) and all are registered in DryIoc via `AutoAddServices(assemblies)` (auto-registers every exported non-`System` interface as a service type, every exported concrete type transiently, plus a `KnownTypes` index used to resolve contract implementations).
4. `SetPluginStatus(usePlugins)` records `PluginStatus.Enabled`.
5. Fallback: if startup throws with plugins enabled, the host unloads the plugin ALCs (`PluginLoader.UnloadPlugins(pluginRefs)`) and **retries once without plugins**; the UI health check `PluginCheck` then reports `Plugins failed to load, check log for details` (help link `#plugins-failed-to-load`).

**Name-collision table (do not conflate):**

| Symbol | External (Abstractions/Common) | Native host (Lidarr repo, `plugins` branch) |
|---|---|---|
| `IPlugin` | `Lidarr.Plugin.Abstractions.Contracts.IPlugin` — plugin entry point: `Manifest`, `InitializeAsync(IPluginContext, ct)`, `CreateIndexerAsync(ct)`, `CreateDownloadClientAsync(ct)`, `SettingsProvider` | `NzbDrone.Core.Plugins.IPlugin` — installed-plugin registry record: `Name`, `Owner`, `GithubUrl`, `InstalledVersion` (from `AssemblyInformationalVersionAttribute`, fallback `Assembly.GetName().Version`), `AvailableVersion` |
| `PluginLoader` | `Lidarr.Plugin.Abstractions.Hosting.PluginLoader.LoadAsync(PluginLoadRequest, ct)` → `PluginHandle` (used by tests/tooling) | `NzbDrone.Common.Composition.PluginLoader.LoadPlugins(paths)` (static; startup, unloading, `AwaitPluginUnload`) |

---

## 4. Settings model conventions

### 4.1 Contract (`Lidarr.Plugin.Abstractions.Contracts.ISettingsProvider`)

The host interacts with plugin settings exclusively through `ISettingsProvider`:
- `IReadOnlyCollection<SettingDefinition> Describe()`
- `IReadOnlyDictionary<string, object?> GetDefaults()`
- `PluginValidationResult Validate(IDictionary<string, object?> settings)`
- `PluginValidationResult Apply(IDictionary<string, object?> settings)`

`SettingDefinition` fields: `Key`, `DisplayName`, `Description`, `DataType`, `IsRequired`, `AllowedValues`, `DefaultValue`.
`SettingDataType` values: `String`, `Boolean`, `Integer`, `Number`, `Enum`, `Password`, `Json`.

### 4.2 Tidalarr pattern (what a concrete plugin actually does)

- Strongly typed settings POCOs: `TidalIndexerSettings` and `TidalDownloadClientSettings`, both deriving from `BaseStreamingSettings` (Common), with `[FieldDefinition]`-attributed properties and FluentValidation validators (e.g. `ValidateSettings(TidalIndexerSettings)`, `ValidateDownloadClientSettings(TidalDownloadClientSettings)` returning `ValidationResult`).
- The bridge maps the flat `IDictionary<string, object?>` settings dictionary to/from the POCO; validation results surface as `PluginValidationResult`.
- Separate settings per provider surface (indexer settings vs download-client settings) — do the same for YtDlp (search settings vs download settings).

### 4.3 Recommended YtDlp settings surfaces

- `YtDlpIndexerSettings`: `yt-dlp` executable path (String, required), search result count (Integer), query template / language, result filters.
- `YtDlpDownloadClientSettings`: `yt-dlp` executable path (String, required), format string (String), output filename template (String), proxy (String/Password), cookies file (String), concurrency/timeout (Integer), quality preset (Enum with `AllowedValues`).
- Keep keys stable and lowercase-ish; expose defaults via `GetDefaults()`; validation should shell out to the configured `yt-dlp --version` where feasible (fail fast, report the exact error in `PluginValidationResult`).

---

## 5. Downloader plugin details (`IDownloadClient`)

### 5.1 External contract (exact members, `Lidarr.Plugin.Abstractions.Contracts.IDownloadClient`)

- `ValueTask<PluginValidationResult> InitializeAsync(CancellationToken cancellationToken = default)`
- `ValueTask<string> EnqueueAlbumDownloadAsync(string albumId, string outputPath, CancellationToken cancellationToken = default)` — returns a download id
- `ValueTask<bool> RemoveDownloadAsync(string downloadId, bool deleteData = false, CancellationToken cancellationToken = default)`
- `ValueTask<IReadOnlyList<StreamingDownloadItem>> GetActiveDownloadsAsync(CancellationToken cancellationToken = default)`
- `ValueTask<StreamingDownloadItem?> GetDownloadAsync(string downloadId, CancellationToken cancellationToken = default)`

### 5.2 YtDlp download client design

- Derive from `Lidarr.Plugin.Common` **`BaseStreamingDownloadClient<YtDlpDownloadClientSettings>`** (Tidalarr: `TidalDownloadClient : BaseStreamingDownloadClient<TidalDownloadClientSettings>`; Tidalarr also exposes `AuthenticateAsync()`, `GetAlbumAsync`, `GetTrackAsync`, `GetStreamUrlAsync(trackId, quality)`, `ValidateDownloadSettings(...)`, `GenerateFileName(...)`).
- `yt-dlp` integration: launch the configured `yt-dlp` binary via a process host (e.g. `System.Diagnostics.Process`) with args like `yt-dlp -f <format> --no-playlist -o "<template in outputPath>" --newline --progress <url>`; capture stderr for failure diagnostics.
- `albumId` convention must match what the YtDlp indexer emits (recommend a stable token such as `yt:<videoId>` or `ytplaylist:<playlistId>` resolving to a URL inside the plugin).
- Track in-flight downloads in an in-memory table keyed by the returned download id (process handle, target path, status, progress); `GetActiveDownloadsAsync` enumerates it; `RemoveDownloadAsync` cancels/kills the process and optionally deletes partial files when `deleteData` is true.
- Reuse Common host-bridge helpers where the streaming download lifecycle needs host-side glue: `HostBridgeDownloadOrchestrator`, `HostBridgeDownloadTrackerStore<TItem>`, `HostBridgeRuntimeCache<TRuntime, TSettings>`, `AlbumSizeEstimator`, `PathTraversalGuard` (validate `outputPath`), `AlbumDownloadUri`, `MultiQualityReleaseBuilder`, `AlbumReleaseInfoBuilder` (all listed in `docs/HOSTBRIDGE.md`).

### 5.3 Native host reference (context only — not an external-plugin target)

`develop` branch: `src/NzbDrone.Core/Download/DownloadClientBase.cs` — `DownloadClientBase<TSettings> : IDownloadClient where TSettings : IProviderConfig, new()` with injected `IConfigService`, `IDiskProvider`, `IRemotePathMappingService`, `ILocalizationService`, `Logger`, and a Polly retry pipeline (`Delay = 3s`, `MaxRetryAttempts = 2`, exponential backoff with jitter; retries on 5xx, `RequestTimeout`, server `HttpException`). `src/NzbDrone.Core/Download/IDownloadClient.cs` — `IDownloadClient : IProvider` with `Protocol`, `Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer)`, `IEnumerable<DownloadClientItem> GetItems()`, `GetImportItem(...)`, `RemoveItem(item, deleteData)`, `DownloadClientInfo GetStatus()`, `MarkItemAsImported(...)`. `DownloadClientType` enum: `Sabnzbd = 0`, `Blackhole = 1`, `Pneumatic = 2`, `Nzbget = 3`. A YtDlp download client in the native model would mirror Sabnzbd-style semantics (enqueue → poll → imported), but for this plugin the external contract above is the one to implement.

---

## 6. Indexer plugin details (`IIndexer`)

### 6.1 External contract (exact members, `Lidarr.Plugin.Abstractions.Contracts.IIndexer`)

- `ValueTask<PluginValidationResult> InitializeAsync(CancellationToken cancellationToken = default)`
- `ValueTask<IReadOnlyList<StreamingAlbum>> SearchAlbumsAsync(string query, CancellationToken cancellationToken = default)`
- `ValueTask<IReadOnlyList<StreamingTrack>> SearchTracksAsync(string query, CancellationToken cancellationToken = default)`
- `ValueTask<StreamingAlbum?> GetAlbumAsync(string albumId, CancellationToken cancellationToken = default)`
- `IAsyncEnumerable<StreamingAlbum> SearchAlbumsStreamAsync(string query, CancellationToken cancellationToken = default)`
- `IAsyncEnumerable<StreamingTrack> SearchTracksStreamAsync(string query, CancellationToken cancellationToken = default)`

### 6.2 YtDlp indexer design

- Derive from `Lidarr.Plugin.Common` **`BaseStreamingIndexer<YtDlpIndexerSettings>`** (Tidalarr: `TidalIndexer : BaseStreamingIndexer<TidalIndexerSettings>, IDisposable` with `AuthenticateAsync()`, `SearchAlbumsAsync(searchTerm)`, `SearchTracksAsync(searchTerm)`, `GetAlbumDetailsAsync(albumId)`, `ValidateSettings(...)`, `GetHttpClient()`, `SearchEnhancedAsync(query)`, `Dispose()`).
- Search via `yt-dlp`: `yt-dlp --flat-playlist -J "ytsearchN:<query>"` (or `--print json` variants) and parse the JSON into `StreamingAlbum` (playlist/album-like results) and `StreamingTrack` (video entries). Set `albumId`/`trackId` to the same stable tokens the download client accepts (see 5.2) so `GetAlbumAsync` and `EnqueueAlbumDownloadAsync` round-trip.
- Respect rate limits (native `HttpIndexerBase` uses a 2s default `RateLimit`; a process-based indexer should serialize invocations and cap `N`), and handle empty/error JSON gracefully.

### 6.3 Native host reference (context only)

`develop` branch: `src/NzbDrone.Core/Indexers/IndexerBase.cs` — `IndexerBase<TSettings> : IIndexer where TSettings : IIndexerSettings, new()`, abstract `Name`/`Protocol`/`SupportsRss`/`SupportsSearch`, `Priority`, `ConfigContract => typeof(TSettings)`, `DefaultDefinitions` yielding an `IndexerDefinition` gated on `config.Validate().IsValid` (sets `EnableRss`, `EnableAutomaticSearch`, `EnableInteractiveSearch`). `src/NzbDrone.Core/Indexers/HttpIndexerBase.cs` — `HttpIndexerBase<TSettings> : IndexerBase<TSettings>`: `MaxNumResultsPerQuery = 1000`, injected `IHttpClient`/`IIndexerStatusService`/`IConfigService`/`IParsingService`/`Logger`, `SupportsRss => true`, `SupportsSearch => true`, `PageSize` (0 = no paging), `RateLimit = 2s`, abstract `GetRequestGenerator()` / `GetParser()`, `FetchRecent()` / `Fetch(AlbumSearchCriteria)`. `src/NzbDrone.Core/Indexers/IIndexer.cs`: `SupportsRss`, `SupportsSearch`, `Protocol`, `FetchRecent()`, `Fetch(AlbumSearchCriteria)`, `Fetch(ArtistSearchCriteria)`, `GetDownloadRequest(link)`. `IIndexerSettings : IProviderConfig` adds `string BaseUrl` and `int? EarlyReleaseLimit`. `IProvider` (`src/NzbDrone.Core/ThingiProvider/IProvider.cs`): `Name`, `ConfigContract`, `Message`, `DefaultDefinitions`, `Definition`, `ValidationResult Test()`, `RequestAction(stage, query)`. These shapes inform what the host ultimately consumes after the bridge maps the external contract, but external plugins implement the external `IIndexer` above.

---

## 7. Testing approach

### 7.1 Categories (docs: `TESTING_WITH_TESTKIT.md`)

`Unit`, `Integration`, `Packaging`, `Performance`, `Security`.

- **Unit:** load the plugin the same way the host does — through the external `Lidarr.Plugin.Abstractions.Hosting.PluginLoader` (`LoadAsync` with a `PluginLoadRequest`) in a collectible `AssemblyLoadContext`; never reference plugin types directly from test code (type-identity across ALCs). Test manifest compatibility failures (expect `InvalidOperationException` `Plugin '{id}' is incompatible: ...`), missing-assembly (`FileNotFoundException` `Plugin assembly '{path}' not found.`), `ISettingsProvider` defaults/validate/apply round-trips, and settings validation. For YtDlp: fake the `yt-dlp` binary (stub executable / injected process runner) so search and download logic are testable without network; use TestKit HTTP fault handlers for any HTTP paths.
- **Integration:** test project references: the plugin project, `Lidarr.Plugin.Common`, `Lidarr.Plugin.Abstractions`; TestKit provides ALC fixtures and HTTP fault injection.
- **Packaging:** assert the built output matches `packaging/expected-contents.txt` — required files present, forbidden sidecars (Section 2.2) absent; manifest `apiVersion`/`minHostVersion`/`commonVersion` consistency.
- **Docker E2E:** xUnit facts marked `[Trait("Category","DockerE2E")]` using `Lidarr.Plugin.Common.TestKit.Hosting.LidarrContainerFixture`:
  - Boots a real Lidarr container with the built plugin DLL mounted at `LidarrContainerOptions.PluginMountPath` (Tidalarr example: `/config/plugins/RicherTunes/Tidalarr`); self-skips (with `SkipReason`) when Docker is unavailable, the plugin DLL is missing, or host-bridge artifacts are missing.
  - `DockerImage` must be a **`.NET 8` plugins-branch build** — example `ghcr.io/hotio/lidarr:nightly-3.1.3.4970` (the `pr-plugins-3.x` line). **`.NET 6` plugins-branch builds (`pr-plugins-2.x`) crash-loop on plugin load** — do not use them.
  - Success assertion: `PluginEntrySubstring` matches `name`/`implementation` in both `/api/v1/indexer/schema` and `/api/v1/downloadclient/schema` (i.e. the plugin's indexer and download client are both visible to the host).
  - GitHub Action `.github/actions/docker-e2e` (wave-21/22 smoke harness): inputs `plugin-name`, `test-project`, `lidarr-docker-version` (default `nightly-3.1.3.4970`), `configuration` (default `Release`), `test-filter` (default `Category=DockerE2E`), `container-name` (default `<plugin-name>-e2e`), `test-session-timeout-ms` (default `600000`); preconditions: Linux runner (`ubuntu-latest`), `actions/setup-dotnet` 8.0.x, plugin DLL already built (standard build step `pwsh scripts/verify-local.ps1 -SkipExtract -SkipTests`); the action runs `dotnet test --no-build` and uploads container logs + TRX on failure.

---

## 8. Gotchas & version compatibility notes

**Version matrix (docs `COMPATIBILITY.md`):**

| Lidarr host | Abstractions | Common | TFM | Status |
|---|---|---|---|---|
| 2.14.x | 1.x | 1.17.x | net8.0 | Active |
| 2.13.x | 1.x | 1.1.3 | net6.0 | Maintenance |
| 2.12.x | 0.x | 1.0.x | net6.0 | Deprecated |

- `net6.0` is retired — target **`net8.0` only**.
- Discrepancy to be aware of: the docs matrix names host `2.14.x`, but Tidalarr ships `minHostVersion: 3.1.2.4913` and the E2E default image is `nightly-3.1.3.4970` (plugins-branch `3.x` line) — the plugins-branch host versioning is separate from the release line; set `minHostVersion` against the actual plugin-enabled host you validate on.
- `apiVersion` (`^\d+\.x$`) must equal the major version of `Lidarr.Plugin.Abstractions` or the loader rejects the plugin; `minHostVersion` is enforced by `EvaluateCompatibility`.
- **Type identity:** never ship `Lidarr.Plugin.Abstractions.dll` / `Lidarr.Plugin.Common.dll` (or host assemblies, or the forbidden third-party list) as sidecars — internalize/merge into the single plugin DLL (Tidalarr relies on this to avoid `COR_E_INVALIDOPERATION`).
- **Each plugin runs in its own collectible `AssemblyLoadContext`** — no type bleed from the default ALC; tests must load via the loader for the same reason.
- **Failing plugin = degraded host:** if a plugin throws during startup, the host unloads plugin ALCs and restarts without plugins, surfacing `Plugins failed to load, check log for details` (`#plugins-failed-to-load`). A buggy `InitializeAsync`/factory therefore degrades to "plugin absent", not a host crash (on the plugins-branch loader).
- **Release packaging:** asset must be named for the host framework (`net8.0.zip` for .NET 8 hosts; `PluginService.Framework => $"net{_platformInfo.Version.Major}.0"`), release notes must contain `Minimum Lidarr Version: X.Y.Z.W`, install requires a host restart.
- **NuGet:** `Lidarr.Plugin.Common` / `Lidarr.Plugin.Abstractions` are not on public nuget.org (flat-container `BlobNotFound` / `The specified blob does not exist.`); document the feed/source procedure, not public availability.
- **Docker E2E:** `.NET 8` plugins-branch image (`pr-plugins-3.x`, e.g. `nightly-3.1.3.4970`) is mandatory; `.NET 6` plugins-branch images (`pr-plugins-2.x`) crash-loop on plugin load; container AppDataFolder is `/config`, so the plugin must be mounted at `/config/plugins/{Owner}/{Name}`.
- **yt-dlp specifics:** the host container needs the `yt-dlp` binary available to the plugin (host OS install or user-specified path in settings — make it a required, validated setting); long downloads must be tracked out-of-process-safe (the host restarts); use JSON output (`-J` / `--print json`) rather than scraping text; guard `outputPath` against path traversal (`PathTraversalGuard`); rate-limit and serialize `yt-dlp` invocations.
- **Two `IPlugin` types / two `PluginLoader` types** exist (Section 3.4 table) — external code must use `Lidarr.Plugin.Abstractions.Contracts.IPlugin` and `Lidarr.Plugin.Abstractions.Hosting.PluginLoader`; host internals (`NzbDrone.Core.Plugins.IPlugin`, `NzbDrone.Common.Composition.PluginLoader`) are for host-side understanding only.

---

## 9. Source list

**RicherTunes/Lidarr.Plugin.Common (docs, `main` branch, raw GitHub):**
- https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/docs/how-to/CREATE_PLUGIN.md
- https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/docs/tutorials/BUILD_YOUR_FIRST_PLUGIN.md
- https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/docs/PLUGIN_MANIFEST.md
- https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/docs/COMPATIBILITY.md
- https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/docs/ABSTRACTIONS.md
- https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/docs/PLUGIN_BRIDGE.md
- https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/docs/HOSTBRIDGE.md
- https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/docs/TESTING_WITH_TESTKIT.md
- https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/docs/reference/SETTINGS.md
- https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/docs/quickstart/PLUGIN_AUTHOR.md
- https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/.github/actions/docker-e2e/README.md
- https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/scripts/extract-lidarr-assemblies.sh

**RicherTunes/Lidarr.Plugin.Common (source, `main` branch):**
- https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/src/Abstractions/Contracts/IPlugin.cs
- https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/src/Abstractions/Contracts/IDownloadClient.cs
- https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/src/Abstractions/Contracts/IIndexer.cs
- https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/src/Abstractions/Contracts/ISettingsProvider.cs
- https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/src/Abstractions/Hosting/PluginLoader.cs
- https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/src/Abstractions/Hosting/PluginLoadRequest.cs
- https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/testkit/Hosting/LidarrContainerFixture.cs
- https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/testkit/Hosting/LidarrContainerOptions.cs

**RicherTunes/Tidalarr (reference plugin, `main` branch):**
- https://raw.githubusercontent.com/RicherTunes/Tidalarr/main/plugin.json
- https://raw.githubusercontent.com/RicherTunes/Tidalarr/main/packaging/expected-contents.txt
- https://raw.githubusercontent.com/RicherTunes/Tidalarr/main/src/Tidalarr/Tidalarr.csproj
- https://raw.githubusercontent.com/RicherTunes/Tidalarr/main/src/Tidalarr/Integration/TidalarrPlugin.cs
- https://raw.githubusercontent.com/RicherTunes/Tidalarr/main/src/Tidalarr/Integration/TidalIndexer.cs
- https://raw.githubusercontent.com/RicherTunes/Tidalarr/main/src/Tidalarr/Integration/TidalDownloadClient.cs
- https://raw.githubusercontent.com/RicherTunes/Tidalarr/main/src/Tidalarr/Integration/TidalIndexerSettings.cs
- https://raw.githubusercontent.com/RicherTunes/Tidalarr/main/src/Tidalarr/Integration/TidalDownloadClientSettings.cs
- https://raw.githubusercontent.com/RicherTunes/Tidalarr/main/scripts/extract-lidarr-assemblies.sh

**Lidarr/Lidarr, `develop` branch (native host classes, reference only):**
- https://raw.githubusercontent.com/Lidarr/Lidarr/develop/src/NzbDrone.Core/Download/DownloadClientBase.cs
- https://raw.githubusercontent.com/Lidarr/Lidarr/develop/src/NzbDrone.Core/Download/IDownloadClient.cs
- https://raw.githubusercontent.com/Lidarr/Lidarr/develop/src/NzbDrone.Core/Download/DownloadClientType.cs
- https://raw.githubusercontent.com/Lidarr/Lidarr/develop/src/NzbDrone.Core/Indexers/HttpIndexerBase.cs
- https://raw.githubusercontent.com/Lidarr/Lidarr/develop/src/NzbDrone.Core/Indexers/IIndexer.cs
- https://raw.githubusercontent.com/Lidarr/Lidarr/develop/src/NzbDrone.Core/Indexers/IIndexerSettings.cs
- https://raw.githubusercontent.com/Lidarr/Lidarr/develop/src/NzbDrone.Core/Indexers/IndexerBase.cs
- https://raw.githubusercontent.com/Lidarr/Lidarr/develop/src/NzbDrone.Core/Indexers/IIndexerRequestGenerator.cs
- https://raw.githubusercontent.com/Lidarr/Lidarr/develop/src/NzbDrone.Core/ThingiProvider/IProvider.cs

**Lidarr/Lidarr, `plugins` branch (host-side plugin subsystem):**
- https://raw.githubusercontent.com/Lidarr/Lidarr/plugins/src/NzbDrone.Host/Bootstrap.cs
- https://raw.githubusercontent.com/Lidarr/Lidarr/plugins/src/NzbDrone.Common/Composition/PluginLoader.cs
- https://raw.githubusercontent.com/Lidarr/Lidarr/plugins/src/NzbDrone.Common/Composition/PluginLoadContext.cs
- https://raw.githubusercontent.com/Lidarr/Lidarr/plugins/src/NzbDrone.Common/Composition/AssemblyLoader.cs
- https://raw.githubusercontent.com/Lidarr/Lidarr/plugins/src/NzbDrone.Common/Composition/Extensions.cs
- https://raw.githubusercontent.com/Lidarr/Lidarr/plugins/src/NzbDrone.Common/Composition/KnownTypes.cs
- https://raw.githubusercontent.com/Lidarr/Lidarr/plugins/src/NzbDrone.Core/Plugins/PluginService.cs
- https://raw.githubusercontent.com/Lidarr/Lidarr/plugins/src/NzbDrone.Core/Plugins/InstallPluginService.cs
- https://raw.githubusercontent.com/Lidarr/Lidarr/plugins/src/NzbDrone.Core/Plugins/Plugin.cs
- https://raw.githubusercontent.com/Lidarr/Lidarr/plugins/src/Lidarr.Api.V1/System/Plugins/PluginController.cs
- https://raw.githubusercontent.com/Lidarr/Lidarr/plugins/src/NzbDrone.Core/HealthCheck/Checks/PluginCheck.cs
- https://raw.githubusercontent.com/Lidarr/Lidarr/plugins/src/NzbDrone.Common/EnvironmentInfo/AppFolderInfo.cs
- https://raw.githubusercontent.com/Lidarr/Lidarr/plugins/src/NzbDrone.Common/EnvironmentInfo/AppFolderFactory.cs
- tree scans: `https://api.github.com/repos/Lidarr/Lidarr/git/trees/plugins?recursive=1`, `https://api.github.com/repos/Lidarr/Lidarr/git/trees/plugins-dev?recursive=1` (no `Abstractions`/`Streaming` host files in either public branch)

**NuGet verification (both returned `BlobNotFound` / `The specified blob does not exist.`):**
- https://api.nuget.org/v3-flatcontainer/lidarr.plugin.common/index.json
- https://api.nuget.org/v3-flatcontainer/lidarr.plugin.abstractions/index.json
