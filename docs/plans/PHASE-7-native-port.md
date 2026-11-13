# PHASE 7 — Port to the native Lidarr plugin model + release pipeline

```
STATUS: v0.2.3 IMPLEMENTED + LOCALLY VERIFIED (2026-11-13) — code, tests
        (225/225), packaging, CI + docs all done; live E2E on the official
        v3.1.6.5079 host (search → grab → download → import) including the
        queue-refresh nudge that makes the activity queue show live progress.
        DONE (user-confirmed 2026-11-13): force push of the rewritten history
        to GitLab; GitHub push-mirror configured (commits AND tags).
        DO NOT re-request the push or the mirror setup — they are done.
        REMAINING (user side): tag v0.2.3 on GitLab (pipeline → release with
        net8.0.zip → mirrored to GitHub), install/update on the user's host
        (expect 0.2.3.0), live validation (queue progress, search, delay
        profile). Debug history for the progress fix:
        docs/research/08-live-queue-progress-debug.md.
REQUIRES: user confirmation (direction already expressed: stay on stable host,
          GitHub mirror OK, GitLab release required)
RESEARCH: docs/research/07-native-model-port.md (contracts + packaging recipe)
```

## Goal

Make `Lidarr.Plugin.YtDlp` loadable and fully functional on the user's **stable**
Lidarr host **v3.1.6.5079** (native plugin model), and ship it through:

1. a **GitLab release** (per tag) containing `net8.0.zip` → manual install,
2. a **GitHub mirror** + **GitHub release** → Lidarr UI one-click install + update checks.

No host changes. No pre-release host. No Abstractions model.

## Step 0 — Housekeeping

- [ ] `git submodule add --depth 1 https://github.com/Lidarr/Lidarr ext/Lidarr`
      pinned to tag **v3.1.6.5079** (record the commit sha in `.gitmodules`).
- [ ] Remove the `ext/Lidarr.Plugin.Common` submodule (no longer referenced).
- [ ] Remove `plugin.json` from the repo (host ignores it; avoid confusion).
- [ ] `.gitlab-ci.yml`: add `GIT_SUBMODULE_DEPTH: 1` (Lidarr repo is large).

## Step 1 — Project re-wire (csproj)

`Lidarr.Plugin.YtDlp/Lidarr.Plugin.YtDlp.csproj`:

- [ ] Remove: `Lidarr.Plugin.Abstractions` reference, `Lidarr.Plugin.Common`
      reference, `FluentValidation` PackageReference (now host-provided),
      `Microsoft.Extensions.Logging.Abstractions` PackageReference (NLog replaces it).
- [ ] Add ProjectReferences (none copied to output):
      - `ext/Lidarr/src/NzbDrone.Common/Lidarr.Common.csproj`
      - `ext/Lidarr/src/NzbDrone.Core/Lidarr.Core.csproj`
      - `ext/Lidarr/src/Lidarr.Http/Lidarr.Http.csproj`
      each with `<Private>false</Private>` so host assemblies never land in the
      plugin output folder.
- [ ] Keep: `CliWrap 3.10.1`, `CopyLocalLockFileAssemblies=true` (needed so
      `CliWrap.dll` is in `$(TargetDir)` for ILRepack), `AssemblyName =
      Lidarr.Plugin.YtDlp`, `<InformationalVersion>` (bump to `0.2.0` — first
      native build).
- [ ] Verify a plain `dotnet build` of the three host csprojs works **without**
      the Lidarr frontend (no yarn). If `Lidarr.Core.csproj` demands
      `_output/UI`, add a CI workaround (skip that target / pre-create stub).

## Step 2 — Logging swap (core)

- [ ] Replace `Microsoft.Extensions.Logging.ILogger` with NLog `Logger` in:
      `CliWrapYtDlpRunner`, `DownloadScheduler`, `YtDlpAlbumDownloader`,
      `YtDlpIndexer` (old), `YtDlpDownloadClient` (old), `YtDlpConnectionTester`,
      `SponsorBlockClient`, `YtDlpProbe`.
- [ ] Update all test fakes/hosts that construct these with an `ILogger` to use
      `NLog.LogManager.GetLogger("test")`.
