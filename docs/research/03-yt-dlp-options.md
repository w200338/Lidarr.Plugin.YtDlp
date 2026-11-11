STATUS: COMPLETE

# yt-dlp CLI options for the Lidarr plugin (shelling out to an existing yt-dlp install)

Research date: 2026-11-22; target yt-dlp **2026.10.26** (latest release; `master`),
web-only. Verified against `README.md` and source on `master` (`yt_dlp/options.py`,
`yt_dlp/YoutubeDL.py`, `yt_dlp/extractor/youtube/*`, `yt_dlp/postprocessor/*`,
`yt_dlp/downloader/common.py`); help strings quoted verbatim from `options.py`. Version
differences flagged throughout.

## TL;DR

Search: `ytsearchN:artist album` (videos-only) + `--flat-playlist` + `--print` (implies
simulate+quiet) or `-J` for one JSON blob. Download: `-f "ba/b" -x --audio-format
<mp3|aac|m4a|opus|vorbis|flac|alac|wav> --audio-quality` (or presets `-t mp3`/`-t aac`,
≥ 2025.04.30). SponsorBlock works with audio: `--sponsorblock-remove <cats>` cuts segments,
`--sponsorblock-mark <cats>` writes chapters; **custom endpoint flag exists:
`--sponsorblock-api URL`** (default `https://sponsor.ajay.app`). Politeness:
`--sleep-requests`, `--sleep-interval N --max-sleep-interval M` (random sleep in [N, M]),
`--retries`, `--download-archive`; prefer cookies over User-Agent tricks (hidden now).

- Search list: `yt-dlp --flat-playlist --print "%(id)s|%(title)s|%(channel)s" "ytsearch25:artist album"`
- Download+re-encode: `yt-dlp -f "ba/b" -x --audio-format mp3 --audio-quality 0 --embed-metadata --embed-thumbnail -o "<OUT>/%(title)s.%(ext)s" <url>`
- SponsorBlock trim: add `--sponsorblock-remove music_offtopic,sponsor,intro,outro --sponsorblock-api http://sb.internal:3000`
- Validation probe: `yt-dlp --version` + `yt-dlp --simulate -J <known video URL>`

---

## 1. YouTube search prefixes and list-only flags

Per `yt_dlp/extractor/youtube/_search.py` on current yt-dlp (2026.10.26):

- `ytsearchN:QUERY` is the **only** YouTube search prefix (`_SEARCH_KEY = 'ytsearch'`).
  Grammar (`SearchInfoExtractor._search_key_regex`, `common.py`): `{key}(|[1-9][0-9]*|all):query`
  → `ytsearch:Q`, `ytsearch10:Q`, `ytsearchall:Q`. Hard-coded **videos only**
  (`_SEARCH_PARAMS = 'EgIQAfABAQ=='  # Videos only`) — no album/channel result types.
- `ytsearchdate` removed in **2026.02.04** ("Remove broken `ytsearchdate` support", #15959).
  `ytsearchall`, `ytsearchchannel`, `ytschtabchannel`, `ytschtabssearch`, `ytschtabfeeds`
  are also gone from `master` (removed in an older, undocumented-here release). Old docs
  still reference them — treat as unavailable on modern installs; probe with
  `--list-extractors` if you must support old binaries.
- URL equivalents with more control:
  - `https://www.youtube.com/results?search_query=Q&sp=PARAMS` (`YoutubeSearchURLIE`,
    "YouTube search URLs with sorting and filter support"; `sp` = YouTube's base64
    filter/sort params, e.g. date-sort `EgIQAg%3D%3D` from source tests).
  - `https://music.youtube.com/search?q=Q[#section]` (`YoutubeMusicSearchURLIE`,
    "YouTube music search URLs with selectable sections, e.g. #songs"); sections: `albums`,
    `artists`, `community playlists`, `featured playlists`, `songs`, `videos` —
    `songs`/`albums` are today's closest thing to album search.

List-only flags (nothing hits disk):

- `-s, --simulate` — "Do not download the video and do not write anything to disk"
- `--flat-playlist` — "Do not extract a playlist's URL result entries; some entry metadata
  may be missing and downloading may be bypassed" (keeps search cheap: entries are `id`,
  `url`, `title`, `channel`/`uploader`; `duration` may be missing)
