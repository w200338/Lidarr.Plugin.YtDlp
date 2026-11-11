# 07 — Native-Model Port Design (Lidarr stable host)

```
STATUS: COMPLETE (2026-11-11)
HOST VERIFIED: v3.1.6.5079 (user's running instance)
```

## 1. Why this document exists

The user's running host is **Lidarr 3.1.6.5079** (the newest *stable* GitHub release,
published 2026-10-26; all 3.2.x releases are pre-releases). Their preferences:

- stay on the **stable** branch of Lidarr (no pre-release host),
- a **GitHub push mirror** of the GitLab repo is acceptable (we write the CI),
- **manual folder install** is acceptable,
- the **GitLab pipeline must create a new release** containing the output file
  (job artifacts are not enough — they want a permanent, downloadable release).

The plugin currently targets the **Abstractions model** (external
`Lidarr.Plugin.Abstractions` + `plugin.json`, from the `pr-plugins-3.x` host line).
That model does **not** exist on the stable host. This document verifies what the
user's exact host expects and designs the port to the **native model**.

## 2. Host verification: v3.1.6.5079 is the native model

Release timeline (GitHub `Lidarr/Lidarr`):

| Tag | Prerelease? | Published |
|---|---|---|
| v3.2.1.5196 | yes | 2026-11-08 |
| v3.2.0.5144 | yes | 2026-10-26 |
| **v3.1.6.5079** | **no** | **2026-10-26** |
| v3.1.6.5078 | yes | 2026-09-13 |
| ... older ... | ... | ... |

Checks performed against tag `v3.1.6.5079` (commit `da7b4dfb`):

- `src/NzbDrone.Core/Plugins/PluginService.cs`, `InstallPluginService.cs`,
  `Plugin.cs` — **byte-identical** to v3.2.1.5196.
- `src/NzbDrone.Common/Composition/PluginLoadContext.cs` — the *simple* ALC
  (identical to the `plugins` branch version), **not** the Abstractions bridge.