- [ ] `NullLoggerFactory.Instance` usages in tests → drop.
- [ ] Run full test suite — expect green (behavior unchanged).

## Step 3 — Native adapter layer (new code)

New namespace `Lidarr.Plugin.YtDlp.Native` (or root):

- [ ] `YtDlp : NzbDrone.Core.Plugins.Plugin`
      - `Name => "YtDlp"`, `Owner => "w200338"`,
        `GithubUrl => "https://github.com/w200338/lidarr.plugin.ytdlp"`.
- [ ] `YtDlpProtocolMarker`-style constants: `Protocol = "ytdlp"`.
- [ ] `YtDlpIndexer : NzbDrone.Core.Indexers.IIndexer`
      - `Name => "YouTube (yt-dlp)"`, `ConfigContract => typeof(YtDlpIndexerSettings)`,
        `SupportsRss => false`, `SupportsSearch => true`, `Protocol => "ytdlp"`.
      - `Definition` (IndexerDefinition) get/set; `DefaultDefinitions` — one
        default with search enabled (`EnableInteractiveSearch = true`,
        `EnableAutomaticSearch = true` off by default) + default settings.
      - `Fetch(AlbumSearchCriteria)` / `Fetch(ArtistSearchCriteria)`:
        compose query from `criteria.Artist.Name` (+`AlbumTitle`/`Disambiguation`
        for album search) → existing search core (`YtDlpCommandBuilder.BuildSearchArgs`
        + runner + `SearchLineParser`, single-flight, `searchLimit`) → map each
        result to `ReleaseInfo`:
          - `Title` = `"Artist - Title"` composition that Lidarr's parser can split
            (use channel as artist; **verify in Step 7**),
          - `Artist`, `Album` fields set,
          - `DownloadUrl`/`InfoUrl` = `https://www.youtube.com/watch?v=<id>` or
            playlist URL, `Indexer` = Name, `Size = 0`, `PublishDate = now`,
            `Guid` = video id (stable de-dupe key),
          - playlist hits → single "album" release pointing at the playlist.
      - `FetchRecent()` → empty list.
      - `GetDownloadRequest(link)` → `new HttpRequest(link, HttpMethod.GET)`.
      - `Test()` → run `YtDlpConnectionTester` (yt-dlp version probe; ffmpeg
        probe — always required since audio extraction runs through ffmpeg;
        SponsorBlock reachability when enabled) → `ValidationResult`.
      - `RequestAction` → null.
- [ ] `YtDlpClientSettings : IProviderConfig` + `YtDlpIndexerSettings : IProviderConfig`
      - same 8 fields as today, each with `[FieldDefinition]` (see research doc
        §3.5 table), shared defaults (`sponsorBlockApiEndpoint =
        "https://sponsor.ajay.app"`, delay range 10–30, `searchLimit = 25`,
        `reencode = disabled`).
      - `Validate()` → FluentValidation rules (host FluentValidation, v9) —
        reuse `YtDlpSettingsValidator` rules (port, don't reference the old
        class).
- [ ] `YtDlpDownloadClient : DownloadClientBase<YtDlpClientSettings>`
      - `Name => "YouTube (yt-dlp)"`, `Protocol => "ytdlp"`.
      - ctor: base services + our own (runner cache / scheduler / tester) —
        all resolvable from host DI (our exported types + host services + NLog).
      - `Download(remoteAlbum, indexer)`:
        parse `remoteAlbum.Release.DownloadUrl` → album token (`yt:<id>` /
        `playlist:<id>`) → enqueue job in the existing `DownloadScheduler`
        (output dir under settings download root, per-album subfolder, re-encode +
        SponsorBlock args from settings) → return the URL.
      - `GetItems()`: map scheduler jobs → `DownloadClientItem`
        (`DownloadId` = job token, `Title`, `Status` = Queued/Downloading/
        Completed/Failed, `TotalSize`/`RemainingSize`/`RemainingTime` from
        progress events, `OutputPath` = job folder, `Message`,
        `CanBeRemoved = true`).
      - `RemoveItem(item, deleteData)` → scheduler cancel + (deleteData → remove
        job folder).
      - `GetStatus()` → `IsLocalhost = false`, `OutputRootFolders = [download root]`.
      - `GetImportItem(item, previous)` → return item.
      - `MarkItemAsImported(item)` → remove completed job from queue (or no-op).
      - `Test(failures)` → same probes as the indexer `Test()` (ffmpeg always).