- `--print` / `-O TEMPLATE` — "Field name or output template to print to screen, optionally
  prefixed with when to print it, separated by ':'. ... Implies --quiet. Implies --simulate
  unless --no-simulate or later stages of WHEN are used. This option can be used multiple
  times"; `--print json` = full info dict per entry; `--print-to-file FILE TEMPLATE` —
  "Append given template to the file. ... This option can be used multiple times"
- `-J, --dump-single-json` — "Quiet, but print JSON information for each URL or infojson
  passed. Simulate unless --no-simulate is used. If the URL refers to a playlist, the whole
  playlist information is dumped in a single line"; `-j, --dump-json` — per-video JSON
  (deprecated alias of `--print "%()j"`; still works); `--print-json` — like `-j` with
  playlist wrapper
- `--playlist-items, -I SPEC` — "Comma-separated playlist_index of the items to download.
  You can specify a range using '[START]:[STOP][:STEP]'. ... E.g. '-I 1:3,7,-5::2' used on
  a playlist of size 15 will download the items at index 1,2,3,7,11,13,15" (works on
  search result lists too). `--no-playlist`/`--yes-playlist` do not apply to `ytsearch:`
  — the URL *is* the playlist there.

---

## 2. Audio extraction / re-encoding flags

All need `ffmpeg`/`ffprobe` in PATH.

- `-x, --extract-audio` — "Convert video files to audio-only files (requires ffmpeg and ffprobe)"
- `--audio-format FORMAT` (default `best`) — "Format to convert the audio to when -x is
  used. (currently supported: best (default), aac, alac, flac, m4a, mp3, opus, vorbis,
  wav). You can specify multiple rules using similar syntax as --remux-video". Source map
  (`FFmpegExtractAudioPP.ACODECS`): `mp3`→.mp3 (libmp3lame), `aac`→.m4a (ADTS),
  `m4a`/`alac`→.m4a (aac/ALAC), `opus`→.opus, `vorbis`→.ogg, `flac`→.flac, `wav`→.wav;
  `best` = keep source audio (no re-encode unless merge forces it).
- `--audio-quality QUALITY` (default `5`) — "Specify ffmpeg audio quality to use when
  converting the audio with -x. Insert a value between 0 (best) and 10 (worst) for VBR or a
  specific bitrate like 128K (default 5)". `0` ≈ near-transparent; `192K`/`320K` = fixed.
- `-f, --format FORMAT` — "Video format code, see 'FORMAT SELECTION' for more details".
  Pick best audio-only first so no video is downloaded: `-f "ba/b"` (best audio-only,
  fallback to best overall) or `-f "ba[acodec^=mp3]/ba/b"` (prefer native mp3 stream).
- **Presets** — `-t, --preset-alias PRESET` (added **2025.04.30**): "Applies a predefined
  set of options. e.g. --preset-alias mp3. The following presets are available: mp3, aac,
  mp4, mkv, sleep. ... This option can be used multiple times". Source expansions:
  - `-t mp3` ≡ `-f 'ba[acodec^=mp3]/ba/b' -x --audio-format mp3`
  - `-t aac` ≡ `-f 'ba[acodec^=aac]/ba[acodec^=mp4a.40.]/ba/b' -x --audio-format aac`
  - `-t sleep` ≡ `--sleep-subtitles 5 --sleep-requests 0.75 --sleep-interval 10 --max-sleep-interval 20`
  Names are guaranteed stable ("existing preset names will not be changed or removed") but
  only exist on ≥ 2025.04.30 — expand manually for older installs. No opus/vorbis preset.
