# Tubifarry Structure Analysis (github.com/TypNull/Tubifarry)

## 1. STATUS: COMPLETE

Tubifarry (tag v2.3.1, analyzed at commit `22c3e66`, 2026-11-04) is a large, multi-provider Lidarr plugin (~350 .cs files, single project) that downloads music from YouTube, Soulseek (slskd), Lucida, Streamrip, Qobuz, SubSonic, TripleTriple, and DAB Music, with YouTube/Spotify/ListenBrainz indexers and import lists, plus a "metadata proxy" layer that wraps other Arr metadata sources (Deezer, Discogs, Last.fm, SkyHook, CustomLidarr) and notifications (FlareSolverr, queue cleaner, ReplayGain). Its YouTube client is the closest analog to YtDlp (YouTube downloads + SponsorBlock + anti-bot session handling), and it demonstrates the full complex-plugin anatomy: thin `DownloadClientBase` facade + generic base download manager + request queue, per-provider settings with FluentValidation, an `ExtendedHttpIndexerBase` indexer pattern, rate limiters/circuit breakers, and external-process + HTTP utilities. It has **no tests at all** (a finding in itself, see §8). It references Lidarr **source via a git submodule**, not release DLLs, and repacks its dependencies into one DLL with ILRepack.

## 2. Repo & project layout

```
Tubifarry/
├── Tubifarry.sln                 # Plugin project + submodule Lidarr projects
├── Directory.Build.props         # Borrowed from Lidarr's build system (OutputPath conventions etc.)
├── NuGet.config                  # Adds nuget.org (+ local fallback)
├── stylecop.json
├── Submodules/Lidarr             # GIT SUBMODULE → github.com/Lidarr/Lidarr (pinned commit)
├── .github/workflows/            # git-info.yml, dotnet-setup.yml, compilation.yml, packaging.yml,
│                                 #   publishing.yml, release-notes.yml, metadata.yml, branch-detection.yml
└── Tubifarry/                    # THE single plugin project
    ├── Plugin.cs                 # Plugin entry class (NLog config, Sentry, DelayProfile auto-allow)
    ├── Tubifarry.csproj
    ├── PluginInfo.targets        # Generates PluginInfo.cs constants at build time (WriteLinesToFile)
    ├── PluginKeys.targets, PreBuild.targets, Debug.targets, ILRepack.targets
    ├── Core/
    │   ├── FFmpeg/               # FFmpeg download/management + AudioProcessingService (tagging/re-encode)
    │   ├── Model/                # ApiCircuitBreaker, FileCache, PlaylistItem, …
    │   ├── Python/               # uv-managed python + fpcalc for fingerprinting (embedded .py resources)
    │   ├── Records/              # POCOs for APIs (YouTubeSession, MusicBrainzData, …)
    │   ├── Replacements/         # ExtendedHttpIndexerBase, FlexibleHttpDispatcher,
    │   │                         #   download-decision comparers, HiddenProviderStartupFilter
    │   ├── Telemetry/            # Sentry helpers, Slskd tracking buffer
    │   └── Utilities/            # ProcessRunner, PluginSettings (own JSON store), CookieManager,
    │                             #   CacheService, UserAgentValidator, ReleaseFormatter, …
    ├── Download/
    │   ├── Base/                 # BaseDownloadManager<TReq,TOpts,TClient>, BaseDownloadRequest,
    │   │                         #   BaseDownloadOptions, BaseHttpClient
    │   └── Clients/<Provider>/   # One folder per provider: *Client.cs (DownloadClientBase),
    │                             #   *DownloadManager.cs, *ProviderSettings.cs, *DownloadRequest.cs
    ├── Indexers/<Provider>/      # *Indexer.cs, *IndexerSettings.cs, *RequestGenerator.cs, *Parser.cs,
    │                             #   *Records.cs, *SessionHelper.cs (per provider)
    ├── ImportLists/              # ListenBrainz (×4), Spotify, LastFmRecommend, ArrStack soundtrack import
    ├── Metadata/                 # Proxies (Deezer/Discogs/Lastfm/SkyHook/CustomLidarr/Mixed), Lyrics,
    │                             #   Converter, Beets, FFmpeg, ScheduledTasks (SearchSniper)
    ├── Notifications/            # FlareSolverr, QueueCleaner, ReplayGain, PlaylistExport, YouTubeProxy
    └── Blocklisting/
```

