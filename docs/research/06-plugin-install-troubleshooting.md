STATUS: COMPLETE (2026-11-11) — RESOLVED via native-model port, see `07-native-model-port.md` and `docs/plans/PHASE-7-native-port.md`

# Plugin install investigation — why a GitLab-URL install leaves nothing installed

Symptom investigated: the user pasted `https://gitlab.gitgudaim.com/w200338/lidarr.plugin.ytdlp`
into Lidarr's System → Plugins install screen, pressed Install, Lidarr displayed a "restart
required to install the plugin" modal, and after restarting the plugin was not present anywhere
(no plugin row, no `YouTube (yt-dlp)` indexer, no YtDlp download client). The startup log showed
many `IndexerRepository/DownloadClientRepository: Skipping provider of unknown type ...` warnings
and `PluginCheck: Status: Unhealthy, Plugins failed to load or are not enabled`.

Research basis: the public `Lidarr/Lidarr` source at the user's actual host version, stable tag
`v3.1.6.5079` (verified: byte-identical plugin subsystem to `v3.2.1.5196`, which is a
pre-release; see doc 07 §2) and at the `plugins` branch (`e42a7ca4`), the public
`TypNull/Tubifarry` repository (the plugin the user successfully runs, whose provider types match
the log warnings exactly), and this repo's own `docs/research/01-lidarr-plugin-guides.md` plus
`ext/Lidarr.Plugin.Common` source for the Abstractions/host model. The user's GitLab instance
(`gitlab.gitgudaim.com`) is not reachable from this machine (resolves to an internal address,
fetch blocked), so the GitLab side is assessed from the local checkout of the same repository.
All host code citations below were read from the actual files of the cited commits.

---

## 1. TL;DR

1. **The install screen accepts GitHub only — and fails silently for anything else.**
   `PluginService.ParseRepositoryInput` matches either `^https?://github\.com/{owner}/{name}[/tree/{branch}]`
   or the token syntax `Name@Owner#branch`. A GitLab URL matches neither, parsing yields
   `owner = null`, `GetRemotePlugin` returns `null` **without logging anything**, and
   `InstallPluginService.Execute` does nothing (`if (package != null) InstallPlugin(package);`).
   No files are downloaded, no folder is created, no error is raised.
2. **The "please restart" modal is a frontend quirk, not a success signal.**
   `PluginsConnector.onPluginCommandFinished` opens the restart-required modal unconditionally
   whenever the install command completes — success, failure, or silent no-op. Plugin
   owner/name/version details only populate when the URL matches a GitHub URL regex, so for a
   GitLab URL the user gets the generic "Restart Required" message with no details. That is
   exactly the reported experience.
3. **Even a correctly-placed copy of our `net8.0.zip` would not load on the user's stable host.**
   Stable Lidarr 3.2.x (and the `plugins` branch) implements only the **native
   host-interface plugin model** (the model Tubifarry uses: plugins reference host assemblies
   via a `Lidarr/Lidarr` submodule and implement host `IIndexer`/`IDownloadClient`, auto-
   registered into host DI; no `plugin.json` is read anywhere). Our plugin is built for the
   **external Abstractions model** (`Lidarr.Plugin.Abstractions` streaming contracts,
   `plugin.json`, shared-assembly ALC), which only the `pr-plugins-3.x` host line (the
   plugin-enabled nightly builds, e.g. `ghcr.io/hotio/lidarr:nightly-3.1.3.4970`) can load.
   On stable, our DLL would fail assembly load (the `Lidarr.Plugin.Abstractions` reference is
   intentionally not shipped and is absent from the host) or, if it loaded, register nothing
   the stable host consumes — invisible either way.
4. **The "Skipping provider of unknown type ..." warnings are not caused by our plugin.**
   Every type name in the log is a Tubifarry feature setting (`YouTubeIndexerSettings`,
   `YoutubeProviderSettings`, `AudioConverterSettings`, `LyricsEnhancerSettings`,
   `SearchSniperTaskSettings`, `SimilarArtistsProxySettings`, `SykHookMetadataProxySettings`,
   `Mixed/Lastfm/Discogs/Deezer/CustomMetadataProxySettings`). They are DB rows from a
   previously configured plugin that cannot be resolved because **no plugin DLL is loaded at
   boot** (the plugins folder is empty after the no-op install). No `YtDlp*` type appears —
   consistent with our plugin never having been installed into this database.
