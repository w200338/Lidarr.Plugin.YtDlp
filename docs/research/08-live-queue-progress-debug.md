# 08 — Live queue progress: debug record + host-integration debug playbook

Date: 2026-11-13
Shipped in: v0.2.3 (commit `9b4b59f`)
Companion docs: `06-plugin-install-troubleshooting.md` (install flow),
`07-native-model-port.md` (host contracts + packaging).

This document exists so a future agent does **not** have to rediscover the
facts below. It records (a) the exact investigation that fixed "no progress
visible in the activity queue during downloads", and (b) a reusable playbook
for debugging any plugin↔host integration issue in this project.

---

## 1. Symptom

On the official `v3.1.6.5079` host, after the plugin started reporting
`TotalSize`/`RemainingSize`/`RemainingTime` on `DownloadClientItem` while
downloading, the activity queue still showed, for the entire download:

```
t= 10s status=downloading size=0 sizeleft=0 timeleft=None msg='Downloading'
...
t=140s status=completed   size=0 sizeleft=0 timeleft=None msg='Completed'
```

Meanwhile the download folder filled up with 11 `.opus` files — the download
was working fine; only the UI numbers were dead.

## 2. Investigation, in the order actually done

### 2.1 Verify the deployed artifact is current (always #1)

The host loads a *copy* in the plugins folder. Verify identity, not just
"it should be":

```sh
md5sum <repo>/Lidarr.Plugin.YtDlp/bin/Release/net8.0/Lidarr.Plugin.YtDlp.dll \
       /root/.config/Lidarr/plugins/w200338/lidarr-plugin-ytdlp/Lidarr.Plugin.YtDlp.dll
strings -n 4 <deployed dll> | grep -c "<new symbol added in this build>"
```

A stale deployment produced the *exact* same symptom class as a real bug and
would have wasted the whole investigation.

### 2.2 Prove what the plugin itself sees (temporary NLog diagnostics)

Added temporary `[YtDlpDiag]` logs (removed before shipping — grep for the
marker before release):

- downloader: first 5 received lines + final summary
  (`lines=334 updates=11 exit=0 track=11 - Someone Like You total=5117051`)
- client: per-`GetItems()` job snapshot (throttled)

Result: the plugin **did** receive 334 lines, **did** parse them into
progress state (11 updates, correct final track/total). The plugin side was
correct end-to-end.

### 2.3 Rule out double assembly load (the original suspicion)

The host loads plugins in a `PluginLoadContext`; if the host ever loaded two
copies, instance state would split. Cheap definitive test:

```csharp
public static readonly object DiagSentinel = new object();
// log: RuntimeHelpers.GetHashCode(DiagSentinel) + GetType().Assembly.Location
```

Same hash across `GetItems` and `RunJobAsync` → single ALC, single instance.
Suspicion ruled out in minutes. (Diagnostics removed before shipping.)

### 2.4 Isolate the plugin from the host with harness projects

Scratch console projects referencing the plugin **plus the host projects**
(note: the host project files are `Lidarr.Core.csproj` / `Lidarr.Common.csproj`
under `ext/Lidarr/src/NzbDrone.Core/` and `NzbDrone.Common/` — *not*
`NzbDrone.*.csproj`):

| Harness | What it proved |
|---|---|
| `/tmp/runnerprobe` — real `CliWrapYtDlpRunner`, real yt-dlp | Current yt-dlp writes **all** `[download]` progress lines to **STDOUT**; stderr is empty. (Older builds used stderr.) |
| `/tmp/parstest` — real `YtDlpProgressParser`, real lines | Parser correctly tracks file total bytes, remaining bytes, ETA, track name, per-file 100%. |
| `/tmp/pipetest` — real runner + `YtDlpCommandBuilder` + `YtDlpAlbumDownloader` + `DownloadJobState`, polled over time | Full in-flight progress updates **outside the host** (`t=12s pct=9.1 track=02 - Rumour Has It total=3638559`, … `done=11/11`). |

Finding from runnerprobe led to a real (secondary) fix: the downloader was
feeding the parser from **stderr only**; it now feeds **both** streams
(prefix-anchored regexes make cross-stream lines harmless).

After the three harnesses the conclusion was inescapable: **the plugin
pipeline is 100% correct outside the host → the bug is host-side.**

### 2.5 Read the host source for the UI's actual data path

Do not guess where the UI reads from — trace it:

```
frontend/src/Activity/Queue/QueueRow.js
    progress = 100 - (sizeleft / size * 100)      ← size=0 → NaN → no bar
src/Lidarr.Api.V1/Queue/QueueController.cs
src/NzbDrone.Core/Queue/QueueService.cs
    MapQueue: Size = trackedDownload.DownloadItem.TotalSize,  ← SNAPSHOT
               Sizeleft = trackedDownload.DownloadItem.RemainingSize,
               Timeleft = trackedDownload.DownloadItem.RemainingTime
src/NzbDrone.Core/Download/TrackedDownloads/DownloadMonitoringService.cs
    Refresh() calls downloadClient.GetItems() and updates TrackedDownloads
    (only when a RefreshMonitoredDownloadsCommand executes)
src/NzbDrone.Core/Jobs/TaskManager.cs
    schedules RefreshMonitoredDownloadsCommand with Interval = 1 (minute)
src/NzbDrone.Core/Jobs/Scheduler.cs
    static timer, Timer.Interval = 1000 * 30 → pushes due tasks every 30 s
```

Key fact: the queue API does **not** call the plugin's `GetItems()` per
request. It renders `TrackedDownload.DownloadItem` snapshots that only change
when `RefreshMonitoredDownloads` executes.

### 2.6 Measure the real cadence (don't trust the constant)

Live `GET /api/v1/command` history showed `RefreshMonitoredDownloads`
executions **~90 s apart** (1-minute interval + 30 s scheduler tick +
processing), not 60 s. Any download shorter than that window is invisible
in flight: the host samples at best once before and once after completion.

### 2.7 Confirm a plugin-side fix is safe before building it

- `RefreshMonitoredDownloadsCommand` is an **empty public `Command`**
  (`NzbDrone.Core/Download/RefreshMonitoredDownloadsCommand.cs`).
- `CommandEqualityComparer` compares type + non-`Command` properties → an
  empty command with no extra properties is **naturally dedup-safe**
  (repeated pushes collapse if one is already queued/running).
- `ProviderFactory.GetInstance` does
  `_container.GetRequiredService(type)` → the host DI container can
  **inject host services into plugin provider constructors** (this is how
  NLog `Logger` already arrives). So `IManageCommandQueue` can be a
  constructor parameter with an optional default of `null` (unit tests).
- `DownloadMonitoringService.QueueRefresh()` itself does exactly
  `_manageCommandQueue.Push(new RefreshMonitoredDownloadsCommand(),
  CommandPriority.High)` — the plugin just reproduces the host's own
  refresh path, throttled.

## 3. Root cause (final)

The queue UI reads host-side `TrackedDownload.DownloadItem` snapshots that
are refreshed only by the scheduled `RefreshMonitoredDownloads` command
(observed ~90 s cadence). For downloads shorter than that window the UI only
ever sees the pre-progress and terminal snapshots → `size=0` forever, no
progress bar, regardless of what the plugin reports from `GetItems()`.

(Secondary contributor, fixed in the same release: yt-dlp progress lines
were parsed from stderr only, while current yt-dlp writes them to stdout.)

## 4. The fix (v0.2.3)

`YtDlpDownloadClient`:

- New optional ctor parameter `IManageCommandQueue? commandQueue = null`
  (host DI injects it; `null` in unit tests → nudges disabled).
- `StartQueueRefreshNudge(job)` — fire-and-forget task started on enqueue:
  every **5 s** (`DefaultNudgeInterval`, overridable via internal
  `NudgeIntervalOverride` for tests), while the job is not terminal and
  still in the shared state, push
  `new RefreshMonitoredDownloadsCommand()` at `CommandPriority.High`.
  60 min cap; push errors are debug-logged and stop the loop.
- Downloader feeds `YtDlpProgressParser` from **both** stdout and stderr.
- All temporary `[YtDlpDiag]` instrumentation removed before release
  (grep for the marker + `Diagnostics`/`lineCount`/`updateCount` before
  shipping — a pre-release checklist item now).

Host submodule: **untouched** (constraint: stay on stock v3.1.6.5079).

## 5. Verification

- Unit test:
  `YtDlpDownloadClientTests.Active_download_nudges_host_queue_refresh_and_stops_when_terminal`
  — NSubstitute `IManageCommandQueue`, `NudgeIntervalOverride = 50 ms`,
  job held in-flight via `FakeYtDlpRunner.StreamHold`; asserts ≥2 pushes of
  `RefreshMonitoredDownloadsCommand`/High while downloading and that pushes
  stop after completion.
  NSubstitute 5.x note: `ReceivedCalls()` takes **no** lambda; count via
  `c.GetMethodInfo().Name` / `c.GetArguments()` (ICall exposes methods, not
  `MethodInfo`/`Arguments` properties).