Conventions: `internal` indexers, `public` download clients; each provider has a unique
`Protocol` type (e.g. `YoutubeDownloadProtocol`) used to link the download client and indexer.
`PluginInfo` (Name/Author/Version/GitCommit/GitTag/Branch/MinimumLidarrVersion/CI) is generated
by MSBuild `WriteLinesToFile` in `PluginInfo.targets` before compile — no hand-maintained constants.

## 3. Lidarr assembly references (important)

**Not HintPath.** Tubifarry keeps the entire Lidarr source as a **git submodule** and builds against it with `ProjectReference`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>Lidarr.Plugin.$(MSBuildProjectName)</AssemblyName>   <!-- Lidarr.Plugin.Tubifarry -->
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Submodules\Lidarr\src\NzbDrone.Common\Lidarr.Common.csproj" />
    <ProjectReference Include="..\Submodules\Lidarr\src\NzbDrone.Core\Lidarr.Core.csproj" />
    <ProjectReference Include="..\Submodules\Lidarr\src\Lidarr.Http\Lidarr.Http.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>
```

- `.gitmodules`: `Submodules/Lidarr` → `https://github.com/Lidarr/Lidarr/`, pinned to commit
  `33cea31a27f7` (2026-11-04, i.e. recent `develop`/master, not a release tag). So the plugin
  tracks **Lidarr HEAD** and gets compiled against `NzbDrone.Core` / `NzbDrone.Common` source.
- CI passes `-p:MinimumLidarrVersion=…` into the build; it lands in the generated `PluginInfo`
  constant. `CI` bool define is set the same way (enables auto-update-check code paths).
- Dependencies are then **ILRepack'd into a single `Lidarr.Plugin.Tubifarry.dll`**
  (`ILRepack.targets`, `AfterTargets=Build`): merges YouTubeMusicAPI, Requests, DownloadAssistant,
  FFMpegCore, Jint, Sentry, System.Text.Json, FuzzySharp, SharpCompress, YamlDotNet, Acornima,
  MS Ext.Logging.Abstractions, etc. — so the plugin folder ships exactly one DLL.
- NuGet packages (versions at analyzed commit): `FuzzySharp 2.0.2`, `Sentry 6.9.0`,
  `ILRepack.Lib.MSBuild.Task 2.0.46`, `Shard.DownloadAssistant 1.1.2`, `System.Text.Json 8.0.6`,
  `FFMpegCore 5.0.2`, `SharpCompress 0.50.4`, `YamlDotNet 18.1.0`, `YouTubeMusicAPI 3.0.11`,
  `YouTubeSessionGenerator 1.0.3`. The `Requests` library (by the same author, NuGet) provides
  `RequestContainer`/`LoadRequest`/`RequestHandler`/`HttpGet.HttpClient` used for all downloading.
- Python scripts are embedded as `EmbeddedResource` with logical names; ffmpeg is downloaded at
  runtime by the plugin itself (`FFmpegBinaryDownloader`) rather than required pre-installed.

**Implication for YtDlp:** this is the "max fidelity" approach (always current, needs submodule
update per Lidarr change, heavier CI). For a simpler plugin, release-assembly HintPath (the
RicherTunes/Lidarr.Plugin.Common approach, see doc 01) is much lighter; either works. ILRepack
is only needed if you take NuGet deps you don't want on disk next to the plugin DLL.

## 4. Settings pattern

Three layers:

1. **Provider settings (Lidarr-managed UI):** class implements `IProviderConfig` (download
   clients) or `IIndexerSettings` (indexers); fields use `[FieldDefinition]` with explicit
   `order`, `Label`, `Type = FieldType.…`, `HelpText`, `Advanced = true` (hides from normal view),
   `Placeholder`, `Unit`, `Hidden = HiddenType.Visible`, `SelectOptions = typeof(Enum)`;
   select-option enums use `[FieldOption(Label, Hint)]` per member. Defaults are property
   initializers.
   `YoutubeProviderSettings` (order 0-11) has: DownloadPath (`FieldType.Path`), CookiePath
   (`FilePath`), UseID3v2_3 (Checkbox), ReEncode (`Select` → `ReEncodeOptions` enum, default
   `OnlyExtract`), Chunks (Number, default 2), RandomDelayMin/Max (Number, ms, 100/2000,
   "to avoid bot notifications"), MaxDownloadSpeed (KB/s, 0=unlimited), TrustedSessionGeneratorUrl,
   UseSponsorBlock (Checkbox), SponsorBlockApiEndpoint (default `https://sponsor.ajay.app`).