5. **What makes install work** — three options, in order of fit:
   - **A. Run the host our plugin was built for** (`pr-plugins-3.x` line) and install manually
     from the GitLab job artifact into `{AppData}/plugins/{Owner}/{Name}/` (works today, no
     GitHub needed). Caveat: that line is 3.1.x-based; the user's 3.2.x data volume is a
     potential schema-downgrade — back it up first.
   - **B. Keep using the host UI install**, which requires publishing to **GitHub**: mirror the
     repo, create a release with an asset whose name contains `net8.0.zip` (release body may
     carry a `Minimum Lidarr Version: X.Y.Z.W` line; enforced only when present).
   - **C. Port the plugin to the native model** so it runs on the user's current stable host
     (the Tubifarry approach). Real rework: different indexer/download-client/settings
     contracts; the yt-dlp runner/queue/SponsorBlock core carries over largely unchanged.

---

## 2. What "Install" actually does (stable `v3.2.1.5196`, file:line verified)

The UI dispatches a command; the server executes it. The controller exposes no install route
itself — the command pipeline does.

### 2.1 Frontend (what the user sees)

`frontend/src/System/Plugins/PluginsConnector.js`:

- `onInstallPluginPress(url)` → dispatches command `INSTALL_PLUGIN` with payload
  `{ githubUrl: url, commandFinished: this.onPluginCommandFinished }`. Note the payload field
  is literally named `githubUrl`.
- `onPluginCommandFinished(command)` → **unconditionally** does
  `setState({ isRestartRequiredModalOpen: true, ... })`. Owner/name/version/details are only
  filled when `url.match(/github\.com\/([^/]+)\/([^/]+)/)` succeeds **and** the command message
  matches `Plugin [owner/name] v... installed`. For any non-GitHub URL both checks fail, so
  the modal opens with empty details.

`frontend/src/System/Plugins/Plugins.js` (lines ~100–114):

- With details: title `Plugin installed - Restart Required`, message
  `Plugin: [owner/name] v...\n\nInstalled from:\n{url}\n\nPlease restart Lidarr to apply changes.`
- Without details (our case): generic title `RestartRequired`, message
  `translate('LidarrRequiresRestartToApplyPluginChanges')` — a bare "restart to apply plugin
  changes" prompt with no indication that nothing happened.

So the modal the user saw is **not** an install-success confirmation; it fires for any
completed install command, including the silent no-op below.

### 2.2 Server: the silent no-op

`src/NzbDrone.Core/Plugins/InstallPluginService.cs`:

```csharp
public void Execute(InstallPluginCommand message)        // line 51
{
    var package = _pluginService.GetRemotePlugin(message.GithubUrl);
    if (package != null)                                  // line 54
    {
        InstallPlugin(package);
    }
}
```

A `null` package is simply skipped — no log, no exception, no failure state. The command
"completes" successfully from the UI's point of view.

`src/NzbDrone.Core/Plugins/PluginService.cs` (stable):

- `ParseRepositoryInput(input)` (lines ~155–198):
  1. Tries `GitHubUrlRegex` (line 241):
     `^https?://github\.com/(?<owner>[^/]+)/(?<n>[^/\s]+)(?:/tree/(?<tree>[^/\s]+))?(?:\.git)?/?$`
     — only `github.com`.
  2. Falls back to a token parser splitting on `@` and `#` (`TagSplitterRegex`, line 244:
     `([@#])`): `Name@Owner#branch` (this is how `Tubifarry@TypNull#develop` works).
  3. `https://gitlab.gitgudaim.com/w200338/lidarr.plugin.ytdlp` matches neither: no
     `github.com`, no `@`/`#` tokens → `nameTokens` swallows the whole URL as `name`,
     `owner` stays `null` → line 195: `if (owner == null || name == null) return default;`