- [ ] Delete the old Abstractions-model adapter files
      (`YtDlpPlugin.cs`, old `YtDlpIndexer.cs`, old `YtDlpDownloadClient.cs`,
      `YtDlpSettingsProvider.cs`, `ISettingsProvider` glue) once the native layer
      is green.

## Step 4 — Packaging (native artifact)

- [ ] `ILRepack.targets`: merge set → `Lidarr.Plugin.YtDlp.dll` + `CliWrap.dll`
      **only** (FluentValidation removed from the set — host-provided now).
- [ ] `ZipRelease` target: zip → exactly `Lidarr.Plugin.YtDlp.dll` (no
      plugin.json, no deps.json/pdb).
- [ ] `PackagingTests` re-written:
      - zip contains exactly one entry: `Lidarr.Plugin.YtDlp.dll`;
      - merged DLL does **not** define types in `FluentValidation`, `NLog`,
        `Newtonsoft.Json`, `Lidarr.*`, `NzbDrone.*` (new marker types we define
        ourselves are allowed — assert the exact allow-list),
      - merged DLL **references** `Lidarr.Core`, `Lidarr.Common`, `Lidarr.Http`
        and does **not** reference `CliWrap` or `FluentValidation`,
      - `AssemblyInformationalVersionAttribute` present (UI version display).
- [ ] Add a **type-load smoke test**: load the merged DLL into a fresh
      `AssemblyLoadContext` (default ALC stand-in) and force type initialization
      of `YtDlpIndexer` / `YtDlpDownloadClient` against the host assemblies
      (proves no `TypeLoadException` from signature mismatches — the exact
      failure class that breaks internalized contract deps).

## Step 5 — CI

### GitLab (`.gitlab-ci.yml`)

- [ ] Keep `build` / `test` (junit) / `package` stages; add
      `GIT_SUBMODULE_DEPTH: 1`.
- [ ] New `release` job:
  - `rules: [{ if: '$CI_COMMIT_TAG' }]`,
  - depends on `package`,
  - upload `artifacts/net8.0.zip` to the GitLab release
    (`POST /api/v4/projects/$CI_PROJECT_ID/releases` with `RELEASE_TOKEN`
    project access token, `api` scope; description = tag + manual-install
    instructions + `Minimum Lidarr Version: 3.1.6.5079`).
  - Fallback: if no `RELEASE_TOKEN`, print the manual API command (job stays
    green, artifact remains downloadable).
- [ ] Document the GitLab **push mirror** setup (Settings → Repository → Mirrors →
  Push mirror → `https://github.com/w200338/lidarr.plugin.ytdlp.git` + PAT) in the
  README — automatic, mirrors commits **and tags**, no job needed.

### GitHub (`.github/workflows/release.yml`) — new file

- [ ] `on: { push: { tags: ['v*'] }, workflow_dispatch: {} }`
- [ ] `actions/checkout@v4` with `submodules: 'recursive'`, `fetch-depth: 1`.
- [ ] `actions/setup-dotnet@v4` (8.0.x).
- [ ] `dotnet build` + `dotnet test` (junit optional) +
      `dotnet build -c Release` (triggers merge + zip).
- [ ] `softprops/action-gh-release@v2`:
  - `files: artifacts/net8.0.zip`,
  - body: plugin name, version, manual install instructions, and
    **`Minimum Lidarr Version: 3.1.6.5079`** (required by the host's
    `MeetsMinimumVersion` gate).
- [ ] `GITHUB_TOKEN` is sufficient (contents:write on tag pushes).

## Step 6 — Docs

- [ ] `README.md`: rewrite Install (UI install from GitHub mirror; manual install
      from GitLab release with exact folder
      `/var/lib/lidarr/plugins/w200338/lidarr-plugin-ytdlp/`); settings table;
      "how the host finds the plugin" section; troubleshooting (restart modal,
      `Skipping provider of unknown type` = stale Tubifarry rows).
