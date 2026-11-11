# PHASE 3: Download client + single-flight queue with spacing

STATUS: PENDING

## Goal
`IDownloadClient` implementation: enqueue album downloads, run EXACTLY ONE yt-dlp
download at a time, enforce a random [min,max]-seconds pause between consecutive
downloads (bot-detection spacing), report live progress, support cancel/remove.

## In scope
- **Album-id token format** (shared with indexer, P4):
  - `yt:<videoId>` → single-track album → URL `https://www.youtube.com/watch?v=<id>`
  - `playlist:<id>` → playlist album → URL `https://www.youtube.com/playlist?list=<id>`
  - `YtDlpAlbumId` static helpers: `Parse` (returns kind + id, validates charset),
    `ToUrl(token)`, `FromUrl(url)` (inverse). Unit-tested both ways.
- **`DownloadScheduler`** (our "space out downloads" engine):
  - `Channel<YtDlpJob>` queue + one pump task (started lazily/once).
  - Pump loop: dequeue → wait `RandomNextDouble() in [min,max]` seconds since last job
    **finished** (skip the wait when the queue was empty for >max seconds, or first
    job — configurable `IDelaySource` abstraction = injectable `Func<TimeSpan,Task>` for
    tests; random via injectable `Random`/`Func<double,double,double,double>`) →
    execute job (await `IYtDlpAlbumDownloader.DownloadAsync(job)`) → record finish time.
  - Job = `StreamingDownloadItem`-backed record: Id (GUID), AlbumId, OutputPath,
    Status, Progress, CurrentTrack, ErrorMessage, Cts.
- **`YtDlpAlbumDownloader`** (implements the per-album pipeline over the runner):
  - Resolve album via `GetAlbumAsync` (indexer model, P4; for P3 a
    `IYtDlpAlbumResolver` seam: `yt:` → single track (title from `-J` probe or "Track"),
    `playlist:` → `--flat-playlist --print <same pipe template> <playlistUrl>`).
  - Run `BuildPlaylistArgs`/`BuildTrackArgs` via `IYtDlpRunner.RunStreamingAsync`;
    parse stderr progress lines (yt-dlp `[download]  45.2% of ~ 10.0MiB in 00:12 at
    ...`) → `OnDownloadProgress` → item.Progress/CurrentTrack/LastUpdated;
    on exit 0 → Completed (Success=true), non-zero → Failed with stderr tail;
    cancellation → Cancelled; `--break-on-existing`/archive make reruns idempotent.
  - Files land in `outputPath` (Lidarr passes the album destination dir).
- **`YtDlpDownloadClient : IDownloadClient`** (does NOT derive from
  `BaseStreamingDownloadClient` — decision: the base's streaming-HTTP pipeline + DI-ish
  surface adds more than it gives for a process-driven client; we reuse its *ideas*
  (item state, RemoveDownloadAsync semantics) directly. Rationale documented in code.):
  - `InitializeAsync` → validate settings (P5 hooks in later; P3: structural check).
  - `EnqueueAlbumDownloadAsync(albumId, outputPath)` → validate token + settings,
    create item (Queued), enqueue job, return Id.
  - `GetActiveDownloadsAsync` → snapshot of jobs (Queued+Downloading; keep recent
    finished jobs visible for a grace period ~5 min so Lidarr can import — check what
    host expects; simplest: keep terminal items until removed or until 50-item cap).
  - `GetDownloadAsync(id)` → item or null.
  - `RemoveDownloadAsync(id, deleteData)` → cancel (ct) + remove from table +
    delete partial files if requested (best-effort file cleanup in outputPath).
  - `DisposeAsync` → stop pump, cancel all.

## Out of scope
Indexer (P4), SB HTTP probe / full connection test (P5), packaging (P6).

## Tests
- Token parse/round-trip table tests (valid/invalid/charset/urls).
- Scheduler (fake runner that records call order + timestamps via a virtual clock):
  - two enqueued jobs run strictly sequentially (no overlap of fake "download" windows);
  - delay between job N finish and job N+1 start ∈ [min,max];
  - first job starts without delay; empty-queue reset (long idle → no delay);
  - remove/cancel mid-download → job Cancelled, runner cancellation token observed;
  - failed job doesn't block the next; error message captured (stderr tail).
- Album downloader: fake runner emitting canned progress lines → item.Progress
  updates monotonically; exit 0/1/cancel paths; playlist vs single-track arg selection.
- Download client: enqueue→active→completed lifecycle through the public API with fake
  runner; duplicate enqueue of same albumId+outputPath → second returns existing id
  (or queued second — pick one, document it: return existing active id).

## Definition of done
`dotnet build` green, `dotnet test` all green, plan status COMPLETED, committed.