- `GetRemotePlugin(input)` (lines ~40–120):
  - Line 45: `if (string.IsNullOrWhiteSpace(owner)) return null;` — **no log statement on this
    path at all**. This is the exact path a GitLab URL takes; nothing is written to the log.
  - Otherwise: `GET https://api.github.com/repos/{owner}/{name}/releases` (line 52); filters
    releases by: not draft, tree match, `HasCompatibleAsset` (an asset whose name **contains**
    `net{major}.0.zip`, case-insensitive — `Framework = $"net{_platformInfo.Version.Major}.0"`,
    line 38), and `MeetsMinimumVersion` (line 232–238: release body is scanned with
    `Minimum\s+Lidarr\s+Version:?\s*(?:\*\*)?[\s]*(?<version>\d+\.\d+\.\d+\.\d+)`; when absent
    the release passes, when present the version must be `<= BuildInfo.Version`).
  - Newest compatible release wins; its matching asset's `BrowserDownloadUrl` becomes
    `PackageUrl`.

`InstallPlugin(package)` (`InstallPluginService.cs` lines ~60–79) — only reached with a
non-null package:

- `EnsurePluginFolder()` → `{AppData}/plugins`
- downloads `PackageUrl` → `{TempFolder}/plugins/{Name}.zip`
- extracts to `Path.Combine(PluginFolder(), package.Owner, package.Name)` (line 77) — i.e.
  `{AppData}/plugins/{Owner}/{Name}/...`
- log lines on success (the only place "restart" appears server-side):
  - `Downloading plugin [{Owner}/{Name} v{Version}]` (line 73)
  - `Extracting plugin [{Owner}/{Name} v{Version}]` (line 76)
  - `Plugin [{Owner}/{Name}] v{Version} installed. Please restart Lidarr.` (line 78)

The user's log contains **none** of these lines — direct confirmation that the download path
was never reached.

### 2.3 Answer to the URL question

Yes — in every build checked (stable `v3.2.1.5196`, upstream `plugins` branch
`e42a7ca4`, and per `docs/research/01` the `pr-plugins-3.x` line which carries the same
subsystem) the install flow is **GitHub-only**, via the GitHub Releases API. There is no
GitLab, generic-zip-URL, or direct-download support anywhere in the path, and no validation
error is produced for foreign URLs — just the silent no-op. The accepted inputs are exactly:

- `https://github.com/{owner}/{name}` (optionally `.../{name}.git`, optionally `/tree/{branch}`)
- `{Name}[@{Owner}][#{branch}]` token syntax, e.g. `Tubifarry@TypNull#develop`

---

## 3. Where plugins live and how the stable host loads them

Verified in `Lidarr/Lidarr @ v3.2.1.5196`:

- **Folder layout** — `src/NzbDrone.Common/Extensions/PathExtensions.cs`:
  - `GetPluginPath()` (lines 318–321): `Path.Combine({AppData}, "plugins")`
  - `GetPluginAssemblies()` (lines 323–336): enumerates
    `{AppData}/plugins/{Owner}/{Name}/Lidarr.Plugin.*.dll` — a **fixed two-level**
    Owner/Name hierarchy; DLLs must be named `Lidarr.Plugin.*.dll` at the top of the Name
    folder (ours is: `Lidarr.Plugin.YtDlp.dll` ✓).
- **Load** — `src/NzbDrone.Host/Bootstrap.cs` (lines ~205–220): for each DLL,
  `PluginLoader.LoadPlugins` loads it from a stream into a fresh **collectible**
  `PluginLoadContext` (`src/NzbDrone.Common/Composition/PluginLoadContext.cs`):
  `Load(AssemblyName)` resolves via `AssemblyDependencyResolver(pluginPath)` — i.e. only from
  the plugin's own folder — and returns `null` otherwise (falling through to the default
  ALC / host assemblies).
- **Registration** — `Bootstrap` adds the loaded plugin assemblies to the DI assembly list,
  and `src/NzbDrone.Common/Composition/Extensions.cs :: AutoAddServices` registers **all
  exported types** into DryIoc (interfaces as singletons, concrete classes transient).
  There is no manifest parsing, no `plugin.json` handling, no `IPlugin` entry-point
  invocation anywhere in the stable tree (full recursive tree scan: no files matching
  `abstraction|streaming|hostbridge`; the `Plugins` folder contains only
  `Commands/, InstallPluginService.cs, Plugin.cs, PluginService.cs, PluginVersion.cs,
  Resources/`).
