# PHASE 5: Connection test, SponsorBlock probe, validation polish

STATUS: COMPLETED (2026-11-11)

## Implementation notes (deviations from the plan above)
- **Synchronous Validate hook design.** `ISettingsProvider.Validate` is synchronous and
  `IPlugin.InitializeAsync` returns a bare `ValueTask` (no result), so there is no
  dedicated "TestConnection" RPC in the contract. The live test is surfaced through
  two paths:
  1. `YtDlpPlugin.InitializeAsync` runs `YtDlpConnectionTester` once at load
     (async, no video probe), caches it in `YtDlpPlugin.LastConnectionTest`, and logs
     failures.
  2. `YtDlpSettingsProvider.ExternalConnectionTest` (internal hook) runs on every
     `Validate`/`Apply` after pure validation passes and a `ytDlpPath` is present.
     It is wrapped in a 15s `CancellationTokenSource` (a wedged binary can't hang a
     settings save; timeout → warning only), results are cached per path for 60s so
     rapid re-saves don't re-probe, and hook crashes degrade to a warning — a save
     is never blocked by the test itself.
- **Fresh runner per live test.** `CliWrapYtDlpRunner` is bound to a binary path, so
  `YtDlpPlugin.RunLiveTestAsync` builds fresh runners for the (possibly changed)
  configured path on each probe; the indexer/download-client factories share one
  runner per path via `YtDlpPlugin._runnerByPath`.
- **Client name:** `SponsorBlockClient` (not `SponsorBlockHttpClient`) — HttpClient is
  injectable, `IDisposable` disposes only when it owns the client. Request timeout is
  a ctor parameter (default 10s, testable) implemented via a linked CTS.
- **ffmpeg check** runs `ffmpeg -version` through a second path-bound runner
  (yt-dlp's runner is bound to the yt-dlp binary); PATH-only, no setting.

## Goal
Make the plugin "testable" in the Lidarr UI: `InitializeAsync` (and a dedicated
`TestConnection`-style validation path surfaced through `ISettingsProvider.Validate`)
checks the external world: yt-dlp binary, ffmpeg (when needed), SponsorBlock endpoint
(when enabled). Errors arrive as user-readable `PluginValidationResult` messages.

## In scope
- **`IYtDlpProbe` / `YtDlpProbe`** (uses `IYtDlpRunner`):
  - `CheckBinaryAsync(path, ct)` → run `--version`; parse `2025.xx.xx` shape; result
    record (Ok, Version, Error). Non-zero/absent → actionable error text
    ("yt-dlp not found at '<path>'. Install from https://github.com/yt-dlp/yt-dlp
    releases or set the full path.").
  - `CheckFfmpegAsync(ct)` → `ffmpeg -version` via same runner seam (probe a
    configurable ffmpeg path — default `ffmpeg` from PATH; add setting? NO — keep
    scope: PATH only, document). Only invoked when `reencode != Disabled` OR
    `useSponsorBlock` (both need ffmpeg post-processing).
  - `ProbeVideoAsync(url, ct)` → `BuildSimulateProbeArgs` → confirms extraction works
    (bot-detection early signal). Optional in Test (can be slow) — include but tolerate
    timeout (30 s) as warning, not failure.
- **`ISponsorBlockClient` / `SponsorBlockHttpClient`** (HttpClient injectable for
  tests):
  - `CheckEndpointAsync(endpoint, ct)` → `GET {endpoint}/api/status` (per research doc
    04); normalize endpoint (trim, prepend `https://` when scheme-less — mirrors
    yt-dlp); 200 + JSON with `uptime` → Ok; 404 → "not a SponsorBlock server";
    4xx/5xx/unreachable → clear messages. 10 s timeout.
  - (Reserved, unused for now) `GetSegmentsAsync(videoId, categories, ct)` for future
    pre-checks — implement + test now since doc 04 has the exact contract; keep
    `categories` defaulting to the non-music set used by the command builder
    (single constant `SponsorBlockCategories.NonMusic` shared by both).
- **`YtDlpConnectionTester`** — composes the above into a `PluginValidationResult`:
  order: binary → (ffmpeg when needed) → (SB endpoint when enabled) → settings
  cross-checks already covered by FluentValidation (P1). All checks run with try/catch;
  each failure appends a message, test does not stop at first failure (Tubifarry
  pattern). Exposed:
  - from `IPlugin.InitializeAsync` (run once at load; failures logged + reported via
    the returned `PluginValidationResult`),
  - and from `YtDlpSettingsProvider.Validate` when settings dict includes
  `ytDlpPath` (host calls Validate on config change → live "Test" behavior).
- Wire `YtDlpPlugin` to construct indexer + download client (replace P1 nulls) sharing
  the applied settings + a single `IYtDlpRunner` instance.

## Out of scope
Packaging (P6). No new user-facing settings beyond existing (ffmpeg from PATH; if
that proves wrong, add in a later minor).

## Tests
- Probe: fake runner returning version strings (valid/invalid/missing), exit codes;
  error message content assertions.
- SponsorBlock client: NSubstitute `HttpMessageHandler` capturing requests → assert URL
  shape (`/api/status`, scheme normalization, categories JSON encoding for
  GetSegmentsAsync), 200/404/500/timeout mappings.
- ConnectionTester matrix: (binary ok/no) × (ffmpeg needed/not) × (SB on/off) →
  expected aggregate result (all pass / which message present); probe timeout →
  warning not failure.
- Integration (Linux-gated, fake yt-dlp script): full `Validate` pass with a fake
  binary + local SB stub? SB stub = tiny python http server is overkill — keep SB to
  the NSubstitute level.

## Definition of done
`dotnet build` green, `dotnet test` all green, plan status COMPLETED, committed.