2. **Validation:** a **separate static FluentValidation validator class** per settings class:
   `public class YoutubeProviderSettingsValidator : AbstractValidator<YoutubeProviderSettings>`
   (path must be valid dir, cookie file exists AND parses to non-empty cookies, delay min ≤ max,
   speed 0..2500 KB/s, URL well-formedness, SponsorBlock endpoint required-when-enabled via
   `.NotEmpty().When(x => x.UseSponsorBlock)`), instantiated once as `private static readonly`,
   exposed via `public NzbDroneValidationResult Validate() => new(Validator.Validate(this));`.
3. **Connection test:** `protected override void Test(List<ValidationFailure> failures)` on the
   client/indexer adds `ValidationFailure` objects keyed by the settings property name
   (e.g. `new ValidationFailure("TrustedSessionGeneratorUrl", msg)`); each check is try/catch so
   failures accumulate instead of aborting. YouTube client's Test also checks an external dependency:
   FFmpeg presence **only if** ReEncode ≠ Disabled or SponsorBlock is on
   (`failures.AddIfNotNull(TestFFmpeg())`).
4. **Plugin-internal state (not Lidarr UI):** a custom `PluginSettings` class (Core/Utilities)
   stores arbitrary JSON values (e.g. `lastStarted` list for uptime tracking) in
   `<plugin data folder>/settings.resx`, XOR+Base64 "obfuscated", thread-safe, with
   `SettingChanged` events. Use for state that must survive restarts but isn't user-facing.

## 5. Download client flow

Architecture: `YoutubeClient : DownloadClientBase<YoutubeProviderSettings>` is a **thin facade**
(83 lines): declares `Name => "Youtube"`, `Protocol => nameof(YoutubeDownloadProtocol)`, and
delegates: `Download(remoteAlbum, indexer)` → `_dlManager.Download(...)`; `GetItems()` →
`_dlManager.GetItems()`; `RemoveItem(item, deleteData)` → deletes data + manager remove;
`GetStatus()` → `DownloadClientInfo { IsLocalhost=false, OutputRootFolders=[Settings.DownloadPath] }`.

Real work lives in a **generic base** (`Download/Base/BaseDownloadManager.cs`) reused by every
provider:

- `BaseDownloadManager<TDownloadRequest,TOptions,TClient>` holds `RequestContainer<TRequest> _queue`
  (list of active download requests) and a shared `Requests.RequestHandler _requesthandler`
  (the global HTTP download scheduler; `_requesthandler.MaxParallelism = 2` for YouTube).
- `Download(...)` calls abstract `CreateDownloadRequest(...)`, adds to queue, returns
  `downloadRequest.ID` (a GUID) — **Download() returns immediately**; work runs in the background
  via the Requests library's `RequestHandler` (async, cancellable, parallelism-capped).
- `GetItems()` maps the queue to `DownloadClientItem`s; `RemoveItem` finds by
  `item.DownloadId`, `request.Dispose()` (cancels), removes from queue.

`BaseDownloadRequest<TOptions>` (the per-download state object) is where progress tracking happens.
It wraps the Requests-library `Request<TOptions,string,string>` and owns a
`DownloadClientItem _clientItem` whose **`ClientItem` getter is computed on every call** by
Lidarr's queue-polling:

```csharp
public virtual DownloadClientItem ClientItem
{
    get
    {
        long remainingSize = GetRemainingSize();
        long totalDownloaded = _trackContainer.Sum(t => t.BytesDownloaded);
        long estimatedTotalSize = totalDownloaded + remainingSize;

        _clientItem.TotalSize = Math.Max(_clientItem.TotalSize, estimatedTotalSize);
        _clientItem.RemainingSize = remainingSize;
        _clientItem.Status = GetDownloadItemStatus();
        _clientItem.RemainingTime = GetRemainingTime();
        _clientItem.Message = GetDistinctMessages();
        _clientItem.CanBeRemoved = HasCompleted();
        _clientItem.CanMoveFiles = HasCompleted();
        return _clientItem;
    }
}
```

Status mapping: `RequestState` → `DownloadItemStatus` (Idle→Queued, Running→Downloading,
Compleated→Completed, Failed→Failed unless ≥half the sub-requests failed… etc). ETA is a sliding
window bytes/sec over `_lastRemainingSize`/`_lastUpdateTime`. `Message` is a deduped
(`Distinct()`) buffer fed by `LogAndAppendMessage(...)` — user-visible per-track failure reasons
show up in the Lidarr queue UI. `CanBeRemoved`/`CanMoveFiles` gate Lidarr's UI buttons.

