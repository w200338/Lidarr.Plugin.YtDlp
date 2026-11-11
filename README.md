# Lidarr.Plugin.YtDlp

Lidarr plugin: YouTube **downloader + indexer** backed by an existing
[yt-dlp](https://github.com/yt-dlp/yt-dlp) installation, with optional
[SponsorBlock](https://sponsor.ajay.app) trimming of non-music parts.

- **Index/search** — searches YouTube (and music.youtube.com) through
  `yt-dlp ytsearch` and resolves single tracks and playlists (flat playlist
  probes) into Lidarr streaming albums/tracks.
- **Download** — downloads single videos and whole playlists via yt-dlp,
  reports real progress, and re-encodes to mp3/aac/opus/vorbis when requested.
- **SponsorBlock** — when enabled, fetched skip segments (intros, outros,
  off-topic, sponsors, self-promo, previews, interactions) are cut out of the
  downloaded song with ffmpeg.
- **Politeness** — downloads are serialized (one at a time) with a randomized
  delay between them to reduce bot-detection risk.

Requires: .NET 8 runtime (host: Lidarr 3.1.2.4913+ plugins build), a working
`yt-dlp`, and `ffmpeg` on PATH (only for re-encoding / SponsorBlock trimming).

## Repository layout

- `docs/` — research notes (`docs/research/`) and implementation plans
  (`docs/plans/`, one per phase, each with a STATUS line)
- `Lidarr.Plugin.YtDlp/` — plugin source (net8.0, C# 12)
- `Lidarr.Plugin.YtDlp.Tests/` — NUnit 3 + NSubstitute tests
- `ext/Lidarr.Plugin.Common` — git submodule providing
  `Lidarr.Plugin.Abstractions` (the host plugin contract)
- `tools/zip-packager/` — tiny helper that builds the release zip
- `.gitlab-ci.yml` — build + test + package on push (dotnet sdk 8.0)
- `plugin.json` — plugin manifest (shipped in the release zip)

## Building

```sh
git submodule update --init --recursive
dotnet build Lidarr.Plugin.YtDlp.sln -c Release
dotnet test  Lidarr.Plugin.YtDlp.Tests/Lidarr.Plugin.YtDlp.Tests.csproj -c Release --no-build
```

The Release build also produces the shippable artifact:

```
artifacts/net8.0.zip   →   Lidarr.Plugin.YtDlp.dll (merged) + plugin.json
```

Everything the plugin needs except the host-provided contract is merged into
the single `Lidarr.Plugin.YtDlp.dll` (CliWrap, FluentValidation — internalized
and renamed). The host-shared assemblies
(`Lidarr.Plugin.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`,
`Microsoft.Extensions.DependencyInjection.Abstractions`) are deliberately
**not** merged: Lidarr's `PluginLoadContext` shares them with the plugin's
collectible assembly-load context, and a second copy would break runtime type
identity.

> Packaging internals: the merge is implemented in
> `Lidarr.Plugin.YtDlp/ILRepack.targets`. That file must stay in the project
> folder — its presence suppresses the `ILRepack.Lib.MSBuild.Task` package's
> built-in fallback target, which would otherwise merge *every* dll in the
> output directory (including the host-shared ones) into the plugin.
> `-p:SkipMerge=true` skips the merge+zip step for debugging.

## Installing

**Manual:** stop Lidarr, unpack `net8.0.zip` into
`{AppDataFolder}/plugins/{Author}/{PluginName}/` (so the folder contains
`Lidarr.Plugin.YtDlp.dll` and `plugin.json` directly), start Lidarr, and
activate the plugin in *Settings → Plugins*. Restart Lidarr after changing
the plugin's settings for the first time if it does not show up.

**Requirements after install:**
- `yt-dlp` reachable (set the full path in the plugin settings, or `yt-dlp`
  if it is on the host PATH of the Lidarr process).
- `ffmpeg` on PATH when using re-encoding or SponsorBlock.

## Settings

| Setting | Type | Default | Description |
|---|---|---|---|
| `ytDlpPath` | string, **required** | — | Full path to the yt-dlp executable (or `yt-dlp` when on PATH). |
| `additionalArgs` | string | — | Extra yt-dlp arguments appended to every invocation (quote values with spaces). |
| `useSponsorBlock` | bool | `false` | Trim non-music parts via the SponsorBlock API. |
| `sponsorBlockApiEndpoint` | string | `https://sponsor.ajay.app` | SponsorBlock server base URL (self-hosted instances supported; scheme may be omitted, `https://` is assumed). |
| `reencode` | enum | `disabled` | Re-encode audio at max quality: `disabled`, `mp3`, `aac`, `opus`, `vorbis`. Requires ffmpeg. |
| `minDownloadDelaySeconds` | number | `5` | Minimum seconds between downloads (start of the randomized pause). |
| `maxDownloadDelaySeconds` | number | `30` | Maximum seconds between downloads (end of the randomized pause). |
| `searchLimit` | integer | `25` | Max results per search (clamped to 1–100). |

### How the download spacing works

Only one download runs at a time. After a download finishes (success or
failure), the next queued download starts after a randomized delay in
`[min, max]` seconds measured from the previous download's end. If the queue
sat idle longer than `max` seconds, the next download starts immediately —
idle time already provided the spacing. Politeness flags are also passed to
yt-dlp itself (`--sleep-requests 1 --retries 10 --retry-sleep exp=2:32`).

### Connection test

When the plugin loads and whenever the settings are saved, the plugin runs a
bounded live check (15 s, cached per yt-dlp path for 60 s): `yt-dlp --version`,
`ffmpeg -version` (only when re-encode or SponsorBlock is on), and the
SponsorBlock endpoint (only when on). Failures show up as user-readable
warnings/errors on the settings form; a plugin-side test crash is degraded to
a warning so valid settings can always be saved.

## Troubleshooting

- **"yt-dlp not found or not runnable"** — set `ytDlpPath` to the absolute
  path of the executable the *Lidarr process* can execute (PATH of an
  interactive shell may differ, especially in Docker).
- **Bot detection / HTTP 403 from YouTube** — keep the download delays
  sensible, update yt-dlp (`yt-dlp -U`), consider a proxy via
  `additionalArgs` (e.g. `--proxy socks5://...`), and avoid downloading many
  playlists at once.
- **SponsorBlock 404** — the video simply has no segments (normal). If the
  endpoint check fails, verify the URL — a scheme-less host is treated as
  `https://`.
- **Re-encode/SponsorBlock fails with "ffmpeg not found"** — install ffmpeg
  where the Lidarr process can find it (PATH); no separate setting exists by
  design.
- **Plugin not loading in Lidarr** — check the Lidarr log for the plugin
  folder; it must contain exactly `Lidarr.Plugin.YtDlp.dll` + `plugin.json`
  (no extra sidecar dlls), and the host must be a 3.1.2.4913+ plugins build.

## Development notes

- Phased implementation, each phase committed: see
  `docs/plans/PHASE-1..6-*.md` (status + implementation notes) and
  `docs/research/` for the backing research.
- Tests target `net8.0` and run under the same SDK as CI; the plugin itself
  ships as a single merged DLL + `plugin.json` (see *Building*).