- Full tree scan (5144 files): **no** `Abstractions`/`Streaming`/host-bridge files.
- `PathExtensions.GetPluginAssemblies()`:

  ```csharp
  return Directory.GetDirectories(pluginFolder)          // {AppData}/plugins/{Owner}/
      .SelectMany(owner => Directory.GetDirectories(owner)   // {Owner}/{Name}/
          .SelectMany(folder => Directory.GetFiles(folder, "Lidarr.Plugin.*.dll")))
  ```

  ⇒ plugin folder layout: `plugins/{owner}/{name}/Lidarr.Plugin.YtDlp.dll`
    (for the user's host: `/var/lib/lidarr/plugins/{owner}/{name}/...`).
- `Bootstrap.cs`: loads those DLLs into a collectible `PluginLoadContext`, then
  `c.AutoAddServices(assemblies)` — all exported concrete types are registered
  into the host DI container (interface → implementation).
- Install flow (`PluginService.ParseRepositoryInput` + `GetRemotePlugin`):
  **GitHub-only** (URL regex `https?://github\.com/{owner}/{name}...` or
  `Name@Owner#branch` token), release assets must contain `net{major}.0.zip`,
  release body may carry `Minimum Lidarr Version: X.Y.Z.W`. Same code as 3.2.1
  (see research doc 06).
- `plugin.json` is **never read** by this host.

**Conclusion:** on 3.1.6.5079 a plugin is (a) one or more DLLs named
`Lidarr.Plugin.*.dll` in `plugins/{owner}/{name}/`, (b) whose exported types
implement host-native contracts (`IIndexer`, `IDownloadClient`, `IPlugin`, ...)
and are auto-registered by DI, and (c) whose UI visibility comes from a
`NzbDrone.Core.Plugins.Plugin` subclass. No manifest, no bridge.

## 3. The native contracts (all from v3.1.6.5079)

### 3.1 UI descriptor — `NzbDrone.Core.Plugins.Plugin`

```csharp
public interface IPlugin {
    string Name { get; }
    string Owner { get; }
    string GithubUrl { get; }
    PluginVersion InstalledVersion { get; }
    PluginVersion AvailableVersion { get; set; }
}
public abstract class Plugin : IPlugin { /* virtual Name/Owner/GithubUrl;
    InstalledVersion = AssemblyInformationalVersionAttribute (fallback: asm version) */ }
```

- A `public class YtDlp : Plugin` in our assembly gives the **System → Plugins**
  row. `GithubUrl` is used for the *update check* (`GetRemotePlugin`) → it must be
  the **GitHub mirror** URL.
- `InstalledVersion` is read from the merged DLL's
  `AssemblyInformationalVersionAttribute` → keep `<InformationalVersion>0.1.0</InformationalVersion>`.
- The UI's "update available" appears when the GitHub release has a newer version
  with a compatible `net8.0.zip` asset.

### 3.2 Provider base — `NzbDrone.Core.ThingiProvider`

```csharp
public interface IProvider {
    string Name { get; }
    Type ConfigContract { get; }                       // our settings class
    ProviderMessage Message { get; }                   // null is fine
    IEnumerable<ProviderDefinition> DefaultDefinitions { get; }
    ProviderDefinition Definition { get; set; }        // set by host per instance
    ValidationResult Test();                          // FluentValidation (host's copy!)
    object RequestAction(string stage, IDictionary<string, string> query);
}
public interface IProviderConfig { NzbDroneValidationResult Validate(); }
```

### 3.3 Indexer — `NzbDrone.Core.Indexers.IIndexer`

```csharp
public interface IIndexer : IProvider {
    bool SupportsRss { get; }
    bool SupportsSearch { get; }
    string Protocol { get; }
    Task<IList<ReleaseInfo>> FetchRecent();
    Task<IList<ReleaseInfo>> Fetch(AlbumSearchCriteria searchCriteria);
    Task<IList<ReleaseInfo>> Fetch(ArtistSearchCriteria searchCriteria);
    HttpRequest GetDownloadRequest(string link);
}
```

- Search criteria carry `Artist` (`Artist.Name`), `Albums`, `AlbumTitle`,
  `AlbumYear`, `Disambiguation`, `ArtistQuery`, `CleanArtistQuery`,
  `UserInvokedSearch`, `InteractiveSearch`.
- Pipeline: `ReleaseSearchService.Dispatch` → `indexer.Fetch(spec)` →
  `IList<ReleaseInfo>` ("reports") → `IDownloadDecisionMaker.GetSearchDecision`
  (host parses release titles against the catalog, runs quality/cutoff decision
  engine) → grabs. **`ReleaseInfo.Title` must be parseable in the usual
  "Artist - Album/Track" shape**; `Artist`/`Album` fields should also be filled.
- `GetDownloadRequest(link)` is used when the host wants to fetch the release
  itself (details/pre-grab). A plain `HttpRequest(link, GET)` is the safe answer
  for a video URL.
- Discovery: `IndexerFactory` receives `IEnumerable<IIndexer>` from DI
  (our exported class is auto-registered). `ProviderFactory.GetAvailableProviders()`
  builds a default definition from `Name` + `ConfigContract`
  (`Activator.CreateInstance(ConfigContract)`) or from `DefaultDefinitions`;
  `SetProviderCharacteristics` copies `Protocol`/`SupportsRss`/`SupportsSearch`
  from the live instance. `GetInstance(definition)` re-resolves the class by
  `definition.Implementation` (class name) from DI and assigns `Definition`.

### 3.4 Download client — `NzbDrone.Core.Download`

```csharp
public interface IDownloadClient : IProvider {
    string Protocol { get; }
    Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer); // returns release link
    IEnumerable<DownloadClientItem> GetItems();
    DownloadClientItem GetImportItem(DownloadClientItem item, DownloadClientItem previousImportAttempt);
    void RemoveItem(DownloadClientItem item, bool deleteData);
    DownloadClientInfo GetStatus();
    void MarkItemAsImported(DownloadClientItem downloadClientItem);
}
```

`DownloadClientBase<TSettings> : IDownloadClient` (host abstract base) provides
`ConfigContract => typeof(TSettings)`, empty `DefaultDefinitions`, `Definition`,
`RequestAction => null`, `Settings => (TSettings)Definition.Settings`,
`Test()` wrapper + `TestFolder` helper, `DeleteItemData(item)`. Abstract members we
must implement:

- `string Name`
- `string Protocol`
- `Task<string> Download(RemoteAlbum, IIndexer)`
- `IEnumerable<DownloadClientItem> GetItems()`
- `void RemoveItem(DownloadClientItem, bool)`
- `DownloadClientInfo GetStatus()`
- `protected void Test(List<ValidationFailure> failures)`

`DownloadClientItem` fields we will fill: `DownloadClientInfo` (via
`DownloadClientItemClientInfo.FromDownloadClient(this, false)`), `DownloadId`,
`Title`, `TotalSize`, `RemainingSize`, `RemainingTime`, `OutputPath`, `Message`,
`Status` (`DownloadItemStatus`), `CanBeRemoved`, `CanMoveFiles`.

`DownloadClientInfo`: `IsLocalhost`, `OutputRootFolders` (we report the download
root).

Queue semantics: after `Download()` returns the link, the host's
`TrackedDownloadService` polls `GetItems()`; items reaching `Completed` are
imported from `OutputPath` by the normal import pipeline.

### 3.5 Settings (UI fields)

Settings classes implement `IProviderConfig`; each persisted property is annotated
with `[FieldDefinition(order, Label = ..., Type = FieldType.X, HelpText = ...)]`
(`NzbDrone.Core.Annotations`); `Validate()` returns a `NzbDroneValidationResult`
built from a FluentValidation `AbstractValidator<T>` (host's FluentValidation,
v9). Field types available: `Path`, `Text`, `Checkbox`, `SelectOptions` (with
`SelectOptionDefinition`), `Number`, etc. Our existing fields map 1:1:

| Field | Type |
|---|---|
| ytDlpPath | Path |
| additionalArgs | Text |
| useSponsorBlock | Checkbox |
| sponsorBlockApiEndpoint | Text (default `https://sponsor.ajay.app`) |
| reencode | SelectOptions (disabled/mp3/aac/opus/vorbis) |
| minDownloadDelaySeconds | Number |
| maxDownloadDelaySeconds | Number |
| searchLimit | Number |

(The two provider types — indexer and download client — can each carry the same
field set, mirroring Tubifarry's `YoutubeProviderSettings` on the client side.)

### 3.6 Logging

Host DI provides **NLog `Logger`** (built-in clients inject `Logger logger`).
The Abstractions model's `Microsoft.Extensions.Logging.ILogger` must be replaced
throughout our core classes (runner, scheduler, indexer, client, probes,
SponsorBlock client).

## 4. Packaging — recipe verified against a real working plugin

Tubifarry v2.3.1 (`Tubifarry-v2.3.1.net8.0.zip`, runs on stable hosts) was
decompiled (Mono.Cecil) to confirm the packaging recipe:

- Zip root: `Lidarr.Plugin.Tubifarry.dll` (+ `.deps.json`, `.pdb`,
  `release_notes.txt` — only the `Lidarr.Plugin.*.dll` glob is loaded).
- **Defined** namespaces: only its own (`Tubifarry.*`) plus *internalized,
  renamed* private deps (Jint, FFMpegCore, FuzzySharp, ...). It also defines a few
  *new* marker types in host namespaces
  (`NzbDrone.Core.Indexers.YoutubeDownloadProtocol : Object`) — new types in host
  namespaces are fine; **redefining host types is not**.
- **External assembly references**: `Lidarr.Core`, `Lidarr.Common`, `Lidarr.Http`,
  `NLog`, `FluentValidation` (9.0.0.0), `Newtonsoft.Json` — i.e. everything that
  appears in host-facing signatures stays **external** and is supplied by the host
  at runtime.
- Its csproj: `AssemblyName = Lidarr.Plugin.$(Project)`, three host
  `ProjectReference`s, `CopyLocalLockFileAssemblies=true`, ILRepack of the output.

**Why host-shared deps must stay external:** `IProvider.Test()` returns
FluentValidation `ValidationResult`; `DownloadClientBase.Test(List<ValidationFailure>)`
is an abstract method we override. If FluentValidation were internalized into our
DLL, our override's parameter type would no longer match the host's signature →
`TypeLoadException` on type load. (This is the same failure class as the Phase 6
Abstractions contamination, just via a different door.)

**ALC semantics (v3.1.6.5079 `PluginLoadContext`):** collectible ALC with an
`AssemblyDependencyResolver` over the plugin DLL path. `Load(name)` resolves only
what the resolver finds in the plugin folder/deps; anything else returns `null` →
the default (host) ALC is used. Consequences:

- ship **exactly one** file: the merged `Lidarr.Plugin.YtDlp.dll`;
- do **not** ship `deps.json`, extra DLLs, or `plugin.json` (harmless but noise);
- references to `Lidarr.Core/Common/Http`, NLog, FluentValidation, Newtonsoft.Json,
  System.Text.Json (in-box on net8) all resolve from the host.

**Our merge set (Release only):** `Lidarr.Plugin.YtDlp.dll` + `CliWrap.dll`
(internalized, renamed). `FluentValidation` is now *host-provided* — it must be
**removed** from the merge set that Phase 6 used. The existing project-local
`ILRepack.targets` (explicit `InputAssemblies`) already supports this; only the
item list changes.

## 5. Component mapping: Abstractions model → native model

| Current (Abstractions model) | Native model replacement | Notes |
|---|---|---|
| `YtDlpPlugin : IPlugin` (factory) | `YtDlp : NzbDrone.Core.Plugins.Plugin` (descriptor) | Name `YtDlp`, Owner `w200338`, GithubUrl = GitHub mirror |
| `YtDlpIndexer : Abstractions IIndexer, IIndexerWithMetadata` (streaming albums/tracks) | `YtDlpIndexer : NzbDrone.Core.Indexers.IIndexer` | `SearchAsync` → `Fetch(criteria)`; `StreamingAlbum/Track` → `ReleaseInfo`; `GetAlbumAsync`/probes drop (host-side detail fetching uses `GetDownloadRequest`); keep single-flight + `searchLimit` |
| `YtDlpDownloadClient : Abstractions IDownloadClient` (streaming progress) | `YtDlpDownloadClient : DownloadClientBase<YtDlpClientSettings>` | `Download()` enqueues into the existing scheduler and returns the URL; `GetItems()` maps scheduler jobs → `DownloadClientItem` |
| `YtDlpSettingsProvider : ISettingsProvider` (flat dict, custom manifest) | `YtDlpIndexerSettings` / `YtDlpClientSettings` : `IProviderConfig` with `[FieldDefinition]` + FluentValidation validator (host v9) | Same 8 fields; `Test()` hook runs the existing `YtDlpConnectionTester` |
| `YtDlpSettings` POCO | split into the two settings classes above (shared field defaults) | |
| `plugin.json` | **removed** (host ignores it) | |
| `ext/Lidarr.Plugin.Common` submodule | **removed**; replaced by `ext/Lidarr` submodule pinned to **v3.1.6.5079** | build against `Lidarr.Common.csproj`, `Lidarr.Core.csproj`, `Lidarr.Http.csproj` |
| `Microsoft.Extensions.Logging.ILogger` everywhere | NLog `Logger` (host DI) | core classes change signatures |
| Core: `YtDlpCommandBuilder`, `CliWrapYtDlpRunner`, `DownloadScheduler`, `SponsorBlockClient`, `ShellTokenizer`, `YtDlpProgressParser` | **kept as-is** (logging swap only) | all their unit tests carry over |
| Packaging: merge {CliWrap, FluentValidation} + zip {dll, plugin.json} | merge {CliWrap} only; zip {dll} only | `PackagingTests` updated: single-DLL zip, no defined FluentValidation/NLog/host types, external refs include Lidarr.Core/Common/Http |

## 6. Versioning and update checks

- `InformationalVersion` (e.g. `0.1.0`) → UI `InstalledVersion`.
- UI update check + one-click install both call
  `PluginService.GetRemotePlugin(GithubUrl)` → GitHub releases of the **mirror**
  repo. Requirements for a release to be accepted by the host:
  - not draft,
  - asset name contains `net8.0.zip`,
  - body may contain `Minimum Lidarr Version: 3.1.6.5079` (host must be ≥; if the
    line is absent the release is accepted unconditionally — include the line so
    older hosts don't try to install it).
- Bump `InformationalVersion` + tag + release together.

## 7. Delivery pipeline (matches user's constraints)

```
GitLab (source of truth, primary CI)
  build → test (junit) → package (net8.0.zip) → [NEW] release (on tag)
      │
      └── GitLab push mirror (Settings → Repository → Mirrors) ──► GitHub repo
                                                                        │
                                                        [NEW] GitHub Actions:
                                                        on tag push: build+test+package
                                                        + create GitHub release
                                                          (asset net8.0.zip,
                                                           body: Minimum Lidarr Version: 3.1.6.5079)
```

- **GitLab release job:** on tag push, POST to the instance's
  `/api/v4/projects/{id}/releases` with `tag_name`, `name`, `description`
  (manual-install instructions), and a link to the packaged zip (upload the
  artifact as a release asset via the API, or link the `package` job artifact).
  Needs a project access token (`api` scope) as CI variable `RELEASE_TOKEN`.
- **GitHub Actions release:** `on: push: tags` → `actions/checkout` with
  `submodules: recursive` (shallow) → `setup-dotnet` 8 → `dotnet build`/`test`/
  `dotnet build -c Release` (runs merge+zip) → `softprops/action-gh-release`
  with `artifacts/net8.0.zip`. `GITHUB_TOKEN` has `contents: write` on tag
  pushes, so no extra secret is needed.
- **Manual install (from the GitLab release):**
  ```
  /var/lib/lidarr/plugins/w200338/lidarr-plugin-ytdlp/Lidarr.Plugin.YtDlp.dll
  ```
  (sub-folder names are arbitrary — only the DLL filename pattern matters).
- **UI install (after mirror + GitHub release):**
  System → Plugins → Install from URL →
  `https://github.com/w200338/lidarr.plugin.ytdlp` (or `YtDlp@w200338`).

## 8. Risks / open verification items

1. **Release matching:** the decision engine parses `ReleaseInfo.Title` to match
   catalog entries. yt-dlp search results come back as `"Artist - TrackTitle"`-ish
   titles with channel name — must verify a real search → grab → import round
   trip on the user's host; adjust title composition (e.g. prefer
   `Channel - Title`, set `Artist`/`Album` fields) if matching fails.
2. **Submodule build cost:** the Lidarr repo is large; CI must use
   `GIT_SUBMODULE_DEPTH: 1` (shallow) and build only the three referenced
   projects (no frontend build — the host projects compile without `_output/UI`).
3. **Host version drift:** the plugin is compiled against v3.1.6.5079's host
   assemblies. If the user later upgrades to 3.2.x, re-pin the submodule and
   rebuild (native hosts are stricter than the Abstractions model about host API
   shape). The `Minimum Lidarr Version:` line documents the floor.
4. **`GetDownloadRequest` semantics** for a non-HTTP release: return a GET to the
   video URL; the host may use it for release details preview.
5. **DI resolution of plugin constructors:** our classes' constructors must only
   take services the host DI can supply (host services + our own exported types +
   NLog `Logger`). Verified pattern: built-in clients and Tubifarry do exactly this.