Concrete YouTube flow (`YouTubeDownloadRequest`, per track): resolve album → cover → then per
track: fetch streaming data, pick highest-bitrate audio stream, create a `LoadRequest` (chunked,
speed-capped, 3 attempts) with a chained post-process `OwnRequest`
(`downloadRequest.TrySetSubsequentRequest(postProcessRequest)` — download → extract/convert
audio (FFmpeg) → SponsorBlock trim → embed ID3 tags → rename via `ReleaseFormatter`
(NamingConfig)). Verbatim core loop (`ProcessAlbumAsync`, abridged to the per-track body):

```csharp
foreach (AlbumSong trackInfo in albumInfo.Songs)
{
    try
    {
        await TryUpdateVideoBoundTokensAsync(trackInfo.Id, token);
        StreamingData streamingData = await Options.YouTubeMusicClient.GetStreamingDataAsync(trackInfo.Id, token).ConfigureAwait(false);
        AudioStreamInfo? highestAudioStreamInfo = streamingData.StreamInfo.OfType<AudioStreamInfo>().OrderByDescending(info => info.Bitrate).FirstOrDefault();

        if (highestAudioStreamInfo == null)
        {
            LogAndAppendMessage($"Skipping track '{trackInfo.Name}' in album '{ReleaseInfo.Album}' because no audio stream was found.", LogLevel.Debug);
            continue;
        }

        AddTrackDownloadRequest(albumInfo, trackInfo, highestAudioStreamInfo, token);
    }
    catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
    {
        _logger?.Warn(ex, $"403 Forbidden for track '{trackInfo.Name}' in album '{ReleaseInfo.Album}'.");
    }
    catch (Exception ex)
    {
        LogAndAppendMessage($"Failed to process track '{trackInfo.Name}' in album '{ReleaseInfo.Album}'", LogLevel.Error);
    }
}
```

Key resilience details: one bad track never fails the album (per-item try/catch, 403s are
explicitly tolerated); post-process failure deletes the partial file; `DeleteFilesOnFailure = true`
on downloads; cancellation via the request's `CancellationToken` (passed through every call).
For YtDlp the analogous unit is "one yt-dlp invocation per track/album"; progress must be parsed
from yt-dlp output (progress_hooks/--progress / `-J` JSON) and pushed into the same
`DownloadClientItem` fields computed in the getter pattern above.

## 6. Indexer pattern

`Indexers/YouTube/YoutubeIndexer.cs` (69 lines) extends the plugin's own
**`ExtendedHttpIndexerBase<TSettings, TIndexerPageableRequest> : IndexerBase<TSettings>`**
(Core/Replacements), which adds `IHttpClient` + Sentry to `IndexerBase` and makes the provider
supply an `IIndexerRequestGenerator` and `IParseIndexerResponse` via abstract members. Provider
class shape:

- `Name => "Youtube"`, `Protocol => nameof(YoutubeDownloadProtocol)` — **must equal the download
  client's Protocol** so Lidarr can pair search results with the downloader.
- `SupportsRss = false`, `SupportsSearch = true`, `PageSize = 50`.
- `RateLimit => TimeSpan.FromSeconds(2)` — the built-in per-indexer request spacing Lidarr
  enforces between calls to this indexer.
- `ProviderMessage` (Warning): documents that YouTube blocks bots and cookies may be needed —
  user-facing guidance lives here.
- `Test(List<ValidationFailure>)`: validates the Trusted Session Generator URL / cookie file
  (same as the client; indexer and client share `TrustedSessionHelper`).
- Companion files: `YouTubeRequestGenerator : IIndexerRequestGenerator<LazyIndexerPageableRequest>`
  (empty `GetRecentRequests` → `LazyIndexerPageableRequestChain` for RSS-off; `GetSearchRequests`
  builds up to `MaxPages = 3` of search requests using the YouTubeMusicAPI library), and
  `YouTubeParser : IParseIndexerResponse` (maps API models → `ReleaseInfo`/`ParsedTrackInfo`
  with `ReleaseFormatter`-style naming). `BaseUrl` exists on settings for the standard
  indexer-settings UI even though search goes through the API lib, not raw HTTP.

So: one indexer = `{Indexer, IndexerSettings, RequestGenerator, Parser, (Records, SessionHelper)}`,
all `internal`, folder per provider.

