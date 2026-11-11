# PHASE 6: Packaging (single merged DLL + plugin.json + release zip)

STATUS: PENDING

## Goal
Produce the shippable artifact: ONE `Lidarr.Plugin.YtDlp.dll` with all dependencies
internalized + `plugin.json`, zipped as `net8.0.zip` per the host's install convention
(release asset named for the host framework; `Minimum Lidarr Version:` line in notes).

## In scope
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
  target; try first.
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