- Live E2E on the official host (poll script `/tmp/htest/poll.py`):
  - command API: `RefreshMonitoredDownloads` at **5 s intervals only during
    the download window**, none after completion;
  - queue row: `status=downloading size=4330619 msg='Downloading: 05 - Set
    Fire to the Rain'` — live track names + per-track sizes, progress bar
    renders; `sizeleft`/`timeleft` populate while individual files are in
    flight (on very fast networks each file finishes inside a sample window
    and `sizeleft` reads 0 — the bar then shows 100% for that track, which
    is the honest state).
- 225/225 tests green in Debug and Release; artifact: single merged DLL,
  host refs stamped ≤ 1.0.0.0.

## 6. Reusable playbook (for future agents)

Ordered, cheapest-first. Each step has a kill criterion: stop when the
hypothesis is confirmed/refuted.

1. **Verify the deployed artifact** (hash/size + `strings` for a new
   symbol). Stale deploys mimic real bugs.
2. **Reproduce with a script**, not by eye: grab via
   `POST /api/v1/release`, poll `GET /api/v1/queue` every 3 s
   (use `r.get('timeleft')` — the key may be absent), capture
   `GET /api/v1/command` history before/after.
3. **Isolate plugin vs host** with a scratch harness referencing the plugin
   + host projects (`Lidarr.Core.csproj` / `Lidarr.Common.csproj`). If the
   pipeline works outside the host, stop debugging the plugin and read the
   host.
4. **Trace the UI's data path in host source**: frontend JS → API
   controller → service → storage. Lidarr UIs read snapshots/caches, not
   live plugin calls, for queue-like views.
5. **Measure cadences live** (`/api/v1/command`) instead of trusting
   scheduler constants.
6. **Check host DI**: `ProviderFactory.GetInstance` resolves plugin
   providers through the host container — you can inject any registered
   host service (`IManageCommandQueue`, `IEventAggregator`, …) via an
   optional ctor parameter.
7. **The host command queue is a safe self-service** for plugins: empty
   public `Command` subtypes are dedup-safe (`CommandEqualityComparer`);
   throttle pushes (5 s here) because they may affect *all* providers.
8. **Temporary plugin diagnostics**: the injected NLog `Logger` works;
   prefix with a unique marker, remove before shipping (grep the repo for
   the marker).
9. **Double-ALC check** when state seems to vanish: static sentinel +
   `RuntimeHelpers.GetHashCode` + `Assembly.Location` in one log line.
10. **Never modify the pinned host submodule** for a plugin-side problem;
    prefer DI injection, host command pushes, or documented host behavior.

## 7. Local host test environment cheatsheet

```
host:        /tmp/host316 (official v3.1.6.5079)
appdata:     /root/.config/Lidarr
plugin:      /root/.config/Lidarr/plugins/w200338/lidarr-plugin-ytdlp/Lidarr.Plugin.YtDlp.dll
api root:    http://127.0.0.1:8686/api/v1        (NOT /api/v3)
api key:     d9267299b7f4433d8dcb6da0d5bba145
stop:        pkill -x Lidarr && sleep 2 && rm -f /root/.config/Lidarr/lidarr.pid
start:       cd /tmp/host316 && setsid nohup env DOTNET_ROLL_FORWARD=Major \
              ./Lidarr --nosingleinstancecheck > /tmp/htest/run-XXX.log 2>&1 < /dev/null &
benign log:  "[Error] BrowserService: Couldn't open default browser" (container has no browser)
```

Known endpoints:

```
GET  /api/v1/system/plugins      descriptor + installed/available version
GET  /api/v1/indexer/schema      provider template (also /downloadclient/schema)
GET  /api/v1/release?albumId=N   album search results
POST /api/v1/release             grab (full release JSON back)
GET  /api/v1/queue               queue rows (size/sizeleft/timeleft/msg)
GET  /api/v1/command             command history (refresh cadence!)
GET  /api/v1/album/{id}          album data; import stats under "statistics"
POST /api/v1/command             start a command (singular path)
```

Build/test (repo root, NOT ext/Lidarr):

```
DOTNET_ROLL_FORWARD=Major dotnet build Lidarr.Plugin.YtDlp.sln --configuration Release -v quiet --nologo -p:NuGetAudit=false -p:EnableAnalyzers=false
DOTNET_ROLL_FORWARD=Major dotnet test Lidarr.Plugin.YtDlp.Tests/Lidarr.Plugin.YtDlp.Tests.csproj -c Release --no-build --nologo -v quiet
```
