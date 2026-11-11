# PHASE 6: Packaging (single merged DLL + plugin.json + release zip)

STATUS: COMPLETED (2026-11-11)

## Implementation notes (deviations from the plan + root-cause findings)

**Merge set — plan corrected.** The plan above said to merge `Lidarr.Plugin.Abstractions`
and `Lidarr.Plugin.Common` into the plugin DLL. That is WRONG for this host:
`ext/.../Abstractions/Hosting/PluginLoadContext.cs` shares exactly three assemblies
with the plugin's collectible ALC — `Lidarr.Plugin.Abstractions`,
`Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`
(its `Load(AssemblyName)` returns null for them so they resolve from the host context).
Merging any of them into the plugin DLL creates a second type identity for every
contract/logging type, and the first constructor call taking/returning a shared type
throws `MissingMethodException` at JIT time. The Abstractions reference therefore
stays un-merged. `Lidarr.Plugin.Common` is not merged because it is simply not
referenced: this plugin uses only Abstractions, and Common drags a heavy closure
(DataProtection, TagLibSharp, M.E.* ...) that can neither be merged (host-shared
types) nor shipped (forbidden sidecars).

Final merge set: **only `CliWrap` + `FluentValidation`**, internalized + renamed.
Resulting merged DLL (Release): 861,696 B, external references limited to
`Lidarr.Plugin.Abstractions`, `Microsoft.Extensions.Logging.Abstractions` and
`System.*` framework assemblies (verified by
`PackagingTests.Merged_dll_references_only_framework_and_host_shared_assemblies`).

**CRITICAL tooling finding: the ILRepack package ships a fallback merge target.**
`ILRepack.Lib.MSBuild.Task` 2.0.48 defines its own `ILRepack` target in its
`build/*.targets` that runs on every Release build **unless the project contains a
local `ILRepack.targets` file**. The fallback merges *every* DLL in the output
directory (InputAssemblies = main + `$(OutputPath)*.dll`), i.e. it swallowed
`Lidarr.Plugin.Abstractions.dll` and both `Microsoft.Extensions.*.dll` sidecars into
the plugin — which is exactly what produced 97 Release-only `MissingMethodException`
test failures (type-identity split across the ALC; `ILogger` in plugin metadata
pointed at the plugin assembly instead of the shared Abstractions DLL). The fix:
`Lidarr.Plugin.YtDlp/ILRepack.targets` exists, which suppresses the fallback and
hosts our controlled `MergePluginDependencies` target. Also note: `ILRepack`
task semantics are `InputAssemblies` = assemblies to merge, `LibraryPath` = search
dirs only (it does NOT auto-pull references); `RenameInternalized` prefixes
internalized type names with a per-assembly GUID (e.g.
`CliWrap.<guid>Command`) while keeping the namespace. The merge target is
incremental via a stamp file (`obj/.../plugin-merge.stamp`) so an already-merged
bin DLL is never re-merged with the sidecars, and the merge input is the raw csc
output from `obj`, never the merged bin copy. `-p:SkipMerge=true` skips
merge+zip for bisection/debug.

