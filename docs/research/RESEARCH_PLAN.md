# RESEARCH PLAN — Lidarr.Plugin.YtDlp

STATUS: IN PROGRESS

Research topics to be covered, one by one. Each topic produces a summary doc in `docs/research/`.
Internal knowledge may be outdated — everything must be verified against live sources (web / GitHub / NuGet).

## Topics

| # | Topic | Output | Status |
|---|-------|--------|--------|
| 1 | Lidarr plugin creation guides (RicherTunes/Lidarr.Plugin.Common: CREATE_PLUGIN.md, BUILD_YOUR_FIRST_PLUGIN.md + other docs): base classes, settings model, which NuGet packages / assembly references the plugin needs, plugin folder layout, install into Lidarr, testing approach | `01-lidarr-plugin-guides.md` | DONE (verified) |
| 2 | Tubifarry (TypNull/Tubifarry) — structure of a real, complex Lidarr plugin (project layout, downloader implementation, settings handling, tests) | `02-tubifarry-structure.md` | DONE |
| 3 | yt-dlp CLI options — search (`ytsearchN:`), audio extraction & re-encode (mp3/aac/opus/vorbis), metadata embedding, output templates, rate limiting (`--sleep-*`, retries, proxies), bot-detection notes, `--write-info-json` for metadata-only | `03-yt-dlp-options.md` | PENDING |
| 4 | SponsorBlock — yt-dlp integration flags (`--sponsorblock-*`), whether a custom API endpoint can be passed to yt-dlp, raw SponsorBlock HTTP API (endpoints, JSON shape, categories) as fallback/verification path | `04-sponsorblock-api.md` | PENDING |
| 5 | CliWrap 3.7.x API — exact API surface for building & executing commands (wrap, arguments, quoting, working dir, timeout, exit codes, stdout/stderr, cancellation) | `05-cliwrap-api.md` | PENDING |

## Known discrepancies (already found)

- **CliWrap 3.7.3 does not exist on NuGet.** Flat container index shows `3.7.0`, `3.7.1`, then `3.8.0`/`3.8.1`/`3.8.2`. Pinned to **3.7.1** (highest in the 3.7.x line). If a 3.8.x API difference matters, revisit.

## Resolved questions

- **Q1 (RESOLVED, verified 2026-11-11):** The plugin base types come from `Lidarr.Plugin.Abstractions` + `Lidarr.Plugin.Common`, which are **not on public nuget.org** (BlobNotFound, re-verified). Tidalarr consumes them via a **git submodule at `ext/Lidarr.Plugin.Common`** with ProjectReferences to `src/Abstractions/Lidarr.Plugin.Abstractions.csproj` + `src/Lidarr.Plugin.Common.csproj` (dual-path: packages only when `UseInRepoCommon=false`). Both projects build cleanly (0 errors) with our SDK, all deps resolve (nuget.org + public Lidarr TagLib Azure feed). We will vendor via submodule the same way. Side note: Common itself references **CliWrap 3.10.1**, so our 3.7.1 pin unifies to 3.10.1 in the merged build.

## Open questions

- None blocking. Q1–Q5 are all resolved (see above).
- Deferred: Docker E2E against a real plugins-branch Lidarr image (manual step, documented in Phase 6).