## 7. Throttling / retry / error handling (the bot-detection kit)

- **Indexer-level spacing:** `TimeSpan RateLimit` (2 s for YouTube indexer) — cheapest lever.
- **Random human-like delay between API calls:** settings `RandomDelayMin/Max` (ms) with
  `ApplyRandomDelayAsync` (`new Random().Next(min,max)` + `Task.Delay`) between the album-info
  call and track processing; help text says "to avoid bot notifications".
- **Global parallelism cap:** shared `Requests.RequestHandler` with `MaxParallelism` (2 for the
  YouTube manager; the client constructor even sets `RequestHandler.MainRequestHandlers[1].MaxParallelism = 1`
  for a specific lane) — no more N concurrent HTTP downloads regardless of queue size.
- **Per-request retry:** `RequestOptions { DelayBetweenAttemps = 5s, NumberOfAttempts = 2 }`
  for orchestration requests, `NumberOfAttempts = 3` on actual track `LoadRequest`s.
- **Speed cap:** `MaxDownloadSpeed` KB/s (validated 0–2500) → `LoadRequest.MaxBytesPerSecond`.
- **`LucidaRateLimiter`** (most complete example in the repo — 270 lines): per-"worker"
  `SemaphoreSlim` slots (max concurrent per worker), per-worker `RateLimitedUntil` cooldown set by
  `MarkWorkerRateLimited(...)` when a 429-like response is detected (`IsRateLimitedResponse`
  pattern-matches the response body), `WaitForAvailableWorkerAsync` polls with 1 s sleeps until a
  non-cooled-down slot with free capacity is found, plus `EnforceGlobalDelayAsync` which
  guarantees ≥ `MIN_REQUEST_DELAY_MS` between *any* two requests globally (lock +
  `DateTime.UtcNow - _lastRequestTime`). Separate `PollingSemaphore` so long polls don't consume
  request slots. If YtDlp ever talks to a rate-limited API (e.g. SponsorBlock, or IP-based
  throttling), this slot+cooldown+global-min-delay design is copy-ready.
- **`ApiCircuitBreaker`** (Core/Model): failure counter (default threshold 5, 5 min reset,
  counter decays by 1 on success, `IsOpen` auto-resets after timeout) + `CircuitBreakerFactory`
  with per-Type `ConditionalWeakTable` and named `WeakReference` breakers. Open = stop hitting
  the API entirely instead of slowly retrying.
- **HTTP:** `BaseHttpClient` wraps a **shared static `HttpClient`** (`HttpGet.HttpClient` from the
  Requests library) — comments stress "Never modifies the shared HttpClient; uses individual
  requests with proper headers"; a fixed browser `UserAgent` constant on `Plugin` is applied per
  request; timeout = linked `CancellationTokenSource.CancelAfter(60s)`; supports an
  `IHttpRequestInterceptor` chain (pre-request/post-response) — the FlareSolverr notification
  uses exactly this to transparently route blocked requests through a CF-solver; gzip/deflate
  decompression of response bodies. No retry loop in the wrapper — retries are the
  RequestHandler/`NumberOfAttempts` responsibility.
- **External processes:** `Core/Utilities/ProcessRunner.cs` (69 lines) — the exact shape needed
  for shelling out to yt-dlp: `ProcessStartInfo` with `ArgumentList` (no string quoting bugs),
  redirected stdout/stderr, `UseShellExecute=false`, `CreateNoWindow=true`, optional env vars,
  `WaitForExitAsync` with linked timeout token, `Kill(true)` on timeout → `TimeoutException`,
  returns `record ProcessResult(int ExitCode, string StandardOutput, string StandardError) { bool Succeeded }`.
- **Anti-bot auth:** `TrustedSessionHelper` (482 lines) fetches PoToken/visitor-data/cookies from
  an optional external "Trusted Session Generator" service, validates in `Test()`, refreshes
  video-bound tokens per video. YtDlp equivalent: `--cookies` / `--extractor-args "youtube:player_client=..."`
  settings + a Test() that runs `yt-dlp --simulate` on a probe video.

## 8. Test patterns

**Absence is the finding: the repository has zero tests.** No test project in the solution, no
`*Test*`/`*Spec*` files, no NSubstitute/Moq/xUnit/NUnit references anywhere, and no `dotnet test`
step in any CI workflow — CI is `dotnet build *.sln -c Release` (compilation.yml) plus packaging/
publishing. The only "quality gates" are the FluentValidation validators, the `Test(List<ValidationFailure>)`
endpoints, and compile-time checks against pinned Lidarr source. Do not model YtDlp's testing on
this; use the standard approach (NSubstitute for `IHttpClient`/`IConfigService` etc., unit-test
parsers/settings validators/ProcessRunner argument building, integration-test with a fake
`yt-dlp` stub script on PATH).