**Packaging.** `tools/zip-packager` (tiny dependency-free console tool) creates
`artifacts/net8.0.zip` with exactly `{Lidarr.Plugin.YtDlp.dll, plugin.json}` at the
zip root (MSBuild property functions can't call `ZipFile.CreateFromDirectory`).
`CopyLocalLockFileAssemblies=true` makes the NuGet sidecars land in the output dir
for the merge; the zip is built from an explicit file list, so sidecars never ship.

**CI.** `.gitlab-ci.yml`: stages `build` → `test` → `package`, image
dotnet/sdk:8.0, `GIT_SUBMODULE_STRATEGY: recursive` (Abstractions come from the
submodule). Build publishes plugin+test output and the zip; test runs the full
suite `--no-build`; package re-runs the `FullyQualifiedName~Packaging` filter
against the produced zip and publishes `artifacts/net8.0.zip` (30 days).

**Packaging tests** (`Lidarr.Plugin.YtDlp.Tests/Packaging/PackagingTests.cs`, 9 tests):
merged-DLL size floor; external references limited to framework + host-shared;
internalized CliWrap/FluentValidation types present (GUID-suffix match);
**merged DLL must not DEFINE any `Lidarr.Plugin.Abstractions.*` or
`Microsoft.Extensions.*` type** (regression guard for the fallback-merge bug);
entry type implements host-shared `IPlugin`; zip has exactly the 2 expected root
entries; zipped DLL is byte-identical (SHA-256) to the build output; no forbidden
sidecars in zip; `plugin.json` fields consistent with the artifact.

**Verification (2026-11-11).** Full clean Release build: 0 warnings/0 errors.
`dotnet test -c Release --no-build`: 195/195 passed. Packaging filter: 9/9.
`unzip -l artifacts/net8.0.zip` → exactly `Lidarr.Plugin.YtDlp.dll` + `plugin.json`.
Debug build also 0 warnings, 195/195 (packaging tests pass there too when a
Release build is present on disk; they `Assert.Ignore` otherwise).

---

## Goal
Produce the shippable artifact: ONE `Lidarr.Plugin.YtDlp.dll` with all dependencies
internalized + `plugin.json`, zipped as `net8.0.zip` per the host's install convention
(release asset named for the host framework; `Minimum Lidarr Version:` line in notes).

## In scope (as originally planned — superseded by the notes above)
- **Merge strategy**: ILRepack via `ILRepack.Lib.MSBuild.Task` (MSBuild task, like
  Tubifarry) OR Tyrrrz `ILRepack` dotnet tool in CI — decide at implementation:
  MSBuild task integrates into `dotnet build` (preferred for CI parity).
  Internalize: `Lidarr.Plugin.Abstractions`, `Lidarr.Plugin.Common` (and their
  transitive runtime deps that aren't host-provided). FORBIDDEN sidecars (Tidalarr
  expected-contents list): no `Lidarr.Plugin.Abstractions.dll`/`Common.dll`, no
  `Lidarr.*`/`NzbDrone.*`, no `Newtonsoft.Json`/`NLog` etc. next to the plugin DLL.
  `PrivateAssets=all`/`ExcludeAssets=runtime` on plugin package references so they
  compile-time-only (they get merged, not copied).
  **Risk note:** Abstractions+Common pull heavy transitive deps (DataProtection,
  Spectre.Console, TagLibSharp...). Merging all of them bloats the DLL; if internalize
  gets hairy, fallback = copy the *required* runtime deps as sidecars (only allowed
  ones — verify against Tidalarr forbidden list) — but single-DLL is the documented
  target; try first. [SUPERSEDED: see notes — Abstractions must NOT be merged; Common
  is not referenced at all; only CliWrap+FluentValidation are merged.]
- **`plugin.json`** finalization: `id "ytdlp"`, `name "YtDlp"`, `version` from csproj,
  `apiVersion "1.x"` (must equal Abstractions major), `main
  "Lidarr.Plugin.YtDlp.dll"`, `commonVersion` = submodule version, `minHostVersion`
  (set to the plugins-branch host we validate against; document it), `targetFramework
  "net8.0"`.
- **Packaging target** `dotnet pack`-style or plain MSBuild target `ZipRelease`:
  output `artifacts/net8.0.zip` containing exactly `{Lidarr.Plugin.YtDlp.dll,
  plugin.json}` at zip root.
- **Packaging test** (in test project): assert built output dir contains the merged
  DLL + plugin.json and NONE of the forbidden sidecars; manifest fields consistent
  (apiVersion regex `^\d+\.x$`, main file exists).
- **CI**: add `package` job (after test) producing `artifacts/net8.0.zip` artifact;
  README "Building & installing" section: manual install path
  `{AppDataFolder}/plugins/{Owner}/{Name}/` + restart, and GitHub-release install path.
- **README** final: features, settings table, requirements (yt-dlp ≥2025.04.30
  recommended, ffmpeg for re-encode/SB), how bot-detection spacing works, troubleshooting.

## Out of scope
Docker E2E against a real plugins-branch Lidarr image (needs Docker + nightly image;
document as manual step instead of automated CI).

## Definition of done
`dotnet build -c Release` green, `dotnet test` green incl. packaging test,
`net8.0.zip` produced with exactly 2 files, plan status COMPLETED, committed.