- **Failure path** — if plugin loading/startup throws, `Bootstrap.RunHost` logs
  `Error starting with plugins enabled` (lines ~174–176) and **retries the whole host with
  plugins disabled**; the `PluginCheck` health check then reports
  `Plugins failed to load or are not enabled`.
- **UI list** — `PluginController` (GET `system/plugins` only) returns
  `NzbDrone.Core.Plugins.IPlugin` instances from DI. In the stable tree that `IPlugin` is a
  *UI descriptor* (`Name`, `Owner`, `GithubUrl`, `InstalledVersion`, `AvailableVersion`,
  `Plugin.cs`), with an abstract `Plugin` base class whose `InstalledVersion` reads
  `AssemblyInformationalVersionAttribute`. A plugin appears in System → Plugins only if it
  contains a class deriving from that `Plugin` base.

So on stable, a working plugin = a `Lidarr.Plugin.*.dll` that (a) references the host's own
assemblies (they resolve through the default-ALC fallback) and (b) implements host-native
`IIndexer` / `IDownloadClient` / provider-settings types that the auto-registration picks up.

---

## 4. The two plugin models — and which one each host speaks

| | **Native host-interface model** | **External Abstractions model** |
|---|---|---|
| Hosts with it | Stable release line (verified in `v3.2.1.5196`), upstream `plugins` branch | `pr-plugins-3.x` plugin-enabled line (e.g. `ghcr.io/hotio/lidarr:nightly-3.1.3.4970`; per doc 01) |
| Plugin references | Host assemblies via a `Lidarr/Lidarr` git submodule (`Lidarr.Common`, `Lidarr.Core`, `Lidarr.Http`) | `Lidarr.Plugin.Abstractions` (+ `Lidarr.Plugin.Common`) only; **no host assemblies** |
| Contracts | Host-native `NzbDrone.Core.Indexers.IIndexer`, `NzbDrone.Core.Download.IDownloadClient`, provider settings | Streaming `IIndexer`/`IDownloadClient`, `ISettingsProvider`, `IPlugin` entry point (`CreateIndexerAsync`/`CreateDownloadClientAsync`) |
| Manifest | none (not read by the host) | `plugin.json` (`apiVersion`, `commonVersion`, `minHostVersion`, `targetFramework`, `capabilities`) |
| ALC | simple folder-resolver ALC, default-ALC fallback (host assemblies shared implicitly) | shared-assembly ALC: `Lidarr.Plugin.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions` shared with host (see `ext/Lidarr.Plugin.Common/src/Abstractions/Hosting/PluginLoadContext.cs`) |
| How it's consumed | `AutoAddServices` native DI scan of all exported types | host bridge instantiates the `IPlugin` entry point and maps streaming contracts onto native functionality (bridge exists only in `pr-plugins-3.x` builds) |
| Reference plugin | **Tubifarry** (`TypNull/Tubifarry`) | **Tidalarr** (RicherTunes) and **our `Lidarr.Plugin.YtDlp`** |

