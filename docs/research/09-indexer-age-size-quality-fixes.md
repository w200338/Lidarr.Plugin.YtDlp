# v0.2.4 — indexer age, size, quality, client-form fix + provider-warning explanation

Date: 2026-11-13
Host target: Lidarr `v3.1.6.5079` (stable). Plugin: `Lidarr.Plugin.YtDlp`.

This document records the investigation and the fixes for the five issues
reported against v0.2.3. Items 1–4 are code fixes, shipped in v0.2.4.
Item 5 is an explanation + remediation (no plugin bug).

---

## 1. Indexer age always shows "0 minutes"

**Symptom.** In `Settings → Indexers` search results (and interactive search),
every release's Age column said `0 minutes`, even for albums uploaded years
ago.

**Root cause (two parts).**

1. The plugin set `ReleaseInfo.PublishDate = DateTime.UtcNow` (search time)
   for every release. The host derives the displayed Age from
   `PublishDate` (`ReleaseInfo.Age`/`AgeHours`/`AgeMinutes` in
   `ext/Lidarr/src/NzbDrone.Core/Parser/Model/ReleaseInfo.cs`), so a
   just-now timestamp always renders as ~0.
2. yt-dlp's flat search results (the music search page in `#albums` mode)
   carry **no release date at all** — the entries are only
   `{id, url}`, and even the album page's metadata does not reliably expose
   a release year. So the plugin cannot learn the real date from YouTube.

