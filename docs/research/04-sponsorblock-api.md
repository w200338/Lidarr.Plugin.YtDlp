# 04 — SponsorBlock API (sponsor.ajay.app)

**STATUS: COMPLETE**

**TL;DR:** The read API is `GET {api}/api/skipSegments?videoID=<id>` (returns a JSON **array** of segment objects) or the privacy-preserving `GET {api}/api/skipSegments/{sha256prefix4}` (returns a JSON array of `{videoID, segments:[...]}`); both return **404 when no segments exist**, not an error. Segment objects carry exactly: `category`, `actionType`, `segment` (float[2] start/end seconds), `UUID`, `videoDuration`, `locked`, `votes`, `description`. The dedicated "non-music" category for music videos is **`music_offtopic`**; a good "remove non-music parts of a song" set is `music_offtopic,sponsor,intro,outro,selfpromo,preview,interaction,hook` — exclude `filler` (wiki: "very aggressive"), `chapter`/`poi_highlight` (not skippable spans) and `exclusive_access` (full-video label). yt-dlp's `--sponsorblock-remove default` already means "all,-filler", so `--sponsorblock-remove music_offtopic,sponsor,intro,outro,selfpromo,preview,interaction,hook` is the explicit, safe form.

## Endpoint(s)

Base: `https://sponsor.ajay.app` (what `--sponsorblock-api` expects, with or without scheme; yt-dlp prepends `https://` if missing).

1. `GET /api/skipSegments?videoID=<videoID>` — direct lookup. Missing `videoID` → 400 "videoID not specified".
2. `GET /api/skipSegments/{hashPrefix}` — k-anonymity variant. `hashPrefix` = first **4–32** chars of `sha256(videoID)` (4 recommended); returns **all** videos whose hash starts with the prefix, so the client must filter by `videoID`.
3. Legacy: `GET /api/getVideoSponsorTimes?videoID=...` → `{sponsorTimes: [[s,e],...], UUIDs: [...]}` (sponsor/skip only; avoid).
4. Health probes (no video needed): `GET /api/status` (all keys) or `GET /api/status/<key>` → JSON with `uptime`, `commit`, `db`, `startTime`, `loadavg`, `hostname`, ...; `GET /api/ready`.

Query params (both skipSegments endpoints). Parsing (server `parseParams.ts`): the plural params are **JSON-encoded array strings** (e.g. `categories=["sponsor","intro"]`); the singular params accept one or repeated values; with no param, defaults apply.

| Param | Type | Required | Default |
|---|---|---|---|
| `videoID` | string | required (endpoint 1) | — |
| `category` / `categories` | string / JSON string[] | optional | `["sponsor"]` |
| `actionType` / `actionTypes` | string / JSON string[] | optional | `["skip"]` |
| `requiredSegment` / `requiredSegments` | segment UUID (prefix match OK) | optional | `[]` |
| `service` | string | optional | `YouTube` |
| `trimUUIDs` | int (hash endpoint only; chars to keep) | optional | null (full UUID) |

- Error codes: **400** bad params ("No valid categories provided.", "Hash prefix does not match format requirements."), **404** no segments for the video, **500** internal.
- There is **no `videoDuration` query param** on the GET endpoint (wiki lists it, server ignores it; it exists only in the response and in POST submissions).
- Wiki mentions a `prefix` query param for the hash endpoint; the code only reads the path segment — use the path.
- Category/action values are validated against `[a-z|_-]`; unknown entries are silently dropped (empty result → 400).

## Response JSON

`GET /api/skipSegments` → JSON **array** of segment objects (verbatim, live-captured for `9bZkp7q19f0` = PSY – Gangnam Style):

```json
[
  {"category":"music_offtopic","actionType":"skip","segment":[0,4.013],
   "UUID":"05bfff938785e59785ab10c8ea113253cabad513e92888c08850e541c4fd147e6",
   "videoDuration":252.241,"locked":1,"votes":8,"description":""},
  {"category":"music_offtopic","actionType":"skip","segment":[222.313,252.241],
   "UUID":"0d9108a3805de824a832ac4745255c250bf7c069ff4e20617ab129069a0287916",
   "videoDuration":252.241,"locked":1,"votes":3,"description":""}
]
```