- [ ] This plan → `STATUS: COMPLETED` with implementation notes.

## Step 7 — Verification (with the user's host)

1. Manual install from the GitLab release zip → restart →
   System → Plugins shows `YtDlp` with installed version;
   Settings → Indexers/Download Clients show **YouTube (yt-dlp)** as a supported
   provider; "Test Connection" passes.
2. Add album → interactive search → results listed → grab → queue item shows
   progress → import completes with correct files. ← **the ReleaseInfo matching
   risk (research §8.1)**
3. UI one-click install from the GitHub mirror release on a fresh folder (or a
   second test instance) → same behavior.
4. Update-check path: bump version + new GitHub release → UI shows "update
   available".

## Acceptance criteria

- [ ] `dotnet test` green (existing core tests + new adapter/packaging tests).
- [ ] Release artifact: single `Lidarr.Plugin.YtDlp.dll` at zip root; no
      host-contract types defined inside; external refs correct.
- [ ] GitLab tag → GitLab release with `net8.0.zip` exists.
- [ ] GitHub tag (mirror) → GitHub release with `net8.0.zip` +
      `Minimum Lidarr Version:` line exists.
- [ ] Plugin visible + functional on the user's 3.1.6.5079 stable host.

## Implementation record (2026-11-12)

Done, with the deviations noted where the verified host forced a different
shape than the plan sketch:

- **Step 0** — `ext/Lidarr` submodule added, pinned to tag `v3.1.6.5079`
  (commit `da7b4dfb`, public URL `https://github.com/Lidarr/Lidarr` so the
  GitHub mirror can clone it); `ext/Lidarr.Plugin.Common` and `plugin.json`
  removed.
- **Step 1** — csproj re-wired as planned. The host csprojs build without the
  frontend. `DisableTransitiveProjectReferences=true` plus a
  `CleanExtraneousOutput` target keep transitive host/NuGet content out of the
  plugin output. Version bumped to `0.2.0`.
- **Step 2** — `ILogger` → NLog `Logger` in all core types. Core logic
  (command builder, runner, scheduler, downloader, SponsorBlock, probes,
  search parser) kept unchanged apart from logging.
- **Step 3** — adapters implemented as `IndexerBase<YtDlpIndexerSettings>` /
  `DownloadClientBase<YtDlpClientSettings>` (plan said raw `IIndexer`; the
  base classes are the host-standard shape — Tubifarry does the same — and
  give `Definition`/`Test()` plumbing for free):
  - `Plugins/YtDlpPlugin.cs` — `NzbDrone.Core.Plugins.Plugin` descriptor
    (`Name "YtDlp"`, `Owner "w200338"`, `GithubUrl` → GitHub mirror).
  - `Protocols/YtDlpDownloadProtocol.cs` — custom `IDownloadProtocol` marker
    in the `NzbDrone.Core.Indexers` namespace (Tubifarry pattern).
  - `Settings/` — shared `YtDlpSettings` POCO (`[FieldDefinition]`, order
    0–7) + `YtDlpIndexerSettings : IIndexerSettings` (`BaseUrl`,
    `EarlyReleaseLimit`, order 8) + `YtDlpClientSettings : IProviderConfig`
    (`DownloadPath`, order 8). One shared rule set via
    `YtDlpSettingsValidator.ApplySharedRules<T>` (FluentValidation
    inheritance across generics was not reliable); the conditional
    SponsorBlock endpoint rule attaches to the property with `.When` at the
    end so `PropertyName` survives.
  - `Runner/YtDlpRunners.cs` — static per-path runner cache with a test seam
    (`Factory` / `ClearCache`); the runner path only exists in provider
    settings, so DI cannot build it.
  - `Probing/` — host-agnostic `ConnectionTestResult`/`ConnectionTestError`;
    `YtDlpConnectionTest.Run/RunAsync` with an `Override` test seam; 90 s
    bound in both provider `Test()` hooks.
  - `Indexing/YtDlpIndexer.cs` — `IndexerBase<YtDlpIndexerSettings>`,
    `Protocol => nameof(YtDlpDownloadProtocol)`, single-flight search through
    the existing builder/runner/parser; `DefaultDefinitions` overridden with
    `Name = null` + interactive search on (the base default would compute
    flags from validation of *empty* settings and leave search off);
    `MapRelease` composes `Title = "Artist - Title"` (host parser verified in
    research §8.1), sets `Artist`/`Album`, `Size = 0`, `PublishDate =
    DateTime.MinValue`; `GetDownloadRequest(link)` → `new HttpRequest(link)`
    (GET).
  - `Download/` — new `DownloadJobState` (thread-safe job record),
    `YtDlpAlbumDownloader` rewritten to mutate it (stderr-tail errors, 500 ms
    progress throttle), `YtDlpDownloadClient` rewritten on
    `DownloadClientBase<YtDlpClientSettings>`: stable `DownloadId` (GUID),
    dedupe while active, host `DownloadItemStatus` mapping, 5-min terminal
    grace, `RemoveItem` via host `IDiskProvider`, `MarkItemAsImported` flag.
  - **No** `DefaultDefinitions` override for the download client — the
    `ProviderFactory` fallback (`Name = ""`, `Implementation` = class name,
    `Settings = new YtDlpClientSettings()`) is exactly what the Add modal
    needs; `SetProviderCharacteristics` fills the display name.