- Tagging (music): `--embed-metadata` (alias `--add-metadata`) — "Embed metadata to the
  video file. Also embeds chapters/infojson if present unless
  --no-embed-chapters/--no-embed-info-json are used"; `--parse-metadata FROM TO_FIELD` —
  "Parse additional metadata like title/artist from other fields; see 'MODIFYING METADATA'
  for details" (default WHEN `pre_process`; the standard way to derive artist/album/track
  from titles); `--embed-thumbnail` — "Embed thumbnail in the video as cover art" (works
  with `-x`). Info-dict music fields: `track`, `track_number`, `track_id`, `artists`,
  `composers`, `genres`, `album`, `album_type`, `album_artists`, `disc_number` (singular
  `artist`/`composer`/`genre` deprecated); YouTube rarely fills these natively.

---

## 3. Output paths and filename template

- `-o, --output TEMPLATE` — "Output filename template; see 'OUTPUT TEMPLATE' for details". Default: `%(title)s [%(id)s].%(ext)s`. `-o -` = stdout.
- `-P, --paths [TYPE:]PATH` — "The paths where the files should be downloaded. Specify the
  type of file and the path separated by a colon ':'. All the same TYPES as --output are
  supported. Additionally, you can also provide 'home' (default) and 'temp' paths. All
  intermediary files are first downloaded to the temp path and then the final files are
  moved over to the home path after download is finished. This option is ignored if --output
  is an absolute path".
- Placeholders: `%(title)s`, `%(id)s`, `%(ext)s`, `%(uploader)s`, `%(channel)s`,
  `%(playlist)s`, `%(playlist_index)s`, `%(track)s`, `%(track_number)s`, `%(artist)s`
  (deprecated), `%(album)s`, `%(disc_number)s`, printf-style dates
  (`%(upload_date>%Y)s`), `%(view_count)s`, `%(duration)s`, `%(filename)s` (no ext),
  `%(filepath)s`. Filters: `%(field?if present:else)s`, `%(playlist_index&%02d - |)s`
  (zero-padded + " - ", empty if absent), `%%` = literal %, dir hierarchies auto-created.
- `--restrict-filenames` — "Restrict filenames to only ASCII characters, and avoid '&' and spaces in filenames" (cross-platform-safe; `--windows-filenames` milder); `--no-mtime` — "Do not use the Last-modified header to set the file modification time (default)".

---

## 4. SponsorBlock