Evidence that Tubifarry (the plugin running on the user's host) is native-model —
`TypNull/Tubifarry @ master`, checked 2026-11-11:

- `Tubifarry/Tubifarry.csproj`: `AssemblyName = Lidarr.Plugin.Tubifarry`;
  `ProjectReference`s to `Submodules/Lidarr/src/NzbDrone.Common/Lidarr.Common.csproj`,
  `.../NzbDrone.Core/Lidarr.Core.csproj`, `.../Lidarr.Http/Lidarr.Http.csproj` (the
  `Submodules/Lidarr` submodule points at `https://github.com/Lidarr/Lidarr/`);
  `CopyLocalLockFileAssemblies=true` + `ILRepack.Lib.MSBuild.Task` (third-party deps merged,
  host assemblies never shipped).
- **No `plugin.json` anywhere** in the repository tree (434 files scanned).
- README: "In Lidarr, go to `System -> Plugins`. Paste `https://github.com/TypNull/Tubifarry`
  into the GitHub URL box and click Install."
- Releases: asset `Tubifarry-v2.3.1.net8.0.zip` (contains `net8.0` → passes
  `HasCompatibleAsset`), release body carries `**Minimum Lidarr Version:** 3.2.0.0` and the
  install token `Tubifarry@TypNull` — i.e. the current master line targets the **stable
  3.2.x** host. (The user's observation that a newer Tubifarry — the develop line, 2.3.2 —
  "requires nightly" fits: develop-branch releases raise their minimum to builds that only
  exist on the develop/nightly channel.)

Consequences for our plugin on the user's stable host, if the zip were placed manually:

1. The DLL **would be discovered** (`Lidarr.Plugin.YtDlp.dll` matches the name pattern).
2. `LoadFromStream` itself would succeed, but the first JIT/type-scan of any type touching
   `Lidarr.Plugin.Abstractions.Contracts.IPlugin` (e.g. `YtDlpPlugin`, the indexer, the
   settings provider) triggers resolution of `Lidarr.Plugin.Abstractions`:
   - plugin folder: not present — we deliberately do **not** ship it (on the target host the
     assembly is shared with the host; shipping it breaks type identity — the exact Phase-6
     `MissingMethodException` class of bug fixed in `a8eac75`, guarded by
     `Merged_dll_does_not_define_host_shared_types`);
   - default ALC: the stable host app does not contain it (no bridge, no reference) →
     `FileLoadException`/`TypeLoadException` during `Bootstrap`'s DI scan →
     `Error starting with plugins enabled` → **the host retries with all plugins disabled**
     → our plugin (and any other installed plugin) is gone for that boot.
3. Even if it did load (it must not be "fixed" by shipping the Abstractions sidecar — that
   poisons the `pr-plugins` host), it would still be inert on stable: no host-native
   `IIndexer`/`IDownloadClient` implementations, no `Plugin`-derived UI class, and the
   `plugin.json` is simply never read.

Conclusion: **our current artifact is valid only for the `pr-plugins-3.x` (Abstractions) host
line**, exactly as designed in doc 01 and the Phase 6 packaging contract.

---

## 5. Reading the startup log

- `IndexerRepository: Skipping provider of unknown type X` /
  `DownloadClientRepository: Skipping provider of unknown type X` — the host's provider
  repositories iterate saved provider-config rows in the DB; when the type named in a row
  cannot be resolved among the loaded assemblies, the row is skipped with that warning. The
  rows persist (they are not deleted), so the warning reappears on every boot until a loaded
  plugin defines the type again.
- Every name in the user's log maps to a **Tubifarry** feature (README section names match
  one-to-one): YouTube downloader (`YouTubeIndexerSettings`, `YoutubeProviderSettings`),
  Codec Tinker (`AudioConverterSettings`), Lyrics Enhancer (`LyricsEnhancerSettings`),
  Search Sniper (`SearchSniperTaskSettings`), Similar Artists (`SimilarArtistsProxySettings`),
  Slskd hook (`SykHookMetadataProxySettings`), MetaMix proxies (`MixedMetadataProxySettings`,
  `LastfmMetadataProxySettings`, `DiscogsMetadataProxySettings`,
  `DeezerMetadataProxySettings`, `CustomLidarrMetadataProxySettings`).
- Interpretation: this data volume previously had Tubifarry (or similar) configured, but at
  the boot in question **no plugin DLL was loaded at all** — consistent with the plugins
  folder being empty after the no-op install (and, if the plugin was previously installed and
  working, the folder may have been lost during a container/migration step worth checking:
  `ls {AppData}/plugins`).
- No `YtDlp*` provider type appears in the log — our plugin never existed in this database,
  independently confirming the install was a no-op.
- The warnings are harmless in themselves (no data loss; rows are retained) but they are the
  host's way of saying "plugins you configured once are not loaded right now."

---

## 6. Options for making the install work

### Option A — Run the host the plugin was built for; install manually (works today)