- **Step 4 (packaging)** — merge set is **CliWrap only**; FluentValidation
  now stays external (host-visible `ValidationResult` in `Test()`).
  `CollectHostReferences` stages `Lidarr.Common/Core/Http` into the ILRepack
  library path. Zip = exactly `Lidarr.Plugin.YtDlp.dll`.
- **Step 5 (CI)** — `.gitlab-ci.yml` rewritten: build (sln, Release,
  `-p:NuGetAudit=false -p:EnableAnalyzers=false` because the pinned host
  builds with `TreatWarningsAsErrors=true`) → test (junit logger, **absolute**
  `LogFileName` — relative paths land in the test project dir) → package
  (packaging-test filter) → `release` on tags (GitLab Releases API with
  `JOB-TOKEN: $CI_JOB_TOKEN`, single multipart POST creates release + asset;
  graceful fallback prints a manual curl and keeps the job artifact).
  GitHub mirror workflows added: `.github/workflows/ci.yml` (push/PR) and
  `.github/workflows/release.yml` (tags; `gh release create` with
  `net8.0.zip` + `Minimum Lidarr Version: 3.1.6.5079` in the body — exactly
  what the host's installer checks).
- **Step 6 (docs)** — README rewritten for the native model (install paths,
  delay-profile protocol enabling, release/CI, troubleshooting).
- **Tests** — full native rewrite: 198 tests green in Debug **and** Release
  (fresh clean-tree build simulated, including wiping the host's `_temp`
  intermediates). Packaging suite re-asserts: single-DLL zip, merged
  CliWrap (GUID-prefixed types), host refs external, no host-contract type
  leakage (except the intentional `YtDlpDownloadProtocol` marker), type-load
  + provider-instantiation smoke against the host assemblies.

### Load-compatibility fix (v0.2.1, 2026-11-12)

The user's `v3.1.6.5079` host refused the v0.2.0 artifact with
`ReflectionTypeLoadException` → `Could not load file or assembly 'Lidarr.Core,
Version=10.0.0.17987'`. Reproduced and root-caused against a pristine official
`v3.1.6.5079` host in this environment.

**Problem 1 — assembly references.** The host submodule's
`src/Directory.Build.props` sets `<AssemblyVersion>10.0.0.*</AssemblyVersion>`
("should be replaced by CI" — official CI stamps the release version, e.g.
`3.1.6.5079`; a local build gets a random `10.0.0.x` revision). The .NET
runtime binds a requested host assembly to the already-loaded copy **only when
loaded version ≥ requested version**. `10.0.0.x` > `3.1.6.5079`, so the plugin
ALC load of `Lidarr.Core` fails and the host aborts startup. A bind-test
harness (same host process, only the plugin's requested version varied) proved
it: request `10.0.0.17053` → the exact user error; request `2.1.1.0` or
`1.0.0.0` → loads. This is also why Tubifarry (compiled against the older
`2.1.1.0` release) loads fine on `3.1.6.5079`.

**Problem 2 — custom-attribute blobs (found during live validation).** The
plugin assembly itself loaded, but `GET /api/v1/{indexer,downloadclient}/schema`
500'd with the same FNF. The C# compiler writes full assembly-qualified type
names into custom-attribute blobs: `[FieldDefinition(Type = FieldType.Textbox)]`
encodes `"NzbDrone.Core.Annotations.FieldType, Lidarr.Core, Version=10.0.0.x,
..."` — a string that bypasses the assembly-reference table. The host's schema
builder (`Lidarr.Http.ClientSchema.SchemaBuilder` → `ReflectionExtensions.
GetAttribute` → `System.Reflection.TypeNameParser`) resolves those strings
verbatim when it maps the provider settings types, and `TypeNameParser`
demands the exact requested version. Cecil confirmed it: decoding those
attributes in the v0.2.0 artifact threw
`AssemblyResolutionException: 'Lidarr.Core, Version=10.0.0.x'`.

**Fix (both problems, one lever).** Build with `-p:AssemblyVersion=1.0.0.0`
(CI build step): global MSBuild properties override the submodule's
`10.0.0.*`, so the compiler emits `1.0.0.0` in **both** the assembly-reference
table and every embedded attribute-blob string — and `1.0.0.0` is below every
released (and plausible future) host version, so the plugin binds against any
host. Belt-and-braces: `tools/ref-version-rewriter` (Mono.Cecil, built on demand
by the `RewriteHostReferenceVersions` Release target) re-stamps the
assembly references **and** patches any remaining embedded host-version string
(same-length byte replace, so blob length prefixes stay intact).

**Problem 3 — incremental build clobber (found while validating the fix).**
MSBuild's output copy re-copies the **unmerged** obj DLL over the merged bin
DLL on every no-change rebuild (the `Copy` task's `SkipUnchangedFiles` check
compares sizes first, and the merged assembly is bigger), while the merge
target's stamp made it skip — silently shipping the unmerged DLL. Fixed by
removing the stamp: `MergePluginDependencies` → `RewriteHostReferenceVersions`
→ `ZipRelease` now run on **every** Release build (~3 s, self-healing,
order-independent). Packaging tests are the final guard.

**Verified on a pristine official `v3.1.6.5079` host** (fresh install folder,
no pre-existing plugins): plugin loads with zero load errors; `/api/v1/system/
plugins` lists `YtDlp` / `w200338`; `/api/v1/indexer/schema` returns 11
templates including `YtDlpIndexer` "YouTube (yt-dlp)" with all 9 fields
(correct widget types/labels/advanced flags); `/api/v1/downloadclient/schema`
returns 19 templates including `YtDlpDownloadClient`. 200/200 tests green in
Debug and Release (10 packaging tests, incl. the new embedded-version and
reference-version guards).

### Remaining (needs the user's environment)

1. Push all local commits to the GitLab origin (this environment has no SSH
   and cannot reach `gitlab.gitgudaim.com`).
2. Configure the GitLab push-mirror to `https://github.com/w200338/lidarr.plugin.ytdlp`
   (mirror commits **and tags**) and confirm that GitHub repository name.
3. Tag `v0.2.3` → GitLab release job publishes the zip; mirror tag →
   GitHub release for the UI install path (`v0.2.1` = load-compatibility
   fix only; `v0.2.2` = post-0.2.1 fixes; `v0.2.3` = the fixes listed in
   the post-0.2.2 section below).
4. Live validation on the user's host: manual install to
   `/var/lib/lidarr/plugins/w200338/lidarr-plugin-ytdlp/`, restart,
   System → Plugins shows `YtDlp`; add download client + indexer,
   **enable `YtDlpDownloadProtocol` in the delay profile**, then
   search → grab → download → import. If matching/grab fails, the
   `ReleaseInfo.Title` composition in `YtDlpIndexer.MapRelease` is the
   first thing to adjust (research §8.1).

## Post-0.2.1 fixes (v0.2.2)

Found via live E2E against a pristine official `v3.1.6.5079` host
(search → grab → queue → download → import):

### 1. Installed version displayed as `1.0.0.0`
The CI build override `-p:AssemblyVersion=1.0.0.0` (added to fix
host-contract binding) also squashed the plugin's own `AssemblyVersion`.
The host reads `AssemblyInformationalVersionAttribute` first; the SDK had
appended `+<git hash>` to it, which `PluginVersion.Parse`
(`Version.TryParse` on the part before `-`) rejects, so the host fell back
to the squashed `AssemblyVersion` → `1.0.0.0` (which also made the UI hide
updates, since `1.0.0.0 > 0.2.1.0`).
**Fix:** project now sets `<Version>0.2.2</Version>`,
`<InformationalVersion>0.2.2</InformationalVersion>`,
`<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>`;
the CI `AssemblyVersion` override was removed — host-contract references are
stamped by the `RewriteHostReferenceVersions` target after the merge instead.

### 2. Plugin state invisible across host provider instances (empty queue)
`Download()` was called on one provider instance (host resolves providers by
concrete type via DI; container registrations are per-interface singletons,
so concrete-type resolution can return a different instance than the
interface one), while `GetItems()` polled another → jobs stored in instance
fields were never seen → queue stayed empty and no download ever started.
**Fix:** `SharedClientStates` — a process-wide job store keyed by saved
provider definition id (id 0 = unsaved/test definition → per-instance
fallback so unit tests stay isolated). Regression test: a second client
instance with the same definition id sees jobs enqueued by the first.

### 3. Album search returned unparseable releases
Flat album-search entries carry no title (only `id`/`url`), and
`PublishDate = DateTime.MinValue` rendered as `739931 days`.
**Fix:** each entry is flat-re-fetched once
(`--print '%(.{playlist_title,playlist_channel,playlist_id})j'`) to resolve
the real album title (stripping the page's `Album - ` prefix) and the
canonical `playlist_id`. `ReleaseInfo.Title` = `Artist - <real album>`
(falling back to the criteria album title, then the whole query),
`PublishDate = DateTime.UtcNow`. When a `playlist_id` resolves,
`DownloadUrl` becomes `https://www.youtube.com/playlist?list=...`; otherwise
the `https://music.youtube.com/browse/...` URL is kept (the download client
now accepts `browse:` tokens / browse URLs, yt-dlp downloads them directly).

### 4. Import found 0 tracks: `.webm` is not a host-accepted audio extension
The host's `DiskScanService` filters files by `MediaFileExtensions.Extensions`
(mp2/mp3/m4a/m4b/m4p/ogg/oga/opus/wma/wav/wv/flac/ape/aif/aiff/aifc) —
no `.webm`. yt-dlp's `bestaudio` without `-x` leaves the source container
(webm/opus), so every downloaded file was invisible to import.
**Fix:** the no-re-encode path now uses `-f ba/b -x --embed-metadata`:
`ba` prefers the best audio-only stream, `-x` (without `--audio-format`)
extracts the source codec losslessly into a matching, host-accepted
container (opus → `.opus`), and `-x` also guarantees no video is ever
downloaded even if the `/b` fallback hits a combined stream on a
non-YouTube URL. Side effect: **ffmpeg is now always required** (audio
extraction + metadata embedding), so the connection test checks
`ffmpeg -version` unconditionally.

### 5. Rewriter whole-file UTF-8 pass corrupted the DLL
The defense-in-depth attribute-blob version patch decoded/re-encoded the
entire PE as UTF-8 text, corrupting binary sections.
**Fix:** byte-level in-place patching — scan ASCII
`Lidarr.{Core,Common,Http}, Version=` prefixes in the raw bytes and replace
higher versions with a same-length zero form (e.g. `10.0.0.21720` →
`0.0.0.000000`), preserving every metadata blob length prefix; no-op when
embedded versions are already ≤ target. Packaging tests guard both the
AssemblyRef table and any embedded host version strings in the merged DLL.

### Verified E2E on the official host (v3.1.6.5079)
Search (real album titles, canonical playlist URLs, no absurd age) →
grab (HTTP 200) → queue (`downloading` → `completed`) → download
(11 × `.opus`, audio only) → import (11 tracks moved to
`{root}/Adele/`) → album statistics show the files. 220/220 tests green
in Debug and Release.

## Post-0.2.2 fixes (v0.2.3)

Field-test feedback on v0.2.2 (correct version display, working
search/grab/download/import, but rough edges) drove four fixes:

1. **Queue progress bar** ("no progress visible in the activity screen").
   Two stacked problems:
   a. The host queue row computes `progress = 100 - sizeleft/size` from
      `DownloadClientItem.TotalSize`/`RemainingSize`; we reported 0/0, so
      the bar was NaN. `YtDlpProgressParser` now also parses the current
      file's total size (`of ~ 12.50MiB`) and `ETA 00:05` from yt-dlp's
      `--newline` progress lines; `DownloadJobState` carries them;
      `MapItem` reports `TotalSize`/`RemainingSize`/`RemainingTime` while
      downloading (the bar fills per track, the status message names the
      track; queued/terminal items report 0/0 and hide the bar). The queue
      API maps these to `size`/`sizeleft`/`timeleft` (`QueueService`
      lines 78-80).
   b. Even with correct size fields, the UI still showed nothing: the
      queue rows are rendered from the host's `TrackedDownload.DownloadItem`
      snapshots, which are only refreshed when the host runs
      `RefreshMonitoredDownloads` — a scheduled command on a ~1 minute
      (up to ~90 s, observed in the command API) cadence. Downloads
      shorter than that window only ever appear as
      `Downloading size=0` → `Completed`. Fix (host untouched):
      `YtDlpDownloadClient` now receives the host's `IManageCommandQueue`
      via DI (`ProviderFactory.GetInstance` resolves the provider through
      the host container) and, while a job is not terminal, pushes the
      host's own `RefreshMonitoredDownloadsCommand` (high priority) every
      5 s (`StartQueueRefreshNudge`). The host's command equality
      de-duplicates overlapping pushes, the loop exits when the job goes
      terminal or is removed, and a 60 min cap bounds it. Verified live on
      the official v3.1.6.5079 host: the command history shows refreshes
      at 5 s intervals exactly during the download window (none after),
      and the queue row updates live — status message names the current
      track (`Downloading: 04 - Don't You Remember`) and `size` carries
      the current file's total bytes, so the progress bar renders.
2. **Search speed + `Entry inspection timed out` log spam.** Entry title
   inspection is now bounded-parallel (3 concurrent flat fetches, still
   under the single-flight search gate) instead of serial, so one slow
   entry costs at most one timeout instead of blocking N-1 others; the
   per-entry backstop grew 30s → 60s and inspect args gained
   `--socket-timeout 20` so hung sockets die earlier. Worst case for 10
   entries dropped from 10×T to ~4×T with a much larger T.
3. **Redundant `-f ba/b`** (user observation, verified in yt-dlp source):
   when `-x` is given without `-f`, yt-dlp defaults the format to
   `bestaudio/best` (`yt_dlp/__init__.py` parseOpts: "Do not
   unnecessarily download audio") — exactly what `ba/b` said. Removed from
   both the re-encode and no-re-encode paths; `-x` remains the
   audio-extraction/no-video guarantee.
4. **Docs/tests** for all of the above; 225/225 green in Debug and
   Release (new test: `Active_download_nudges_host_queue_refresh_and_stops_when_terminal`).

### Not changed (expected behavior, for the record)

- `age: 0 minute` on search results: flat search exposes no release date;
  we publish the search time (DateTime.MinValue rendered as an absurd age).
- Titles like `Artist - <searched album>`: that is the fallback for
  entries whose page inspection failed/timed out — the decision engine
  then rejects wrong albums (correct signal) and accepts the right one.
- `Skipping provider of unknown type YouTubeIndexerSettings`: a stale
  provider definition from an old YouTube plugin (e.g. Tubifarry) in the
  host's database; unrelated to this plugin, safe to delete from the UI.
- `Release rejected: YtDlpDownloadProtocol is not enabled for this
  artist`: delay-profile gate; enable the protocol on the artist's delay
  profile.