Group: "Make chapter entries for, or remove various segments (sponsor, introductions, etc.)
from downloaded YouTube videos using the SponsorBlock API (https://sponsor.ajay.app)".

- `--sponsorblock-mark CATS` — "SponsorBlock categories to create chapters for, separated
  by commas. Available categories are sponsor, intro, outro, selfpromo, preview, filler,
  interaction, music_offtopic, hook, poi_highlight, chapter, all and default (=all). You can
  prefix the category with a '-' to exclude it. E.g. --sponsorblock-mark all,-preview"
- `--sponsorblock-remove CATS` — "SponsorBlock categories to be removed from the video
  file, separated by commas. If a category is present in both mark and remove, remove takes
  precedence. The syntax and available categories are the same as for --sponsorblock-mark
  except that 'default' refers to 'all,-filler' and poi_highlight, chapter are not
  available". Source note: "The filler category is very aggressive. It is strongly
  recommended to not use this in a client by default."
- `--sponsorblock-chapter-title TEMPLATE` — chapter title template for marked segments;
  "Defaults to '[SponsorBlock]: %(category_names)l'" (fields: start_time, end_time,
  category, categories, name, category_names)
- `--no-sponsorblock` — "Disable both --sponsorblock-mark and --sponsorblock-remove"
- `--sponsorblock-api URL` — "SponsorBlock API location, defaults to https://sponsor.ajay.app"
  → **yes, a custom endpoint flag exists.** Without a scheme, `https://` is prepended
  (source); request path `{api}/api/skipSegments/{hash4}?...` — any compatible instance
  works.

Categories / display names (`SponsorBlockPP.CATEGORIES`): `sponsor` (Sponsor), `intro`
(Intermission/Intro Animation), `outro` (Endcards/Credits), `selfpromo`
(Unpaid/Self Promotion), `preview` (Preview/Recap), `filler` (Filler Tangent; excluded
from remove `default`), `interaction` (Interaction Reminder), `music_offtopic`
(Non-Music Section), `hook` (Hook/Greetings) — all removable; plus chapter-only
`poi_highlight` (Highlight) and `chapter` (Chapter), not removable. `all` = every
category; `default` = `all` for mark, `all,-filler` for remove.

Compatibility with audio: **works with `-x`** (YouTube extractor only —
`SponsorBlockPP.EXTRACTORS = {'Youtube': 'YouTube'}`, no-op + warning elsewhere). Pipeline
(`get_postprocessors`, `yt_dlp/__init__.py`): `SponsorBlock` PP at `when=after_filter`
fetches segments → chapters; `FFmpegExtractAudio` re-encodes; `ModifyChapters` physically
cuts `--sponsorblock-remove` categories (re-encode around cuts by default;
`--force-keyframes-at-cuts` = faster, less accurate: "This is slow due to needing a
re-encode, but the resulting video may have fewer artifacts around the cuts");
`FFmpegMetadata` writes mark chapters (mark auto-enables `--add-metadata`). So
`--sponsorblock-remove` trims the final audio file. Caveats: whole-video segments ignored,
segments <1s from start snapped to 0; marked chapters only persist in containers that
support them (m4a, flac, wav, mp3-via-ID3; plain ogg/opus do not) → ogg/opus: remove only.

---

## 5. Rate limiting, retries, cookies, user agent, player_client

Sleep family (Workarounds group):

- `--sleep-requests SECONDS` — "Number of seconds to sleep between requests during data
  extraction" (metadata requests only; does not gate downloads)
- `--sleep-interval SECONDS` (alias `--min-sleep-interval`) — "Number of seconds to sleep
  before each download. This is the minimum time to sleep when used along with
  --max-sleep-interval"; `--max-sleep-interval SECONDS` — "Maximum number of seconds to
  sleep. Can only be used along with --min-sleep-interval". Semantics (`YoutubeDL` param
  docs): "Actual sleep time will be a random float from range [sleep_interval;
  max_sleep_interval]" — randomized per download when both given.
- `--sleep-subtitles SECONDS` — "Number of seconds to sleep before each subtitle download"
- **`--max-sleep-requests` and `--sleep-entries` do not exist in current yt-dlp.**
- Official baseline preset: `-t sleep` ≡ `--sleep-subtitles 5 --sleep-requests 0.75
  --sleep-interval 10 --max-sleep-interval 20`. One yt-dlp process = one download at a
  time (playlists sequential) — the concurrency rule reduces to "one yt-dlp process".

Retries / timeouts:

- `--retries RETRIES` — "Number of retries (default is 10), or 'infinite'"
- `--fragment-retries RETRIES` — "Number of retries for a fragment (default is 10), or
  'infinite' (DASH, hlsnative and ISM)"; `--extractor-retries RETRIES` — "Number of
  retries for known extractor errors (default is 3), or 'infinite'"
- `--retry-sleep [TYPE:]EXPR` — "Time to sleep between retries in seconds (optionally)
  prefixed by the type of retry (http (default), fragment, file_access, extractor) ... EXPR
  can be a number, linear=START[:END[:STEP=1]] or exp=START[:END[:BASE=2]]" (exp = backoff)
- `--socket-timeout SECONDS` — "Time to wait before giving up, in seconds"
- `-r, --limit-rate RATE` — caps download bitrate; `-N, --concurrent-fragments N` — "Number
  of fragments of a dash/hlsnative video that should be downloaded concurrently (default is
  1)"; leave at 1 for politeness
- `--download-archive FILE` — "Download only videos not listed in the archive file. Record
  the IDs of all downloaded videos in it" (dedupe mechanism; rename-proof);
  `--break-on-existing` — "Stop the download process when encountering a file that is in
  the archive supplied with the --download-archive option" (default off:
  `--no-break-on-existing`); `--break-per-input` — "Alters --max-downloads,
  --break-on-existing, --break-match-filters, and autonumber to reset per input URL"