1. **Host**: a `pr-plugins-3.x` (Abstractions) build — reference image
   `ghcr.io/hotio/lidarr:nightly-3.1.3.4970` (doc 01; do not use the `.NET 6`
   `pr-plugins-2.x` line — crash-loops on plugin load).
   - **Version-direction caveat**: that line is 3.1.x-based while the user's data volume is
     from a 3.2.x stable. Servarr-family DBs are generally forward-compatible within 3.x, but
     this is a schema **downgrade** direction that is not guaranteed; **back up the config
     volume first**, and be prepared to start a fresh volume and re-create indexer/download
     client settings if the host refuses or rolls back the DB.
   - Open item: whether a newer (3.2-based) plugin-enabled host exists now; the
     RicherTunes docs / Tidalarr CI are the source of truth. If one exists, prefer it — it
     removes the downgrade caveat.
2. **Artifact**: from GitLab CI, open the `package` (or `build`) job of the latest green
   pipeline and download the **job artifact** `net8.0.zip`. (GitLab job-artifact URLs are
   session/token-scoped, so "paste the artifact URL into Lidarr" is not an option — the UI
   only takes GitHub anyway; manual copy it is. Alternatively publish via Option B.)
3. **Place** (contents of the zip are exactly two files; put them at the top of the Name
   folder):

   ```
   {AppData}/plugins/{Owner}/{Name}/
   ├── Lidarr.Plugin.YtDlp.dll
   └── plugin.json
   ```

   - Docker/hotio: `/config/plugins/w200338/lidarr.plugin.ytdlp/`
   - Bare metal: `{AppData}` is whatever the startup log's first lines report as the app
     data folder (Linux default `~/.config/Lidarr`).
   - Owner/Name folder names are cosmetic for loading (the loader only requires the two-level
     shape and the `Lidarr.Plugin.*.dll` name) — but use stable, consistent names; uninstall
     deletes the folder by these names.
4. **Restart** Lidarr.
5. **Expected on success**: no new `Skipping provider of unknown type` lines for YtDlp; no
   `Error starting with plugins enabled`; System → Plugins lists the plugin (version from
   the manifest); Settings → Indexers offers `YouTube (yt-dlp)`; Settings → Download Clients
   offers the YtDlp client; the settings page shows `ytDlpPath` etc.

### Option B — Keep using the host's UI install (requires GitHub)

The UI install is the only "one-click" path and it is GitHub-only (§2.3). To use it:

1. Mirror/publish the repository to GitHub (e.g. `w200338/lidarr.plugin.ytdlp`).
2. Create a **release** (e.g. tag `v0.1.0`) with a release asset whose name **contains**
   `net8.0.zip` (case-insensitive) — e.g. `net8.0.zip` or
   `Lidarr.Plugin.YtDlp-v0.1.0.net8.0.zip`. (This repo's CI already produces
   `artifacts/net8.0.zip` with exactly the two required files at the zip root; GitHub release
   uploads of that file are drop-in compatible.)
3. Optionally include a line `Minimum Lidarr Version: 3.1.2.4913` in the release body —
   enforced only when present (`MeetsMinimumVersion`), and it must be ≤ the host's version.
4. Then: System → Plugins → paste `https://github.com/w200338/lidarr.plugin.ytdlp` → Install.
   The host downloads the newest compatible release asset and extracts it into
   `plugins/w200338/lidarr.plugin.ytdlp/`; expect the `Plugin [w200338/lidarr.plugin.ytdlp]
   v0.1.0 installed. Please restart Lidarr.` progress log line — which is also the UI's real
   success signal (the restart modal alone is not, per §2.1).

This still requires the Abstractions host (Option A step 1) — GitHub solves the *delivery*,
not the *model mismatch*.

### Option C — Port the plugin to the native model (runs on the user's current stable host)

Follow the Tubifarry pattern:

1. Add a `Lidarr/Lidarr` submodule pinned to a `3.2.x` tag; reference
   `Lidarr.Common` / `Lidarr.Core` / `Lidarr.Http` projects (never ship those assemblies —
   they resolve via the stable ALC's default-context fallback).
2. Implement host-native contracts: `NzbDrone.Core.Indexers.IIndexer` (+ provider settings
   type) for search, `NzbDrone.Core.Download.IDownloadClient` (+ provider settings) for the
   download queue, and a small class deriving from `NzbDrone.Core.Plugins.Plugin` so the
   plugin shows in System → Plugins (name/owner/github URL; version from
   `AssemblyInformationalVersionAttribute`).
3. Drop `plugin.json` (not read by this host) and the Abstractions reference; keep the
   ILRepack-merged single-DLL packaging (merge third-party deps only — host assemblies are
   excluded from the merge set and never copied local).
4. Distribute via GitHub releases (Option B mechanics) or manual folder install.

Scope: the contract surface changes (native indexer search API, native download-client queue
API, provider-settings validation flow), so Phases 4/5 need rework on top of the existing
runner/queue/SponsorBlock core, which is model-agnostic and carries over. Treat this as a new
planning phase, not a config tweak.

---

## 7. Verification checklist (for the user)

1. **Exact host identity** — first lines of the startup log (version/build info) and/or the
   container image tag:
   - `3.2.x` release / `linuxserver`, `hotio` stable tags → stable **native** model.
   - tag containing `pr-plugins` / `nightly-3.1.x.49xx` → **Abstractions** host.
   This decides whether Option A/B (current artifact) or Option C (rework) applies.
2. **Confirm the no-op** — `ls {AppData}/plugins` after the GitLab "install": empty or
   missing folder proves nothing was installed (expected, per §2.2).
3. **After a manual install** — `ls {AppData}/plugins/{Owner}/{Name}` shows exactly
   `Lidarr.Plugin.YtDlp.dll` and `plugin.json`.
4. **After restart** — log must **not** contain `Error starting with plugins enabled`;
   System → Plugins must list the plugin; the indexer and download client must appear in
   their settings pages.
5. **Unknown-type warnings** — they disappear only when a loaded plugin redefines the
   corresponding types (e.g. reinstalling the Tubifarry version that still ships them); they
   do not block anything.

---

## 8. Sources

- `Lidarr/Lidarr @ v3.2.1.5196` (stable tag, fetched 2026-11-11):
  `src/NzbDrone.Core/Plugins/PluginService.cs` (lines 38, 45, 52, 155–198, 232–250),
  `src/NzbDrone.Core/Plugins/InstallPluginService.cs` (lines 51–94),
  `src/Lidarr.Api.V1/System/Plugins/PluginController.cs` (GET only),
  `src/NzbDrone.Common/Extensions/PathExtensions.cs` (lines 318–336),
  `src/NzbDrone.Common/Composition/{PluginLoader,PluginLoadContext,Extensions}.cs`,
  `src/NzbDrone.Core/Plugins/Plugin.cs`, `src/NzbDrone.Host/Bootstrap.cs` (lines ~131–220),
  `frontend/src/System/Plugins/{Plugins,PluginsConnector}.js` (lines ~84–114, 180–188).
- `Lidarr/Lidarr @ plugins` (`e42a7ca4fd633e021d69da7daa0368b870b0282e`): same subsystem —
  GitHub-only install, simple ALC, native DI registration.
- `TypNull/Tubifarry @ master` (checked 2026-11-11): `Tubifarry/Tubifarry.csproj`,
  `.gitmodules`, `README.md`, releases API (v2.3.1 2026-11-04: asset
  `Tubifarry-v2.3.1.net8.0.zip`, `Minimum Lidarr Version: 3.2.0.0`, token
  `Tubifarry@TypNull`).
- This repo: `docs/research/01-lidarr-plugin-guides.md` (two-model analysis,
  `pr-plugins-3.x` line, hotio image `nightly-3.1.3.4970`, shared-assembly ALC,
  `minHostVersion` guidance), `ext/Lidarr.Plugin.Common/src/Abstractions/Hosting/PluginLoadContext.cs`,
  `plugin.json`, `Lidarr.Plugin.YtDlp.Tests/Packaging/PackagingTests.cs` (host-shared
  assembly set / merge contract).
- User report (2026-11-11): GitLab URL install attempt, "restart required" modal, absent
  plugin after restart, startup log with `Skipping provider of unknown type ...` warnings and
  `PluginCheck` unhealthy; host self-described as current stable release with Tubifarry 2.1.1
  working and Tubifarry 2.3.2 (develop line) reportedly requiring nightly.