Field notes (names are verbatim):
- `segment`: `float[2]` `[start, end]` in seconds. `poi` → start == end; `full` → `[0,0]`.
- `category`: **string** — one category per segment object. The "array" people refer to is the top-level response array (or the `categories` request param), not a field on the segment.
- `actionType`: one of `skip` | `mute` | `full` | `poi` | `chapter` (below).
- `UUID`: 64-char hex (shorter only when `trimUUIDs` used; then `videoID` is needed to re-identify).
- `videoDuration`: float, video duration at submission time; `0` when unknown, ±1 s. Clients use it to reject stale segments — yt-dlp drops a segment when `|duration - videoDuration| >= 1 s` unless the diff is < 5 s AND < 5 % of the segment length.
- `locked`: int 0/1 (VIP-locked). `votes`: int (can be negative; -1 survives, < -1 is filtered).
- `description`: chapter title; empty string `""` for all other categories.
- No `userID` in the response (stripped server-side; the wiki's older model mentioned it).
- Hash endpoint: `GET /api/skipSegments/2691?categories=["music_offtopic"]` → `[{"videoID":"9bZkp7q19f0","segments":[ ...same objects... ]}, ...]` (one entry per matching video); **404 when nothing matches** the prefix.

## Categories

Server `categoryList` (config.ts, 12 values): `sponsor`, `selfpromo`, `exclusive_access`, `interaction`, `intro`, `outro`, `preview`, `hook`, `music_offtopic`, `filler`, `poi_highlight`, `chapter`.

| Category | Meaning | Action types allowed (config `categorySupport`) | Non-music? |
|---|---|---|---|
| `sponsor` | Paid promotion of a product/service | skip, mute, full | yes (for music vids) |
| `selfpromo` | Creator promoting own merch/platforms (Unpaid/Self Promo) | skip, mute, full | yes |
| `interaction` | Like/subscribe/interaction reminders | skip, mute | yes |
| `intro` | Intermission/Intro Animation | skip, mute | yes |
| `outro` | Endcards/Credits | skip, mute | yes |
| `preview` | Preview/Recap clips repeated later | skip, mute | yes |
| `hook` | Hook/Greetings, narrated trailers, goodbyes | skip, mute | yes |
| `filler` | Tangents/Jokes, B-roll, replays | skip, mute | aggressive — exclude |
| `music_offtopic` | "Music: Non-Music Section": only for videos where music is the primary content; segments must be audio not in the official release | skip (only) | **the non-music category** |
| `poi_highlight` | Point of interest (start==end), POI only | poi | no (marker, not a span) |
| `chapter` | Chapter labels (titles only) | chapter | no (not a span) |
| `exclusive_access` | Full-video label (whole video is the topic) | full | no (whole video) |

- yt-dlp's `CATEGORIES` = the 12 minus `exclusive_access` (11).
- **Recommendation for "remove non-music parts of songs":** `music_offtopic,sponsor,intro,outro,selfpromo,preview,interaction,hook`.
  - `filler`: exclude — wiki: "very aggressive. It is strongly recommended to not use this in a client by default"; yt-dlp's own `default` alias is `all,-filler`.
  - `chapter`, `poi_highlight`: not skippable spans (chapter titles / point markers) — yt-dlp explicitly forbids them in `--sponsorblock-remove`.
  - `exclusive_access`: full-video label; not a removable segment at all.

## actionType values

`skip`, `mute`, `full`, `poi`, `chapter` (wiki Types page; no "optInOnly" exists in the current API — that flag from older API revisions is gone; the closest concepts are `exclusive_access` and `poi_highlight`).
- `skip` — cut the span. This is what `--sponsorblock-remove` trims.
- `mute` — silence the span (visuals kept). yt-dlp **does not request mute segments** (`actionTypes=["skip","poi","chapter"]`), so mute-only content is never removed.
- `full` — entire video labelled (segment `[0,0]`). yt-dlp never requests it and ignores `[0,0]` segments, so a full label can't make it delete/discard a download.
- `poi` — point marker (start==end). yt-dlp fetches it only to emit chapters (extends end by 1 s), not to cut.
- `chapter` — chapter title data; yt-dlp uses it for chapter titles only.
- GET default when `actionTypes` is omitted: `["skip"]` only. Legacy compat: if the request includes `poi_highlight` without `poi`, the server adds `poi` and rewrites those segments' `actionType` to `skip`.
- **"Does `--sponsorblock-remove` remove opt-in/full categories?"** No. The remove option's valid set is the 9 span categories (`sponsor intro outro selfpromo preview filler interaction music_offtopic hook`); `poi_highlight`/`chapter` are explicitly excluded from remove, `exclusive_access` isn't in yt-dlp's category list at all; `default` = `all,-filler`.
- yt-dlp request (verbatim): `GET {api}/api/skipSegments/{sha256(video_id)[:4]}?service=YouTube&categories=[...]&actionTypes=["skip","poi","chapter"]`, then filters `d['videoID'] == video_id`.

## Test / probe recipe (C# plugin)

1. Liveness (no video, no 404 ambiguity): `GET {api}/api/status` → expect **200** + JSON containing `uptime`, `commit`, `db`, `hostname`. 404 → not a SponsorBlock server at this URL.
2. Segment sanity check with a known video (verified live during this research):

```
curl -sS 'https://sponsor.ajay.app/api/skipSegments?videoID=9bZkp7q19f0&categories=%5B%22sponsor%22%2C%22intro%22%2C%22outro%22%2C%22selfpromo%22%2C%22preview%22%2C%22music_offtopic%22%2C%22interaction%22%2C%22hook%22%5D&service=YouTube'
```

   (`9bZkp7q19f0` = PSY – Gangnam Style, URL-encoded `categories=["..."]`.)
   Expected: **200** + non-empty JSON array — at research time exactly two `music_offtopic` `skip` segments, `[0,4.013]` and `[222.313,252.241]`, `videoDuration` 252.241.
3. Hash variant (what yt-dlp does; C# computes `SHA256("9bZkp7q19f0") = 26917f86...`, prefix `2691`):

```
curl -sS 'https://sponsor.ajay.app/api/skipSegments/2691?categories=%5B%22music_offtopic%22%5D'
```
   Expected: 200 + array containing `{"videoID":"9bZkp7q19f0","segments":[...]}` (plus other videos sharing the 4-char prefix).

"Healthy" interpretation: 200 + JSON array = endpoint works (non-empty expected for this video); **404 = server up, no segments** for that video/categories (fine as connectivity, wrong as content for this known ID); 400 = bad params or a very old server; 5xx = server fault; DNS/TLS/timeout = endpoint unreachable. Note the public server sets an `ETag` header (default expiry 5 s) — no `If-None-Match` handling needed for a probe.

## Rate limits / etiquette / self-hosting

- No documented per-IP rate limit on the GET endpoints. `rateLimitMiddleware` (config defaults) is wired only to `/api/voteOnSponsorTime` (15 / 15 min) and `/api/viewedVideoSponsorTime` (10 / 15 min), bypassable by VIPs; **429** is documented only for POST submissions. Don't hammer: one request per download is fine.
- Caching: server responds with `ETag` (`etagExpiry` default 5000 ms), Redis cache `expiryTime` default 24 h, DB query caching. Repeated identical GETs are cheap.
- Etiquette/licensing: the API and database follow the SponsorBlock "Database and API License"; an attribution template is provided; the maintainer asks you to open a GitHub issue / email if you use the API; **automated segment submissions are not allowed** (irrelevant for read-only use).
- Why custom endpoints exist: self-hosting (official Docker image; `readOnly: true` mode for read-only mirrors), privacy (hash-prefix k-anonymity queries), and offloading the public server. The official **database** mirrors (sb.ltn.fi, sbmirror.minibomba.pro, mirror.sb.mchang.xyz) do **not** serve the API — pointing a client at them yields no segments; DeArrow-only API mirror: `dearrow.minibomba.pro/sbserver/`.

## Gaps / unknowns

- Public-server CDN/nginx-level limits are undocumented; expect 429 on bursts (not observed during research).
- Wiki lists `videoDuration` and `prefix` as skipSegments query params; the current server code ignores `videoDuration` and only reads the hash from the path.
- The wiki Types page omits `music_offtopic` from its category list (it exists in the Segment_Categories page and in server config); treat server config as canonical.
- The "best segment" thresholds (votes + reputation weighting, overlap grouping) are implementation details that change over time — the wiki itself recommends `/api/skipSegments` over `/api/searchSegments` for client use.
- Live captures above reflect research-time data; `votes`/`locked`/segment boundaries for `9bZkp7q19f0` may shift.

## Sources

- Wiki: https://wiki.sponsor.ajay.app/w/API_Docs · /w/Types · /w/Segment_Categories
- Server (github.com/ajayyy/SponsorBlockServer, branch `master`): `src/app.ts` (routes), `src/config.ts` (categoryList, categorySupport, rateLimit), `src/routes/getSkipSegments.ts`, `src/routes/getSkipSegmentsByHash.ts`, `src/utils/parseSkipSegments.ts`, `src/utils/parseParams.ts`, `src/middleware/requestRateLimit.ts`
- Client (github.com/yt-dlp/yt-dlp, branch `master`): `yt_dlp/postprocessor/sponsorblock.py`, `yt_dlp/options.py`
- Live verification during research: `GET https://sponsor.ajay.app/api/skipSegments?videoID=9bZkp7q19f0...` → 200 (2 segments); `GET https://sponsor.ajay.app/api/skipSegments/2691?...` → 200; `videoID=dQw4w9WgXcQ` → 404 (no segments, as expected).
