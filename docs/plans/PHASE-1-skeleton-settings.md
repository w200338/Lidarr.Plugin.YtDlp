# PHASE 1: Project wiring + plugin skeleton + settings

STATUS: PENDING

## Goal
Make the plugin project build against the real Lidarr plugin contracts and expose the
plugin entry point + the full settings surface (all user settings) through
`ISettingsProvider`. Everything compiles; settings validate.

## In scope
- `Lidarr.Plugin.YtDlp.csproj`: ProjectReferences to the `ext/Lidarr.Plugin.Common`
  submodule (`src/Abstractions/Lidarr.Plugin.Abstractions.csproj` +
  `src/Lidarr.Plugin.Common.csproj`), `FluentValidation 9.5.4` (same version Common uses;
  Common's own reference is `PrivateAssets=all` so we must take it directly),
  `Microsoft.Extensions.Logging.Abstractions` (for `ILogger` in bases), keep `CliWrap 3.7.1`.
- `plugin.json` manifest at repo root (copied to output): `id: "ytdlp"`, `name: "YtDlp"`,
  `version: "0.1.0"`, `apiVersion: "1.x"`, `main: "Lidarr.Plugin.YtDlp.dll"`,
  `commonVersion` from submodule.
- `YtDlpSettings` POCO (single settings object shared by indexer + download client,
  flat key namespace per `ISettingsProvider` contract):
  | key | type | default |
  |---|---|---|
  | `ytDlpPath` | String, required | `""` |
  | `additionalArgs` | String | `""` (raw CLI string, split later) |
  | `useSponsorBlock` | Boolean | `false` |
  | `sponsorBlockApiEndpoint` | String | `https://sponsor.ajay.app` |
  | `reencode` | Enum `ReencodeFormat {Disabled, Mp3, Aac, Opus, Vorbis}` | `Disabled` |
  | `minDownloadDelaySeconds` | Number (double) | `5` |
  | `maxDownloadDelaySeconds` | Number (double) | `30` |
  | `searchLimit` | Integer | `25` (indexer result count) |
- `YtDlpSettingsProvider : ISettingsProvider` — `Describe()` (SettingDefinitions incl.
  AllowedValues for the enum), `GetDefaults()`, `Validate(dict)` (map dict→POCO, run
  FluentValidation `YtDlpSettingsValidator`, map to `PluginValidationResult`), `Apply(dict)`
  (validate + store). Validation rules: ytDlpPath non-empty (file-exists check happens in
  Phase 5 connection test, keep Validate pure/deterministic), min ≤ max, both > 0,
  endpoint absolute URL when `useSponsorBlock`, searchLimit 1..100, reencode in set,
  additionalArgs parseable (tokenize, no empty tokens).
- `YtDlpPlugin : IPlugin` — `Manifest` via `PluginManifest.Load` (fallback like Tidalarr),
  `InitializeAsync` stores `IPluginContext` logger, `SettingsProvider` wired,
  `CreateIndexerAsync`/`CreateDownloadClientAsync` return `null` for now (real in P3/P4).
  `DisposeAsync` stub.

## Out of scope
Any yt-dlp execution, indexer logic, download logic, packaging/ILRepack (P2–P6).

## Tests
- Settings provider: defaults match POCO; Describe() exposes all 8 keys with correct
  DataType/AllowedValues; Apply round-trip stores values; validation failure matrix
  (missing path, min>max, negative delays, bad URL with SB on, bad reencode value,
  searchLimit 0/101) using NUnit `Assert.That` style; NSubstitute used where a seam exists.
- Plugin: `Manifest` loads the copied `plugin.json` from output; `SettingsProvider`
  non-null; Create* return null.

## Definition of done
`dotnet build` green (0 errors), `dotnet test` all green, plan status COMPLETED, committed.
