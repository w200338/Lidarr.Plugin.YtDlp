# PHASE 4: Indexer (search via yt-dlp)

STATUS: PENDING

## Goal
`IIndexer` implementation: search YouTube for tracks and albums using yt-dlp and map
results to `StreamingTrack`/`StreamingAlbum`; `GetAlbumAsync` expands an album token
into its tracks so the download client round-trips.

## In scope
- **`YtDlpIndexer : IIndexer, IIndexerWithMetadata`** (no base-class inheritance —
  `BaseStreamingIndexer` is HTTP-API-shaped; a process-driven indexer only borrows its
  query pre/post-processing ideas. Documented in code.):
  - `ServiceName => "YouTube (yt-dlp)"`, `Protocol => "ytdlp"`, `PageSize/MaxPageSize => 25`,
    `SupportsRss => false`.
  - `InitializeAsync` → settings validation (light in P4; full in P5).
  - `SearchTracksAsync(query, ct)` → runner buffered run of
    `BuildSearchArgs(query, min(searchLimit, 25))`; parse pipe-delimited lines into
    `StreamingTrack` (Id=`yt:<id>`, Title, Artist.Name=channel, Duration,
    Metadata[url,type]); skip rows with empty id; cap at searchLimit; dedupe by id.
  - `SearchAlbumsAsync(query, ct)` → music.youtube.com album section:
    `https://music.youtube.com/search?q=<urlencoded>#albums` through the same
    `--flat-playlist --print` builder (URL form instead of `ytsearchN:`); map entries to
    `StreamingAlbum` (Id=`playlist:<id>` when entry is a playlist/url contains
    `list=`, else fall back to single-video album `yt:<id>` with TrackCount=1).
    Entries without a usable id/title are skipped.
  - `GetAlbumAsync(albumId, ct)` → resolve token:
    - `playlist:<id>` → `--flat-playlist --print <template>
      "https://www.youtube.com/playlist?list=<id>"` → tracks (TrackNumber = position,
      Duration, Album.Title from `--print playlist`-level? flat entries lack playlist
      title → best effort: first entry's channel as artist, playlist id kept in
      Metadata; refine in P5 with a `-J` probe if cheap);
    - `yt:<id>` → single-track album (probe via `BuildSimulateProbeArgs` or flat
      search `ytsearchall`-style? NO: `--simulate -J <watchUrl>` → title/channel/
      duration → 1 track).
  - Streaming variants (`SearchAlbumsStreamAsync`/`SearchTracksStreamAsync`):
    simple async-enumerable wrappers over the buffered methods (chunk results) —
    host falls back to non-streaming anyway.
  - Rate-limit courtesy: serialize search invocations (single-flight `SemaphoreSlim`)
    + honor `searchLimit`; errors → empty list + logged (search never throws to host).
- **Search output parse contract** (shared with P2 builder):
  `id|url|title|channel|duration|type` per line; `|` inside values is sanitized by the
  builder template? yt-dlp `--print` doesn't sanitize → parser splits with
  `Split('|', 5)`-style careful parsing (take first 3 from left, last 2 from right;
  title may contain `|`). Unit-tested with messy titles.

## Out of scope
Download execution (P3 done), connection test details (P5), packaging (P6).

## Tests
- Parse: happy path, empty results, malformed lines skipped, `|` in title (left/right
  anchoring), unicode titles, duration missing (0), dedupe, cap enforcement.
- SearchTracksAsync: fake runner → canned lines → assert StreamingTrack mapping
  (id prefix, artist=channel, duration); runner failure → empty list, no throw.
- SearchAlbumsAsync: playlist entries → `playlist:` ids; video-only entries → `yt:`
  fallback; empty url/id skipped.
- GetAlbumAsync: playlist token → N tracks with TrackNumber; `yt:` token → 1 track;
  unknown token format → null.
- IIndexerWithMetadata properties sanity (protocol constant equals download client's).

## Definition of done
`dotnet build` green, `dotnet test` all green, plan status COMPLETED, committed.