Cookies / headers / user agent:

- `--cookies FILE` (Authentication Options) — "Netscape formatted file to read cookies from
  and dump cookie jar in"; `--no-cookies` disables
- `--cookies-from-browser BROWSER[+KEYRING][:PROFILE][::CONTAINER]` — "The name of the
  browser to load cookies from. ..." (host-browser scraping; on a headless service prefer
  an exported Netscape cookies file)
- `--add-headers FIELD:VALUE` (Workarounds) — "Specify a custom HTTP header and its value,
  separated by a colon ':'. You can use this option multiple times"
- `--user-agent UA` / `--referer URL`: still accepted but **hidden and deprecated** in
  current yt-dlp (`optparse.SUPPRESS_HELP`; README "Almost redundant options" maps them to
  `--add-headers "User-Agent:UA"` / `--add-headers "Referer:URL"`); code against
  `--add-headers`.
- `--impersonate CLIENT[:OS]` — "Client to impersonate for requests. E.g. chrome,
  chrome-110, chrome:windows-10. Pass --impersonate='' to impersonate any client. Note that
  forcing impersonation for all requests may have a detrimental impact on download speed
  and stability" (needs optional `curl_cffi` extra); `--list-impersonate-targets` — "List
  available clients to impersonate."
- `--proxy URL`, `-4/--force-ipv4`, `-6/--force-ipv6`.

`player_client` (YouTube extractor args) via `--extractor-args` — "Pass ARGS arguments to
the IE_KEY extractor. See 'EXTRACTOR ARGUMENTS' for details. You can use this option
multiple times to give arguments for different extractors" (CLI allows `-` for `_`).
README verbatim (current): "player_client: Clients to extract video data from. The
currently available clients are `web`, `web_safari`, `web_embedded`, `web_music`,
`web_creator`, `mweb`, `ios`, `visionos`, `android`, `android_vr`, `tv`,
`tv_downgraded`, `tv_samsung`, and `tv_simply`. By default, `tv_samsung,visionos,web` is
used. If no JavaScript runtime/engine is available, then `web` and `tv_samsung` are
omitted. If logged-in cookies ... `web_embedded,tv_samsung,web` ... premium account cookie
... `web_creator,tv_samsung,web` ... Some clients, such as `web_creator` and `web_music`,
require a `po_token` ... You can use `default` for the default clients, or you can use
`all` for all clients (not recommended). You can prefix a client with `-` to exclude it,
e.g. `youtube:player_client=default,-web`". Source: `_DEFAULT_CLIENTS =
('tv_samsung','visionos','web')`, `_DEFAULT_JSLESS_CLIENTS = ('visionos',)`,
`_DEFAULT_AUTHED_CLIENTS = ('web_embedded','tv_samsung','web')`, `_DEFAULT_PREMIUM_CLIENTS =
('web_creator','tv_samsung','web')`. The list churns (`ios_downgraded`/`tv_embedded`
removed 2026.01.31 as broken) — do not hard-code; use `default`. Related:
`youtube:player_skip=configs,webpage,js,initial_data` — "can help reduce the number of
requests needed or avoid some rate-limiting, they could cause issues ..." (opt-in only).
Bot-detection: cookies + default clients + moderate sleeps is the supported path;
`--impersonate` only if the user opts in.

---

## 6. Machine-readable progress / status

- `--newline` — "Output progress bar as new lines"; `--progress-delta SECONDS` — "Time between progress output (default: 0)" (bound tick rate)
- `--progress-template TEMPLATE` — "Template for progress outputs, optionally prefixed with
  one of 'download:' (default), 'download-title:' (the console title), 'postprocess:', or
  'postprocess-title:'. The video's fields are accessible under the 'info' key and the
  progress attributes are accessible under 'progress' key. E.g. --console-title
  --progress-template 'download-title:%(info.id)s-%(progress.eta)s'"
- `--no-progress` — "Do not print progress bar"; `--progress` — "Show progress bar, even
  if in quiet mode"; `--quiet, -q` — "Activate quiet mode. If used with --verbose, print
  the log to stderr"; `--color no_color` (hidden `--no-colors` alias) for ANSI-free stdout.
- Progress attributes in the `progress` dict (`downloader/common.py`):
  `status` (`downloading`/`finished`), `info_dict`, `filename`, `downloaded_bytes`,
  `total_bytes`, `total_bytes_estimate`, `speed`, `eta`, `elapsed`, `_percent`,
  `_percent_str` (+ derived `_*_str`). E.g. `--newline --progress-delta 5
  --progress-template "download:%(progress.status)s %(progress._percent_str)s
  %(progress.eta)s"` = parseable lines.
