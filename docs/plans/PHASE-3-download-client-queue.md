# PHASE 3: Download client + single-flight queue with spacing

STATUS: COMPLETED (2026-11-11)

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

## Implementation notes (2026-11-11)
Deviations from the plan and things worth knowing when reading the code:

- **Token format**: `yt:<videoId>` and `playlist:<id>`, validated charset
  `[A-Za-z0-9_-]`, max 64 chars. `playlist:<id>#N` suffix is accepted and
  stripped (normalizes to the same album). `YtDlpAlbumId.TryParse/ToUrl/
  TryFromUrl` — inverse round-trips are unit-tested.
- **Scheduler** (`Queue/DownloadScheduler.cs`): unbounded
  `Channel<ScheduledJob>` (SingleReader) + one pump task, started lazily from
  `Enqueue`/`Start()`. First job runs immediately; subsequent jobs pause a
  random `[minDelay, maxDelay]` measured from the previous job's FINISH time;
  if idle time already exceeded maxDelay the pause is skipped (spacing budget
  already spent). Injectable `randomSeconds`, `delayFor`, `now` seams — tests
  run with virtual clocks.
- **Scheduler stop semantics (bug found by tests)**: `Channel.Writer
  .CompleteAsync()` does NOT discard queued items, and
  `ReadAllAsync(cancelledToken)` keeps returning items that are already in the
  queue — so `StopAsync` sets a `_stopping` flag, the pump checks it before
  starting each job and CANCELS the CTS of every job it skips (their owners
  mark the item Cancelled). The in-flight job's CTS is tracked as `_activeCts`
  and canceled when `cancelPending: true`. Without the flag, `StopAsync(true)`
  silently ran the whole queue.
- **Downloader** (`Download/YtDlpAlbumDownloader.cs`): buffered `--simulate -J`
  probe (best-effort metadata → `item.Album` + total track count; failures
  non-fatal) then the real streaming download. Progress: stderr parser with a
  500 ms throttle into the item + one un-throttled final sync before terminal
  state. Non-zero exit → Failed with a 15-line/2000-char stderr tail.
- **Cancellation mapping**: the runner contract says OCE is the ONLY
  exception path (cancellation, incl. the 10 s SIGINT→SIGKILL escalation), so
  the downloader catches ANY `OperationCanceledException` and maps to
  Cancelled — a `when (ct.IsCancellationRequested)` filter was too strict (an
  internal runner timeout surfaces as OCE with a different token).
- **Client** (`Download/YtDlpDownloadClient.cs`): implements `IDownloadClient`
  directly (no `BaseStreamingDownloadClient` — that base is HTTP-shaped).
  `ConcurrentDictionary<string, Job>`; duplicate enqueue of the same
  normalized album id + output path while non-terminal returns the EXISTING id
  (no double downloads; documented in the plan's test list). Terminal items
  stay visible for a 5-minute grace period, pruned lazily on
  `GetActiveDownloadsAsync`. `RemoveDownloadAsync` cancels the job CTS and
  best-effort deletes the output directory when `deleteData`.
- **Factory** (`YtDlpPlugin.CreateDownloadClientAsync`): `IPluginContext`
  carries no settings, so the factory reads the validated snapshot from the
  stable `YtDlpSettingsProvider` (the host applies settings to it) and returns
  `null` when `ytDlpPath` is blank.
- **Test-fake pitfall (caused a test-host hang)**: `FakeYtDlpRunner` now
  (a) returns a thread-safe snapshot from `Calls`, (b) treats `StreamException`
  as one-shot, and (c) honors the job token while blocked on `StreamBlock`
  (cancellation completes the block with OCE). A fake that ignored the token
  made `DisposeAsync → StopAsync → await pump` wait forever after a test threw
  before releasing the block.
- **C# closure pitfall (caught by tests)**: scheduler tests that built
  `ScheduledJob.Run` lambdas over a `for` loop variable captured the loop var
  (always final value) and never signaled completion — classic
  `var index = i;` fix.

Test count for the phase: 48 new (114 total, all green).