**Fix (v0.2.4).** The host passes the album it is searching for into the
indexer — `AlbumSearchCriteria.AlbumYear` (populated by the host's
`ReleaseSearchService` from the catalog's release metadata). The plugin now
uses:

- `PublishDate = <album year>-12-31 00:00:00 UTC` when the criteria carry a
  plausible year (≥ 1900 and not in the future);
- `PublishDate = DateTime.UtcNow` otherwise (no year, artist/track search).

Why **Dec 31** rather than Jan 1 or the search time:

- It gives a meaningful age (a 2011 album now shows ~15 years instead of
  0 minutes).
- It is safe for the host's `EarlyReleaseSpecification`: that spec only
  rejects a release when the publish date falls *inside* the
  album-date-plus-N-days early window. Dec 31 of the album year is never
  inside that window, so enabling "Early Download Limit" can't cause
  false rejections. (Jan 1 could sit inside a same-year window.)
- It does **not** affect year matching: `YearMatchSpecification` compares
  the year parsed from the release *title*, not `PublishDate`.

**Verification.** Unit tests in
`Lidarr.Plugin.YtDlp.Tests/Indexing/YtDlpIndexerTests.cs`
(`MapAlbumRelease_publish_date_uses_album_year_dec31`,
`PublishDate_falls_back_to_search_time_for_missing_or_future_years`).

**Caveat.** The age is "as old as the catalog says this album is", not the
exact upload date of the YouTube track — YouTube metadata does not expose
per-video upload dates in search results, and inspecting every entry just
for a date would slow every search down significantly.

---

## 2. Indexer size shows "0B"

**Symptom.** The Size column of every release showed `0B`.

**Root cause.** The plugin set `ReleaseInfo.Size = 0`. That is the *correct
way to say "unknown"* for the host's decision engine: both
`AcceptableSizeSpecification` and `MaximumSizeSpecification` explicitly
skip validation when `Size == 0`, so nothing was rejected — it just looked
bad in the UI.

But it turns out a safe estimate is possible after all: the entry
inspection the plugin already performs per album (the flat re-fetch that
resolves the real album title) exposes each entry's `duration` in seconds
(observed live on `https://music.youtube.com/browse/...` pages). Summing
those gives the album's total duration, which can be converted to bytes
with a conservative per-delivery-mode bitrate:

| Delivery mode (client "Re-encode") | Assumed bytes/s | Rationale |
|---|---|---|
| MP3 | 30,000 | V0 VBR tops out ~245 kbps |
| AAC | 28,000 | VBR `-q:a 0.1` ≈ up to ~192 kbps |
| Vorbis / source stream | 32,000 | Q10 ≈ up to ~256 kbps; source Opus ≈ 160–200 kbps |

The rate is deliberately *over*-estimated: if the host does compare the
size against profile min/max limits, an over-estimate keeps a legitimate
release inside the allowed window.

**Fix (v0.2.4).**

- The inspection print template now includes `duration`:
  `%(.{playlist_title,playlist_channel,playlist_id,duration})j`.
- `SearchLineParser` parses per-line durations; the indexer sums them into
  `EntryResolution.TotalDurationSeconds` (the flat page echoes the shared
  playlist fields on every line, so title/channel/playlist-id take the
  first non-empty value and durations are added).
- `ReleaseInfo.Size = totalSeconds × bytesPerSecond` when the inspection
  saw durations; `0` (unknown → host skips size checks) when it didn't.
- The delivery mode (hence the bitrate) is read at search time from the
  first **enabled** yt-dlp download client's `Reencode` setting, via the
  host's `IProviderRepository<DownloadClientDefinition>` injected through
  the host DI container (plugin constructors are resolved by
  `ProviderFactory` from the host container, so host services can be
  constructor-injected). No client found/lookup fails → `Disabled`
  assumptions (no quality token, generic estimate) — the search never
  fails because of this.
- **Catalog-duration guard:** reading `AcceptableSizeSpecification`
  closely, it rejects *any* `Size > 0` release when the host catalog has
  no release durations for the searched album ("Album duration is 0") —
  and the default quality definitions all have a non-zero `MaxSize`, so
  this check is live. That would deadlock title-only albums (no import →
  no duration data → always rejected). The plugin therefore mirrors the
  spec's duration math over `searchCriteria.Albums`: no catalog release
  durations → `Size = 0` (host skips the size check, exactly as before
  v0.2.4); durations present → the estimate (which fits under the default
  MaxSize bounds: MP3_VBR 350 Kbps ≈ 43.7 kB/s > 30 kB/s, AAC_VBR
  350 Kbps > 28 kB/s, VORBIS_Q10 550 Kbps ≈ 68.7 kB/s > 32 kB/s,
  Unknown 350 Kbps > 32 kB/s).

**Verification.** Unit tests
(`MapAlbumRelease_estimates_size_from_entry_durations`); live inspection
output confirmed the per-entry `duration` field.

**Caveat.** It is an *estimate* (duration × assumed bitrate), accurate to
roughly ±20% — the host UI will show e.g. `49.6 MB` for a ~13-track
album. The host does not re-check the size after the download completes
against the search-time value in a way that rejects imports, and the
over-estimate keeps size-based profile decisions on the permissive side.

---

## 3. Indexer quality shows "unknown"

**Symptom.** Every release's Quality column showed `unknown`, so profiles
that disallow `Unknown` (e.g. the built-in `Lossless` and `Standard`)
rejected every YtDlp release.

**Root cause.** The host does **not** read quality from the plugin's
`ReleaseInfo` — it parses it from the release **title**
(`DownloadDecisionMaker` → `Parser.ParseAlbumTitle(report.Title)`, quality
via `QualityParser`). The plugin's titles were plain `Artist - Album`,
which contain no quality tokens, so the host resolved `Unknown`.

**Fix (v0.2.4).** When the enabled download client re-encodes, the plugin
now appends a host-parseable quality token to the album title, matching
what `--audio-quality 0` actually produces per codec:

| Re-encode mode | Title suffix | Host quality parsed |
|---|---|---|
| MP3 | `(MP3 VBR V0)` | `MP3-VBR-V0` (id 2) |
| AAC | `(AAC VBR)` | `AAC-VBR` (id 12) |
| Vorbis | `(VORBIS Q10)` | `OGG Vorbis Q10` (id 14) |
| Opus | *(none)* | `Unknown` (unchanged) |
| Disabled | *(none)* | `Unknown` (unchanged) |

Why Opus and Disabled keep `Unknown`:

- The host's `Quality` value set has **no Opus entry at all** — any token
  we invented would still parse to `Unknown` (or worse, to the wrong
  quality), so an honest `Unknown` is the correct answer for Opus
  delivery. (If your profile disallows `Unknown` and you want Opus, allow
  `Unknown` in the profile — it is the only way Lidarr can represent it.)
- "Disabled" delivers whatever YouTube's source stream is (usually Opus in
  a `.webm`), so `Unknown` is again the honest label.

The delivery mode is discovered the same way as for the size estimate
(§2): the first enabled yt-dlp client's `Reencode` setting, at search
time, with a safe `Disabled` fallback.