- One-shot status JSON: `-J` (whole playlist, single line; simulate by default) is the best
  resolution probe; `-j`/`--print-json` per-video. `--write-info-json` — "Write video
  metadata to a .info.json file (this may contain personal information)"; `--load-info-json
  FILE` — "JSON file containing the video information (created with the
  '--write-info-json' option)".
- Exit codes: 0 success / 1 failure (errors on stderr). `--no-abort-on-error` (default) —
  "Continue with next video on download errors; e.g. to skip unavailable videos in a
  playlist"; `--abort-on-error` — "Abort downloading of further videos if an error occurs
  (Alias: --no-ignore-errors)". Single-URL: `--abort-on-error` + exit code = fail-fast.
- `-O/--print` implies `--quiet` and `--simulate` (add `--no-simulate` to print during a real download).

---

## 7. Miscellaneous useful flags

- `--version` — "Print program version and exit" (parse for capability gating: presets need
  ≥ 2025.04.30); `--no-warnings` — "Ignore warnings"
- `--ignore-config` (alias `--no-config`) — "Don't load any more configuration files except
  those given to --config-locations. ..." (recommended: the plugin builds the full command
  line, so user config can't surprise the run)
- `--match-filters FILTER` — "Generic video filter. Any 'OUTPUT TEMPLATE' field can be
  compared with a number or a string using the operators defined in 'Filtering Formats'.
  ... E.g. --match-filters !is_live ..." (e.g. `--match-filters "!is_live"` skips
  livestreams in search results); `--break-match-filters` — "Same as '--match-filters' but
  stops the download process when a video is rejected"
- Overwrite policy family: `--force-overwrites` / `--no-overwrites` / `--force-continue`
  (recommended for a music library: archive + `--break-on-existing`); `--playlist-random`
  — "Download playlist videos in random order"; `-4/-6`, `--concurrent-fragments`,
  `--restrict-filenames`, `--no-mtime` (see above)

---

## Recommended command shapes

Assume `yt-dlp` resolved from PATH; one process at a time; `<OUT>` = Lidarr's track
destination (absolute), passed to `-o`.

1) Search (list-only, machine-parseable, no download):
   ```
   yt-dlp --ignore-config --no-warnings --flat-playlist \
     --print "%(id)s|%(title)s|%(channel)s|%(duration)s" \
     "ytsearch25:DAZN The Art of Electronics"
   ```
   Pipe-delimited per line; `duration` may be empty in flat mode; `--print json`/`-J` for full records. Album lookup: `https://music.youtube.com/search?q=...#albums`.

2) One track, re-encoded to mp3 (explicit; any recent yt-dlp):
   ```
   yt-dlp --ignore-config --no-warnings \
     -f "ba/b" -x --audio-format mp3 --audio-quality 0 \
     --embed-metadata --embed-thumbnail \
     --parse-metadata "title: %(artist|)s - %(album|)s:album" \
     -o "<OUT>/%(playlist_index&%02d - |)s%(title)s.%(ext)s" \
     "https://www.youtube.com/watch?v=VIDEO_ID"
   ```
   (≥ 2025.04.30: `-f "ba/b" -x --audio-format mp3` can be written `-t mp3`.)

