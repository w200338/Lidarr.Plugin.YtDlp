# Lidarr.Plugin.YtDlp

Lidarr plugin: YouTube **downloader + indexer** backed by an existing
[yt-dlp](https://github.com/yt-dlp/yt-dlp) installation, with optional
[SponsorBlock](https://sponsor.ajay.app) trimming of non-music parts.

- **Index/search** — searches YouTube (and music.youtube.com) through
  `yt-dlp --flat-playlist` search output and resolves single tracks and
  playlists into Lidarr releases.
- **Download** — downloads the **audio only** (never video) for single tracks
  and whole playlists via yt-dlp, reports real progress, and re-encodes to
  mp3/aac/opus/vorbis when requested. Without re-encoding the source codec is
  kept (lossless extraction, e.g. YouTube's Opus → `.opus` — the host's media
  scanner accepts `.opus` but not `.webm`).
- **SponsorBlock** — when enabled, fetched skip segments (intros, outros,
  off-topic, sponsors, self-promo, previews, interactions) are cut out of the
  downloaded song with ffmpeg.
- **Politeness** — downloads are serialized (one at a time) with a randomized
  delay between them to reduce bot-detection risk.

This is a **native host plugin** (Phase 7 model). It targets the stable Lidarr
host **v3.1.6.5079** and is compiled against the host's own assemblies
(`NzbDrone.Core` / `NzbDrone.Common` / `Lidarr.Http`); the host discovers the
plugin types at startup via its DI container. There is no `plugin.json`.

Requires:
- Lidarr **3.1.6.5079** (stable) or a compatible native-plugins build, .NET 8.
- A working `yt-dlp` the Lidarr process can execute.
- `ffmpeg` on the Lidarr process's PATH (always: audio extraction and metadata
  embedding run through ffmpeg even without re-encoding; re-encoding and
  SponsorBlock additionally need it).

## Repository layout

- `docs/` — research notes (`docs/research/`) and implementation plans
  (`docs/plans/`, one per phase, each with a STATUS line)
- `Lidarr.Plugin.YtDlp/` — plugin source (net8.0, C# 12)
- `Lidarr.Plugin.YtDlp.Tests/` — NUnit 3 + NSubstitute tests (245 tests)
- `ext/Lidarr` — git submodule pinned to tag `v3.1.6.5079` (host source;
  compile-time contract only, `Private=false`, never shipped)
- `tools/zip-packager/` — tiny helper that builds the release zip
- `.gitlab-ci.yml` — GitLab pipeline: build + test + package, and a
  tag-triggered release with the zip as a release asset
- `.github/workflows/` — GitHub mirror CI (build + test on push/PR) and
  release publishing on tags (this is what Lidarr's UI install uses)

## Building

```sh
git submodule update --init --recursive
dotnet build Lidarr.Plugin.YtDlp.sln -c Release
dotnet test  Lidarr.Plugin.YtDlp.Tests/Lidarr.Plugin.YtDlp.Tests.csproj -c Release --no-build
```

The Release build stamps the host-contract references (including the host
versions embedded in attribute blobs, which the host's schema builder
resolves at runtime) to `1.0.0.0` via the `RewriteHostReferenceVersions`
target, so the plugin binds on any Lidarr host version — no special build
flags needed.

The Release build also produces the shippable artifact:

```
artifacts/net8.0.zip   →   Lidarr.Plugin.YtDlp.dll (merged, single entry)
```

Everything the plugin needs except what the host supplies is merged into the
single `Lidarr.Plugin.YtDlp.dll`. **CliWrap** is internalized and renamed.
The host-supplied assemblies (`Lidarr.Core`, `Lidarr.Common`, `Lidarr.Http`,
`NLog`, `FluentValidation`, `Newtonsoft.Json`) are deliberately **not**
merged: the stable host loads its own copies into the plugin's collectible
assembly-load context, and a second copy would break runtime type identity
(provider `Test()` signatures expose host-visible `ValidationResult`/NLog
types). The packaging tests assert all of this on every Release build.

> Packaging internals: the merge is implemented in
> `Lidarr.Plugin.YtDlp/ILRepack.targets`. That file must stay in the project
> folder — its presence suppresses the `ILRepack.Lib.MSBuild.Task` package's
> built-in fallback target, which would otherwise merge *every* dll in the
> output directory (including the host ones) into the plugin.
> `-p:SkipMerge=true` skips the merge+zip step for debugging.

## Installing

### Manual (works on any host, no GitHub needed)

1. Stop Lidarr.
2. Unpack `net8.0.zip` into
   `/var/lib/lidarr/plugins/w200338/lidarr-plugin-ytdlp/` (the *AppData*
   `plugins` folder, i.e. `~/.config/Lidarr/plugins/...` on Linux desktop).
   The folder must contain `Lidarr.Plugin.YtDlp.dll` directly (the host only
   loads `Lidarr.Plugin.*.dll` from `{plugins}/{owner}/{name}/`).
3. Start Lidarr. System → Plugins should list **YtDlp**.

### From GitHub (UI install / update checks)

If this repository has a push mirror on GitHub with releases (see
*Releases & CI* below), System → Plugins → *Install plugin from URL* accepts:

```
https://github.com/w200338/lidarr.plugin.ytdlp
```

The host downloads the latest release's `net8.0.zip`, installs it into the
plugins folder, and restarts. The release body's
`Minimum Lidarr Version: 3.1.6.5079` line gates the install by host version.

### After install (both paths)

1. **Settings → Download Clients → Add → Custom → "YouTube (yt-dlp)"**
   - yt-dlp path (absolute path recommended; the Lidarr process PATH can
     differ from your shell's).
   - Download folder (where raw downloads land before import).
   - Test connection, then save.
2. **Settings → Indexers → Add → Custom → "YouTube (yt-dlp)"**
   - same yt-dlp path + search options; Test connection, then save.
3. **Enable the custom download protocol in your delay profiles** —
   Settings → Profiles → (edit a profile) → *Delay & Protocol* section.
   Lidarr adds the plugin's `YtDlpDownloadProtocol` to existing delay
   profiles **disabled by default**; while it is off, every release from the
   indexer is permanently rejected by the decision engine. Tick it (and
   configure a delay if you like; the plugin already spaces downloads
   itself, so 0 is fine).

## Settings

The two providers have separate settings forms (v0.2.4); they share only
the yt-dlp path. Existing saved configs are unaffected — the host
deserializes stored settings by property name.

**Indexer form:**

| Setting | Type | Default | Description |
|---|---|---|---|
| yt-dlp path | string, **required** | — | Full path to the yt-dlp executable (or `yt-dlp` when on the Lidarr process PATH). |
| search limit | number | `25` | Max results per search (clamped to 1–100). |
| early release limit (days) | number | — | Host standard: ignore releases published more than N days early. |

**Download client form:**

| Setting | Type | Default | Description |
|---|---|---|---|
| yt-dlp path | string, **required** | — | Full path to the yt-dlp executable (or `yt-dlp` when on the Lidarr process PATH). |
| additional args | string | — | Extra yt-dlp arguments appended to every invocation (quote values with spaces). |
| SponsorBlock | checkbox | off | Trim non-music parts via the SponsorBlock API. |
| SponsorBlock endpoint | string | `https://sponsor.ajay.app` | SponsorBlock server base URL (self-hosted supported; scheme may be omitted, `https://` is assumed). |
| re-encode | select | `disabled` | Re-encode audio at max quality: `disabled`, `mp3`, `aac`, `opus`, `vorbis`. `disabled` keeps the source codec (extracted losslessly via ffmpeg). |
| min download delay (s) | number | `5` | Minimum seconds between downloads (start of the randomized pause). |
| max download delay (s) | number | `30` | Maximum seconds between downloads (end of the randomized pause). |
| download folder | path, **required** | — | Root folder where downloads are placed before import. |

### How the download spacing works

Only one download runs at a time. After a download finishes (success or
failure), the next queued download starts after a randomized delay in
`[min, max]` seconds measured from the previous download's end. If the queue
sat idle longer than `max` seconds, the next download starts immediately —
idle time already provided the spacing. Politeness flags are also passed to
yt-dlp itself (`--sleep-requests 1 --retries 10 --retry-sleep exp=2:32`).

### Connection test

When you save either provider, the plugin runs a bounded live check (90 s
timeout): `yt-dlp --version`, `ffmpeg -version`, and the SponsorBlock
endpoint (only when on). Failures
are reported as form validation errors; the plugin-side test never blocks a
save with an unhandled exception.

## Releases & CI

- **GitLab (primary, manual path)** — every push runs
  build → test → package. Pushing a **tag** additionally runs the `release`
  job, which creates a permanent GitLab release on that tag with
  `artifacts/net8.0.zip` attached (via the predefined `CI_JOB_TOKEN`).
  If the API call is not permitted by your GitLab version, the job degrades
  gracefully: it prints a manual `curl` command and keeps the zip as a job
  artifact.
- **GitHub (UI install path)** — the repository is mirrored to
  `https://github.com/w200338/lidarr.plugin.ytdlp` (GitLab push-mirror with
  tags). `.github/workflows/release.yml` builds, tests, and on each tag
  creates a GitHub release whose asset `net8.0.zip` and body
  (`Minimum Lidarr Version: 3.1.6.5079`) satisfy Lidarr's plugin installer.
  `.github/workflows/ci.yml` runs the same build + tests on pushes/PRs.
- **Tagging a release**: `git tag v0.2.4 && git push origin v0.2.4` (or the
  equivalent from your fork) — both pipelines pick the tag up automatically.

## Troubleshooting

- **Plugin not in System → Plugins** — check the Lidarr log for
  `Error starting with plugins enabled` or type-load exceptions; the plugin
  folder must contain `Lidarr.Plugin.YtDlp.dll` at the root of
  `plugins/w200338/lidarr-plugin-ytdlp/` (no subfolders, no extra sidecar
  dlls). The host must be a native-plugins build ≥ 3.1.6.5079.
- **Indexer finds releases but they are never grabbed** — almost always the
  delay-profile protocol: the `YtDlpDownloadProtocol` entry must be ticked
  in the profile's Delay & Protocol settings (it is added *disabled* to
  existing profiles). Also check the release's rejection reason in the
  "Why didn't Lidarr download this?" screen.
- **"yt-dlp not found or not runnable"** — set the yt-dlp path to the
  absolute path of the executable the *Lidarr process* can execute (PATH of
  an interactive shell may differ, especially in Docker).
- **Bot detection / HTTP 403 from YouTube** — keep the download delays
  sensible, update yt-dlp (`yt-dlp -U`), consider a proxy via
  *additional args* (e.g. `--proxy socks5://...`), and avoid downloading
  many playlists at once.
- **SponsorBlock 404** — the video simply has no segments (normal). If the
  endpoint check fails, verify the URL — a scheme-less host is treated as
  `https://`.
- **Fails with "ffmpeg not found"** — install ffmpeg where the Lidarr process
  can find it (PATH); no separate setting exists by design. ffmpeg is
  required for every download (audio extraction + metadata embedding).
- **Downloads land in the wrong place** — the download client's *download
  folder* is where raw files are written; Lidarr's usual import rules then
  move them into your music library.

## Development notes

- Phased implementation, each phase committed: see
  `docs/plans/PHASE-1..7-*.md` (status + implementation notes) and
  `docs/research/` for the backing research (07 covers the native-model port
  and the verified v3.1.6.5079 host contracts).
- The plugin compiles against `ext/Lidarr` (pinned to `v3.1.6.5079`) with
  `Private=false` project references; host DLLs are staging-only for the
  ILRepack library path.
- Tests target `net8.0` and run under the same SDK as CI (198 tests,
  including packaging/type-identity regression guards that run on Release).
- Local dev on non-.NET-8 machines: `DOTNET_ROLL_FORWARD=Major dotnet test`.