**Verification.** New contract test suite
`Lidarr.Plugin.YtDlp.Tests/Indexing/HostQualityParseContractTests.cs`
runs the **real host v3.1.6.5079 `Parser`** (the test project references
the pinned host sources) and asserts:

- `Adele - 21 (MP3 VBR V0)` → `Quality.MP3_VBR`
- `Adele - 21 (AAC VBR)` → `Quality.AAC_VBR`
- `Adele - 21 (VORBIS Q10)` → `Quality.VORBIS_Q10`
- plain `Adele - 21` → `Unknown` via the host's
  `ParseAlbumTitleWithSearchCriteria` fallback (baseline behavior)
- the token survives that fallback path too.

**Note.** The title is what the UI shows for the release, so a suffixed
release now displays e.g. `Adele - 21 (MP3 VBR V0)`. After import, the
*imported* media is identified by file/track data, not by this title.

---

## 4. "Search Limit" showed up in the download client settings

**Symptom.** The download client's settings form contained a **Search
Limit** field — a pure indexer concept the download client never uses.

**Root cause.** All shared fields lived on a common base class
`YtDlpSettings` that both `YtDlpIndexerSettings` and
`YtDlpClientSettings` inherit from, and `SearchLimit` was on that base.
The host's form builder (`Lidarr.Http/ClientSchema/SchemaBuilder.cs`)
reflects over the concrete settings type's public `[FieldDefinition]`
properties, **including inherited ones** — so every field on the base
appeared in both forms. The mirror bug (download-side fields leaking into
the indexer form) existed too.

**Fix (v0.2.4).** The settings types were split so each form only carries
fields its provider actually uses:

- `YtDlpSettings` (abstract base, not a form itself): `yt-dlp Path` only —
  the one field both providers genuinely share.
- `YtDlpIndexerSettings`: `Search Limit`, `Early Download Limit` (plus the
  inherited path).
- `YtDlpClientSettings`: `Additional Arguments`, `Use SponsorBlock`,
  `SponsorBlock API Endpoint`, `Re-encode`, `Min/Max Delay`, `Download
  Folder` (plus the inherited path).
- Validators were split the same way: the shared validator only checks the
  path; each concrete validator checks its own fields. The SponsorBlock
  endpoint is now validated on the client form only, and the connection
  test takes the endpoint explicitly (the indexer no longer even pings
  SponsorBlock when testing).

**Backward compatibility.** Nothing to do on your side: the host
deserializes stored provider settings by property *name*, so existing
installed configs keep every saved value; the moved/removed fields are
simply ignored where they no longer exist. The provider names
(`YtDlpIndexer` / `YtDlpDownloadClient`) and the settings contract names
are unchanged.

**Verification.** New test
`SearchLimit_appears_in_indexer_schema_but_not_client_schema` builds the
actual host `SchemaBuilder` schemas for both settings types (with a
stubbed localization service) and asserts:

- `searchLimit` is present in the indexer schema, **absent** from the
  client schema;
- `reencode` is present in the client schema, absent from the indexer
  schema;
- `ytDlpPath` remains in both.

---

## 5. `Skipping provider of unknown type ...` warnings

**Symptom.** Repeated log entries such as:

```
[Warn] ProviderRepository: Skipping provider of unknown type MixedMetadataProxySettings
[Warn] ProviderRepository: Skipping provider of unknown type SykHookMetadataProxySettings
[Warn] ProviderRepository: Skipping provider of unknown type SearchSniperTaskSettings
[Warn] ProviderRepository: Skipping provider of unknown type LastfmMetadataProxySettings
```

**Root cause. These are not caused by Lidarr.Plugin.YtDlp.**

The warning is emitted by the host's
`ProviderRepository<TProviderDefinition>.Query()`
(`ext/Lidarr/src/NzbDrone.Core/ThingiProvider/ProviderRepository.cs:67`):
it reads every row from the provider tables in `lidarr.db`, and when a
row's stored `ConfigContract` (the settings class name) names a type that
cannot be resolved from any loaded assembly, the host skips the row and
logs this warning.

None of the four named settings types exist in Lidarr's own source. Web
research + the plugin's own research notes
(`docs/research/02-tubifarry-structure.md`) identify them as settings
classes of the **third-party `Tubifarry` plugin**
(`MixedMetadataProxy`, `SykHookMetadataProxy`, `SearchSniperTask`,
`LastfmMetadataProxy` are all Tubifarry providers — `Tubifarry@TypNull` on
the plugin marketplace). The rows are orphans: at some point Tubifarry was
installed (its providers were saved into your DB), and it is now removed
or not loaded, so Lidarr can no longer resolve those settings types.