3) Official-album playlist → opus, SponsorBlock trim via custom instance:
   ```
   yt-dlp --ignore-config --no-warnings \
     -f "ba/b" -x --audio-format opus --audio-quality 128K \
     --sponsorblock-remove music_offtopic,sponsor,intro,outro,selfpromo,preview,interaction,hook \
     --sponsorblock-api http://sb.internal:3000 \
     --download-archive "<STATE>/yt-dlp.archive" --break-on-existing \
     -o "<OUT>/%(playlist)s/%(playlist_index&%02d - |)s%(title)s.%(ext)s" \
     "https://www.youtube.com/playlist?list=LIST_ID"
   ```
   (ogg/opus can't carry mark-chapters → remove only; `128K` = fixed bitrate).

4) m4a (AAC) with marked chapters (container that stores them):
   ```
   yt-dlp -t aac --audio-quality 128K \
     --sponsorblock-mark all,-filler \
     --sponsorblock-api http://sb.internal:3000 \
     -o "<OUT>/%(title)s.%(ext)s" <URL>
   ```

5) Politeness wrapper for residential connections (≥ 2025.04.30: `-t sleep`):
   ```
   --sleep-requests 1 --sleep-interval 5 --max-sleep-interval 15 \
   --retries 10 --fragment-retries 10 --extractor-retries 3 \
   --retry-sleep exp=2:32 --socket-timeout 30
   ```

6) Validation probe at startup (no disk writes; non-zero exit on failure):
   ```
   yt-dlp --version
   yt-dlp --ignore-config --simulate -J "https://www.youtube.com/watch?v=dQw4w9WgXcQ"
   ```
   Parse JSON for `id` to confirm extraction health before a real run.

---

## Gaps / unknowns

- `sp` filter params for `youtube.com/results` are YouTube-internal base64 strings that
  change without notice; don't hard-code beyond the couple verified in source tests — prefer
  `ytsearchN:`.
- Removal versions of `ytsearchall`/`ytsearchchannel`/`ytschtab*` unknown (older than the
  shipped Changelog window); only `ytsearchdate` (2026.02.04) is documented. Probe
  `--list-extractors` if old installs must be supported.
- Chapter persistence per final container with marked chapters (mp3/m4a/flac/wav) and
  `--audio-quality` per-codec results (passed straight to ffmpeg) are reasoned, not run
  here (web-only); verify on the target box.
- `po_token`/premium-client reliability varies with YouTube rollouts; stick to
  `player_client=default`; treat "Sign in to confirm you're not a bot" as a cookies issue;
  `--impersonate` needs the user's optional `curl_cffi` install (best-effort).
- Search result quality for `artist album` is YouTube's: expect mixes, live streams (guard
  with `--match-filters "!is_live"`), unofficial uploads — plugin-side scoring is essential.

## Sources

All under https://github.com/yt-dlp/yt-dlp (master, release 2026.10.26) unless noted:

- `README.md` (all option help sections) and `Changelog.md` (`ytsearchdate` removal #15959
  in 2026.02.04; client removals 2026.01.31; `--preset-alias` added 2025.04.30); verbatim
  help quotes from `yt_dlp/options.py`
- Search: `yt_dlp/extractor/youtube/_search.py` (prefix, videos-only params); prefix
  grammar in `yt_dlp/extractor/common.py`; clients in `_base.py` (`INNERTUBE_CLIENTS`),
  `_video.py` (`_DEFAULT_*_CLIENTS`)
- SponsorBlock: `yt_dlp/postprocessor/sponsorblock.py` (categories, default API,
  `api/skipSegments` path), `postprocessor/modify_chapters.py` (default chapter title), PP
  ordering in `yt_dlp/__init__.py` (`get_postprocessors`)
- Audio: `yt_dlp/postprocessor/ffmpeg.py` (`FFmpegExtractAudioPP.ACODECS`); progress fields:
  `yt_dlp/downloader/common.py` (`report_progress`); sleep semantics: `yt_dlp/YoutubeDL.py`
- SponsorBlock category docs: https://wiki.sponsor.ajay.app/w/Segment_Categories