## 9. Patterns worth copying for Lidarr.Plugin.YtDlp

- **Facade + manager + queue:** `YtDlpClient : DownloadClientBase<YtDlpProviderSettings>` (Name/Protocol/Test/GetStatus)
  delegating to a manager that owns `List<YtDlpDownloadRequest>`; `Download()` enqueues + returns GUID;
  `GetItems()` computes `DownloadClientItem` fields lazily in the getter (TotalSize/RemainingSize/
  RemainingTime/Status/Message/CanBeRemoved/CanMoveFiles).
- **`ProcessRunner`** verbatim for the yt-dlp binary (ArgumentList, timeout, Kill, env, cancellation).
- **Settings trio:** `[FieldDefinition(order, …, Advanced=true)]` fields with defaults in initializers;
  enum + `[FieldOption]` for format/quality choices; separate static `AbstractValidator<T>` +
  `Validate() => new NzbDroneValidationResult(Validator.Validate(this))`; `Test()` that runs
  `yt-dlp --version` (and a `--simulate` probe when cookies configured) and appends
  `ValidationFailure("SettingName", …)` without throwing.
- **Protocol string** shared constant between client and indexer (`YtDlpDownloadProtocol`).
- **Throttle stack for bot safety:** indexer `RateLimit` (e.g. 2–5 s) + configurable random
  min/max delay between API calls + a single global `SemaphoreSlim`/RequestHandler cap on
  concurrent downloads + optional circuit breaker around SponsorBlock API; per-item try/catch so
  one failed track doesn't fail the album, with messages funneled into `DownloadClientItem.Message`
  (deduped).
- **Naming:** build output paths with `NamingConfig`/`ReleaseFormatter` (`BuildArtistFolderName`,
  `BuildTrackFilename`) so files land where Lidarr's naming scheme expects.
- **Optional:** ILRepack single-DLL packaging (only if you take NuGet deps), `PluginInfo.targets`
  build-time constants, and a `Plugin` entry class that registers DelayProfile items for your
  protocol at startup (Tubifarry auto-allowlists its protocols in every delay profile).
- **Do NOT copy:** git-submodule Lidarr build (prefer HintPath/release assemblies per doc 01 for
  simplicity), no-tests posture, the 482-line session-helper (yt-dlp handles PoToken itself).

## 10. Source files read (paths)

- `/tmp/tubifarry/Tubifarry/Tubifarry.csproj`, `Directory.Build.props`, `PluginInfo.targets`, `ILRepack.targets`, `Tubifarry.sln`, `.gitmodules`
- `/tmp/tubifarry/Tubifarry/Plugin.cs`
- `/tmp/tubifarry/Tubifarry/Download/Base/BaseDownloadManager.cs`, `BaseDownloadRequest.cs`, `BaseHttpClient.cs`
- `/tmp/tubifarry/Tubifarry/Download/Clients/YouTube/YoutubeClient.cs`, `YoutubeDownloadManager.cs`, `YoutubeProviderSettings.cs`, `YouTubeDownloadRequest.cs`, `SponsorBlock.cs` (partial), `TrustedSessionHelper.cs` (referenced, not read in full)
- `/tmp/tubifarry/Tubifarry/Indexers/YouTube/YoutubeIndexer.cs`, `YoutubeIndexerSettings.cs`, `YoutubeRequestGenerator.cs` (partial)
- `/tmp/tubifarry/Tubifarry/Core/Utilities/ProcessRunner.cs`, `PluginSettings.cs`
- `/tmp/tubifarry/Tubifarry/Core/Replacements/ExtendedHttpIndexerBase.cs` (partial)
- `/tmp/tubifarry/Tubifarry/Download/Clients/Lucida/LucidaRateLimiter.cs` (full), `ILucidaRateLimiter.cs` (sizes only)
- `/tmp/tubifarry/Tubifarry/Core/Model/ApiCircuitBreaker.cs` (full)
- `.github/workflows/compilation.yml` (grep only)
- Submodule pin verified via `git ls-tree HEAD Submodules/` + GitHub API (Lidarr commit `33cea31a27f7`, 2026-11-04)