**Effect.** Benign. The orphaned rows are skipped; no YtDlp functionality
is affected; the warnings are noise on startup and on provider queries.

**Remediation (pick one).**

1. **If you still want Tubifarry:** (re)install/update it via
   `Settings → Plugins` — the types resolve again and the warnings stop.
2. **If you don't:** delete the orphaned provider entries.
   - Preferred: Lidarr UI → `Settings → Indexers / Download Clients /
     Import Lists / Metadata / Notifications` and delete anything you
     don't use (orphaned rows whose type can't be resolved usually
     *can't* be shown or edited in the UI, which is why the DB step may
     be needed).
   - Precise DB cleanup (verified against the `v3.1.6.5079` schema):
     with Lidarr **stopped** and a **backup of `lidarr.db` taken**, run
     in `sqlite3` (or any SQLite client):

     *Step 1 — see every provider row you have* (five tables carry
     provider definitions in this version; there is no single generic
     `Providers` table):

     ```sql
     SELECT 'Metadata' AS Table, Id, Name, ConfigContract FROM Metadata
     UNION ALL SELECT 'Indexers', Id, Name, ConfigContract FROM Indexers
     UNION ALL SELECT 'DownloadClients', Id, Name, ConfigContract FROM DownloadClients
     UNION ALL SELECT 'ImportLists', Id, Name, ConfigContract FROM ImportLists
     UNION ALL SELECT 'Notifications', Id, Name, ConfigContract FROM Notifications
     ORDER BY Table, Id;
     ```

     *Step 2 — flag every row whose settings type is NOT a built-in
     Lidarr v3.1.6.5079 provider (and not your YtDlp plugin's).*
     Anything this returns is an orphan from an uninstalled plugin —
     in a typical Tubifarry install it is exactly the ten metadata rows
     below. (Verified: on a reference `v3.1.6.5079` DB this flags the
     ten Tubifarry `Metadata` rows and nothing else.)

     ```sql
     SELECT Id, Name, ConfigContract FROM Metadata        WHERE ConfigContract NOT IN
        ('XbmcMetadataSettings','WdtvMetadataSettings','RoksboxMetadataSettings');
     SELECT Id, Name, ConfigContract FROM Indexers        WHERE ConfigContract NOT IN
        ('HeadphonesSettings','IPTorrentsSettings','GazelleSettings','YtDlpIndexerSettings');
     SELECT Id, Name, ConfigContract FROM DownloadClients WHERE ConfigContract NOT IN
        ('Aria2Settings','DelugeSettings','DownloadStationSettings','FloodClientSettings','FloodSettings',
         'FreeboxDownloadSettings','HadoukenSettings','NzbVortexSettings','NzbgetSettings','PneumaticSettings',
         'QBittorrentSettings','RTorrentSettings','SabnzbdSettings','TorrentBlackholeSettings','TransmissionSettings',
         'UTorrentSettings','UsenetBlackholeSettings','YtDlpClientSettings');
     SELECT Id, Name, ConfigContract FROM ImportLists     WHERE ConfigContract NOT IN
        ('CustomSettings','HeadphonesImportSettings','LastFmTagSettings','LastFmUserSettings',
         'LidarrListsSettings','LidarrSettings','MusicBrainzSeriesSettings',
         'SpotifyFollowedArtistsSettings','SpotifyPlaylistSettings','SpotifySavedAlbumsSettings');
     SELECT Id, Name, ConfigContract FROM Notifications   WHERE ConfigContract NOT IN
        ('AppriseSettings','CustomScriptSettings','DiscordSettings','EmailSettings','GotifySettings','JoinSettings',
         'MailgunSettings','MediaBrowserSettings','NotifiarrSettings','NtfySettings','PlexServerSettings','ProwlSettings',
         'PushBulletSettings','PushcutSettings','PushoverSettings','SendGridSettings','SignalSettings',
         'SimplepushSettings','SlackSettings','SubsonicSettings','SynologyIndexerSettings','TelegramSettings',
         'TwitterSettings','WebhookSettings','XbmcSettings');
     ```

     *Step 3 — targeted delete* (only after Steps 1–2 look right). In a
     Tubifarry install every orphan is in `Metadata`; the ten known
     Tubifarry contracts are:

     ```sql
     DELETE FROM Metadata WHERE ConfigContract IN
        ('SearchSniperTaskSettings','SimilarArtistsProxySettings','SykHookMetadataProxySettings',
         'MixedMetadataProxySettings','LastfmMetadataProxySettings','DiscogsMetadataProxySettings',
         'DeezerMetadataProxySettings','CustomLidarrMetadataProxySettings','LyricsEnhancerSettings',
         'AudioConverterSettings');
     -- if Step 2 flagged rows in other tables too, delete those the same way:
     -- DELETE FROM Indexers        WHERE ConfigContract IN (...flagged names...);
     -- DELETE FROM DownloadClients WHERE ConfigContract IN (...flagged names...);
     -- DELETE FROM ImportLists     WHERE ConfigContract IN (...flagged names...);
     -- DELETE FROM Notifications   WHERE ConfigContract IN (...flagged names...);
     ```

     Safety notes, verified against the `v3.1.6.5079` source and a
     reference DB:
     - The rows are named deceptively: **"Lidarr Default"** is
       Tubifarry's SykHook proxy and **"Lidarr Custom"** is its
       CustomLidarrMetadataProxy — neither is a Lidarr built-in. The
       real built-in metadata providers are only Kodi (XBMC), WDTV and
       Roku Box (`XbmcMetadataSettings` / `WdtvMetadataSettings` /
       `RoksboxMetadataSettings`).
     - Metadata profiles in this version do **not** store metadata
       provider ids (they only hold album-type/status filters), so
       deleting `Metadata` rows leaves no dangling references.
     - `YtDlpIndexerSettings` / `YtDlpClientSettings` are deliberately
       whitelisted in Step 2 so your own plugin's providers are never
       flagged.

---

## What changed in the plugin (v0.2.4 file list)

- `Lidarr.Plugin.YtDlp/Settings/YtDlpSettings.cs` — abstract base, path only.
- `Lidarr.Plugin.YtDlp/Settings/YtDlpIndexerSettings.cs` — indexer fields +
  value equality.
- `Lidarr.Plugin.YtDlp/Settings/YtDlpClientSettings.cs` — client fields +
  value equality.
- `Lidarr.Plugin.YtDlp/Settings/YtDlpSettingsValidator.cs` — split
  validators, SponsorBlock endpoint helper.
- `Lidarr.Plugin.YtDlp/Commands/YtDlpCommandBuilder.cs` — inspect template
  includes `duration`.
- `Lidarr.Plugin.YtDlp/Indexing/SearchLineParser.cs` — `InspectLine`
  record + per-line parsing; `EntryResolution` carries total duration.
- `Lidarr.Plugin.YtDlp/Indexing/YtDlpIndexer.cs` — album-year
  `PublishDate`, duration-based `Size` (with the catalog-duration guard),
  delivery-mode quality token, DI-injected client-definitions lookup.
- `Lidarr.Plugin.YtDlp/Probing/YtDlpConnectionTester.cs`,
  `Lidarr.Plugin.YtDlp/Probing/YtDlpConnectionTest.cs` — explicit
  SponsorBlock endpoint parameter.
- `Lidarr.Plugin.YtDlp/Download/YtDlpDownloadClient.cs`,
  `Lidarr.Plugin.YtDlp/Download/YtDlpAlbumDownloader.cs`,
  `Lidarr.Plugin.YtDlp/Indexing/YtDlpIndexer.cs` — concrete-settings types
  wired through.
- Tests: updated for the new shapes; new coverage for age/size/quality
  mapping, host-parser contract (real `Parser` from the pinned host), and
  host `SchemaBuilder` form shapes, and the catalog-duration guard
  (AcceptableSizeSpecification safety). **245/245 passing in Debug and
  Release.**

## Deployment notes

- Same artifact as before: one merged `Lidarr.Plugin.YtDlp.dll`,
  `artifacts/net8.0.zip`, drop into the plugin folder. No `plugin.json`.
- Existing provider settings survive the update (property-name
  deserialization).
- The indexer's quality token and size estimate depend on the *enabled*
  yt-dlp download client's `Re-encode` setting — changing that setting
  changes what future searches report (no re-search of old results
  happens; the next search picks it up).
