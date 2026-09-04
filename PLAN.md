# Plan: convert jellyfin-watchnotify into a standalone Jellyfin plugin

Replace the Go webhook receiver with an in-process Jellyfin server plugin so that
`jellyfin-plugin-webhook`, the external container, and the `.env` file all go away.
Configuration and an event log live in the Jellyfin admin dashboard; the repo doubles
as a plugin repository (`manifest.json`).

## 1. Target platform (verified)

Checked against the `release-10.11.z` tree (`SharedVersion.cs` = `10.11.11`) and a
compile probe against the real NuGet packages.

| Item | Value |
|---|---|
| Server | 10.11.11 (`ApplicationVersion` 10.11.11) |
| TargetFramework | `net9.0` (Jellyfin 10.11 moved to .NET 9) |
| Packages | `Jellyfin.Controller` + `Jellyfin.Model` `10.11.11`, both with `<ExcludeAssets>runtime</ExcludeAssets>` |
| `targetAbi` | `10.11.0.0` (it is a *floor*: server installs a version when `ApplicationVersion >= targetAbi`) |
| Local SDKs | .NET 9.0.317 and 10.0.111 are installed — no toolchain work needed |

**Next Jellyfin release is `12.0`, not `10.12`.** `master` is `AssemblyVersion 12.0.0` and
targets `net10.0`; `Jellyfin.Controller` 12.0.0-rc7 is already on NuGet. Plan for a second
build (net10.0 / `targetAbi: 12.0.0.0`) published as an additional entry in the same
`versions[]` array — the server picks the highest entry whose `targetAbi` it satisfies, so
one manifest can serve both 10.11 and 12.0 users.

## 2. Repo layout after the conversion

```
/
├── PLAN.md
├── README.md                                  # rewritten for the plugin
├── manifest.json                              # plugin repository (served raw from GitHub)
├── Directory.Build.props                       # Version/AssemblyVersion/FileVersion
├── Jellyfin.Plugin.WatchNotify.slnx
├── Makefile                                    # build / package / release
├── .github/workflows/release.yml
└── Jellyfin.Plugin.WatchNotify/
    ├── Jellyfin.Plugin.WatchNotify.csproj
    ├── Plugin.cs                               # BasePlugin<PluginConfiguration>, IHasWebPages
    ├── PluginServiceRegistrator.cs             # IPluginServiceRegistrator
    ├── Configuration/
    │   ├── PluginConfiguration.cs
    │   ├── configPage.html   configPage.js     # settings
    │   └── logPage.html      logPage.js        # event log
    ├── Watch/
    │   ├── WatchNotifyEntryPoint.cs            # IHostedService, subscribes/unsubscribes
    │   ├── SessionTracker.cs                   # port of sessions.go
    │   ├── WatchEventFactory.cs                # port of payload.go title/progress helpers
    │   └── WatchEvent.cs
    ├── Dispatch/
    │   ├── DispatchQueue.cs                    # port of handler.go retry loop
    │   ├── ScnClient.cs                        # port of scn.go
    │   └── JoplinClient.cs                     # port of joplin.go
    ├── Logging/
    │   └── EventLogStore.cs                    # rolling, persisted event log
    └── Api/
        └── WatchNotifyController.cs            # /WatchNotify/*
```

The Go program in `jellyfin-watchnotify/` is deleted in the final phase (git history keeps it).

Identity:

- Assembly / namespace: `Jellyfin.Plugin.WatchNotify`
- Display name: `WatchNotify`, slug `watchnotify`
- Plugin GUID: `295982d0-fc94-4c98-9bf4-54bc7cf05b30`

## 3. Architecture

One `IHostedService` owns all event wiring; `PluginServiceRegistrator` registers it plus
the singletons it needs.

```
ISessionManager.PlaybackStart ─┐
ISessionManager.PlaybackProgress ┤→ SessionTracker (start time, coalesce window)
ISessionManager.PlaybackStopped ─┴→ watched? → dedup → WatchEvent → DispatchQueue ─→ ScnClient
IUserDataManager.UserDataSaved ──→ fallback / manual toggle                       └─→ JoplinClient
ILibraryManager.ItemAdded ───────→ log only                                            ↓
                                                                                 EventLogStore
```

### Why `IHostedService` rather than `IEventConsumer<T>`

The modern event bus covers `PlaybackStartEventArgs` / `PlaybackProgressEventArgs` /
`PlaybackStopEventArgs`, but **not** `UserDataSaveEventArgs` or `ItemChangeEventArgs` —
even the webhook plugin falls back to manual subscription for those two. `IEventConsumer<T>`
is also registered `AddScoped`, so shared start-time/dedup state would have to live in a
separate singleton anyway. One hosted service subscribing in `StartAsync` and unsubscribing
in `StopAsync` (the pattern `jellyfin-plugin-trakt` uses) is the smaller design.
`IServerEntryPoint` no longer exists (removed in 10.9).

### Event handlers

| Source | Args | Action |
|---|---|---|
| `ISessionManager.PlaybackStart` | `PlaybackProgressEventArgs` | `SessionTracker.Record(key, now)`; log `playback-start` |
| `ISessionManager.PlaybackProgress` | `PlaybackProgressEventArgs` | `SessionTracker.Record` (keeps `lastSeen` fresh) |
| `ISessionManager.PlaybackStopped` | `PlaybackStopEventArgs` | watched logic → dispatch |
| `IUserDataManager.UserDataSaved` | `UserDataSaveEventArgs` | `SaveReason == PlaybackFinished` and no stop seen → treat as watched; `TogglePlayed` + `UserData.Played` → optional manual trigger |
| `ILibraryManager.ItemAdded` | `ItemChangeEventArgs` | log only (skip `IsVirtualItem`) |

Session key: `Session.Id + "|" + Item.Id`. Dedup key: `userId + "|" + itemId`.

### Watched logic (unchanged semantics)

`Item is Movie or Episode` **and** (`PlayedToCompletion` **or**
`PlaybackPositionTicks / Item.RunTimeTicks >= WatchedThreshold`), then dedup within
`DedupTtl`.

Known trap worth the `UserDataSaved` fallback: some clients report
`PlayedToCompletion = false` with `PlaybackPositionTicks = 0` on a natural end of file
(reported repeatedly against the Trakt plugin). `UserDataSaved` with
`SaveReason == PlaybackFinished` fires in that case. Guard it with the same dedup ledger so
a normal watch is not notified twice.

### Metadata extraction (replaces `payload.go`)

- `e.Item is Movie m` → `m.Name`, `m.ProductionYear`
- `e.Item is Episode ep` → `ep.SeriesName`, `ep.ParentIndexNumber` (season),
  `ep.IndexNumber` (episode), `ep.Name` (episode title)
- Runtime/position: `Item.RunTimeTicks`, `e.PlaybackPositionTicks` (10,000,000 ticks/s)
- User: `e.Users.FirstOrDefault()?.Username`, falling back to `e.Session?.UserName`;
  for `UserDataSaved`, `IUserManager.GetUserById(e.UserId).Username`
- Device/client: `e.DeviceName`, `e.ClientName`

Title formatting (`[👁️] [S02E13] Firefly`, `Firefly [S01E02]`, the padded Joplin line)
ports over verbatim from `payload.go` / `joplin.go`.

### Timestamps

Events now arrive in real time, so `DateTime.UtcNow` is the event instant — the whole
`parseJellyfinTime` / `UtcTimestamp` layer disappears. Display conversion uses
`TimeZoneInfo.FindSystemTimeZoneById(config.DisplayTimeZone)` with a UTC fallback if the ID
does not resolve.

### Dispatch and retry

`DispatchQueue` is a singleton wrapping an unbounded `Channel<PendingAction>` plus one
background consumer loop, cancelled by the hosted service's `StopAsync`. Same behaviour as
the Go version (retry every 30 s until success or shutdown, never block the caller) without
one unbounded task per notification. Each attempt writes an `EventLogStore` entry
(`scn-sent`, `scn-failed`, `joplin-appended`, …). HTTP goes through `IHttpClientFactory`,
which the server already registers.

## 4. Configuration model

`PluginConfiguration : BasePluginConfiguration`, persisted by the server as XML at
`<ProgramDataPath>/plugins/configurations/Jellyfin.Plugin.WatchNotify.xml` (a flat shared
directory — *not* a per-plugin folder). Saved via the dashboard's
`ApiClient.updatePluginConfiguration`, which calls `UpdateConfiguration` and raises
`ConfigurationChanged`; the hosted service subscribes to that to pick up new settings
without a restart.

| Old env var | New setting | Type | Default |
|---|---|---|---|
| `PORT`, `LOG_LEVEL`, `LOG_FORMAT` | — | — | dropped (Jellyfin owns the process and logging) |
| `DISPLAY_TZ` | `DisplayTimeZone` | string | `Europe/Berlin` |
| `WATCHED_THRESHOLD` | `WatchedThreshold` | double | `0.90` |
| `DEDUP_TTL` | `DedupMinutes` | int | `360` |
| `START_COALESCE_WINDOW` | `StartCoalesceMinutes` | int | `10` |
| — | `RetrySeconds` | int | `30` |
| — | `ScnEnabled` | bool | `false` |
| `SCN_BASE_URL` | `ScnBaseUrl` | string | `https://simplecloudnotifier.blackforestbytes.com/` |
| `SCN_USER_ID` / `SCN_KEY` | `ScnUserId` / `ScnKey` | string | — |
| `SCN_CHANNEL` | `ScnChannel` | string | — |
| `SCN_PRIORITY` | `ScnPriority` | int | `1` |
| `SCN_ACCOUNTS` | `ScnAllUsers` + `ScnUserIds` | bool + `Guid[]` | `true` / empty |
| — | `JoplinEnabled` | bool | `false` |
| `JOPLIN_BASE_URL` | `JoplinBaseUrl` | string | `http://10.8.0.4:4466` |
| `JOPLIN_TOKEN` | `JoplinToken` | string | — |
| `JOPLIN_NOTE_ID` | `JoplinNoteId` | string | — |
| `JOPLIN_ANCHOR` | `JoplinAnchor` | string | — |
| `JOPLIN_POSITION` | `JoplinPosition` | string | `before` |
| `JOPLIN_EMPTYLINE_GAP` | `JoplinEmptylineGap` | int | `0` |
| `JOPLIN_ACCOUNTS` | `JoplinAllUsers` + `JoplinUserIds` | bool + `Guid[]` | `false` / empty |
| — | `EventLogSize` | int | `500` |
| — | `WriteToActivityLog` | bool | `true` |

Two deliberate changes from the env-var version:

1. **Explicit enable checkboxes** instead of "enabled when credentials are set" — in a
   settings UI, implicit enablement is confusing, and there is no startup log to report it.
2. **Allowlists hold user GUIDs, not names.** The config page fetches the real user list via
   `ApiClient.getUsers()` and renders checkboxes, so a renamed Jellyfin account keeps working.
   The "all users" checkbox replaces the `ALL` sentinel. Validation that used to fail at
   startup (`JOPLIN_ANCHOR` required) becomes a form-level validation plus a warning banner
   on the config page and a `LogWarning` when a dispatch is skipped for that reason.

`TimeSpan` serializes awkwardly through the XML serializer, hence the integer
minute/second fields.

## 5. Event log

Three layers, cheapest first:

1. **`ILogger<T>`** in every component. Lands in the normal server log and the dashboard log
   viewer, categorised by full type name, so `Jellyfin.Plugin.WatchNotify` filters cleanly.
2. **`EventLogStore`** — the thing the dashboard page renders. A `Queue<LogEntry>` capped at
   `EventLogSize`, guarded by a lock, persisted to
   `Path.Combine(IApplicationPaths.DataPath, "watchnotify", "events.json")` (`System.Text.Json`,
   temp file + `File.Move`, debounced ~2 s), reloaded on startup. Deliberately **not**
   `DataFolderPath`, which resolves to `plugins/<Assembly>_<Version>/` and is discarded on
   every plugin update; and not `PluginConfigurationsPath`, which the server owns.
   `LogEntry`: `Timestamp`, `Kind`, `User`, `Item`, `Detail`, `Success`.
   Kinds: `playback-start`, `playback-progress-skipped`, `playback-stop`, `watched`,
   `not-watched`, `dedup-skipped`, `scn-sent`, `scn-failed`, `joplin-appended`,
   `joplin-failed`, `item-added`, `config-changed`.
3. **`IActivityManager.CreateAsync(new ActivityLog(name, type, userId) { ShortOverview = … })`**
   when `WriteToActivityLog` is on — surfaces watched events in Jellyfin's own
   Dashboard → Activity with no UI of ours. `ActivityLog` lives in
   `Jellyfin.Database.Implementations.Entities`; `IActivityManager` in `MediaBrowser.Model.Activity`.
   Only "interesting" events (watched / sent / failed) go here, not every progress ping.

## 6. Admin UI

`GetPages()` returns four `PluginPageInfo` entries: `WatchNotify` (html) + `WatchNotify.js`,
`WatchNotifyLog` (html) + `WatchNotifyLog.js`. All are embedded resources
(`<None Remove …/><EmbeddedResource Include …/>` in the csproj; path = namespace with `/` → `.`).

- Only the settings page sets `EnableInMainMenu = true` — the server filters
  `GET web/ConfigurationPages?enableInMainMenu=true`, and jellyfin-web's plugin drawer
  section renders exactly that list. That satisfies "an entry in the sidebar under Plugins".
- The log page is reached from a native tab strip: root div gets
  `class="page type-interior pluginConfigurationPage withTabs"` and the controller calls
  `LibraryMenu.setTabs(name, index, getTabs)` with
  `{ href: 'configurationpage?name=WatchNotifyLog', name: 'Log' }`.
- Page pattern: `data-controller="__plugin/WatchNotify.js"` on the root div, JS file as an ES
  module exporting `default function (view)` listening on `view.addEventListener('viewshow', …)`
  (the current webhook-plugin pattern), with `Dashboard.showLoadingMsg` /
  `Dashboard.processPluginConfigurationUpdateResult` for save feedback.
- Settings page sections: General (timezone, threshold, dedup, coalesce, retry) · SCN
  (enable, credentials, channel, priority, user checkboxes) · Joplin (enable, URL, token,
  note id, anchor, position, gap, user checkboxes) · Log (size, activity-log toggle) ·
  a "Send test notification" button.
- Log page: auto-refreshing table (timestamp, kind badge, user, item, detail), kind filter,
  "clear" button.

## 7. Plugin HTTP API

Controllers in a plugin assembly are discovered automatically as MVC application parts —
`ApplicationHost.GetApiPluginAssemblies()` scans plugin types for `ControllerBase` and
`AddApplicationPart`s their assemblies. No registration needed. Routes are **not** namespaced
under `/Plugins`.

```
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]   // MediaBrowser.Common.Api
[Route("WatchNotify")]
[Produces(MediaTypeNames.Application.Json)]
public class WatchNotifyController : ControllerBase
```

| Route | Purpose |
|---|---|
| `GET /WatchNotify/Events?limit=&kind=` | event log for the log page |
| `DELETE /WatchNotify/Events` | clear the log |
| `POST /WatchNotify/Test?target=scn\|joplin` | send a synthetic watch event, return the result |
| `GET /WatchNotify/Status` | enablement + queue depth + last error, for the config page banner |

Called from the pages with `ApiClient.ajax({ type, url: ApiClient.getUrl('WatchNotify/Events'), dataType: 'json' })`,
which attaches auth itself.

Do **not** constructor-inject `ILogger<T>` into the controller: jellyfin#11488 / #11359 —
plugin controllers are built by a logger factory that cannot resolve it. Keep logging in the
injected services instead.

## 8. Packaging and release

The format is fully specified, so a small script is enough and `jprm` stays optional.

**Zip**: `dotnet publish -c Release -f net9.0`, then zip `Jellyfin.Plugin.WatchNotify.dll`
(+ `meta.json`) at the **zip root**, named `watchnotify_<4-part-version>.zip`.

**`meta.json`** inside the zip (`PluginManifest` shape): `guid`, `name`, `overview`,
`description`, `owner`, `category`, `targetAbi`, `version`, `timestamp`, `changelog`,
`assemblies`.

**`manifest.json`** at the repo root — a JSON **array** of plugin objects:

```json
[{
  "guid": "295982d0-fc94-4c98-9bf4-54bc7cf05b30",
  "name": "WatchNotify",
  "description": "…", "overview": "…", "owner": "Mikescher", "category": "Notifications",
  "versions": [{
    "version": "1.0.0.0",
    "changelog": "…",
    "targetAbi": "10.11.0.0",
    "sourceUrl": "https://github.com/Mikescher/jellyfin-watchnotify/releases/download/v1.0.0.0/watchnotify_1.0.0.0.zip",
    "checksum": "<md5 hex of the zip>",
    "timestamp": "2026-09-04T12:00:00Z"
  }]
}]
```

`checksum` is a plain **MD5 of the zip file**, compared case-insensitively
(`InstallationManager` does `Convert.ToHexString(MD5.HashDataAsync(stream))`). `versions[]`
is newest-first and keeps old entries; the server filters by `targetAbi <= ApplicationVersion`.

**Hosting**: zip on a GitHub Release; manifest served raw:
`https://raw.githubusercontent.com/Mikescher/jellyfin-watchnotify/master/manifest.json`
→ added under Dashboard → Plugins → Repositories.

**`make release VERSION=1.0.0.0`** does: set version in `Directory.Build.props` → publish →
zip + `meta.json` → md5 → insert the version entry into `manifest.json` → `gh release create`
with the zip. CI (`.github/workflows/release.yml`) runs the same target on a tag push and
commits the updated `manifest.json` back to `master`.

## 9. Dev loop

- Build: `dotnet build`
- Deploy locally: copy `Jellyfin.Plugin.WatchNotify.dll` into
  `<jellyfin-config>/plugins/Jellyfin.Plugin.WatchNotify_1.0.0.0/` and restart the server.
  A folder without `meta.json` is still loaded (`PluginManager.LoadManifest` synthesises one
  from the `<name>_<version>` folder name), so no packaging step is needed while iterating.
  `make deploy JELLYFIN_CONFIG=…` should wrap this.
- Verify: server log filtered by `Jellyfin.Plugin.WatchNotify`, plus the plugin's own log page.

## 10. Implementation phases

1. **Scaffold** — `.csproj` (net9.0, packages with `ExcludeAssets=runtime`),
   `Directory.Build.props`, `.slnx`, `Plugin.cs`, empty `PluginConfiguration`, stub config
   page. Success: plugin shows up under Plugins with a settings page, no errors in the log.
2. **Config** — full `PluginConfiguration`, settings page with all fields, user checkboxes via
   `ApiClient.getUsers()`, save/load round-trip.
3. **Event pipeline** — `PluginServiceRegistrator`, `WatchNotifyEntryPoint`, `SessionTracker`,
   watched logic, `EventLogStore` writes. No outbound calls yet; verify against the log page
   by actually watching something.
4. **Integrations** — `ScnClient`, `JoplinClient`, `DispatchQueue` with retry, allowlist
   filtering, `POST /WatchNotify/Test`.
5. **Log UI** — `GET/DELETE /WatchNotify/Events`, log page with tab strip, optional
   `IActivityManager` entries.
6. **Packaging** — `meta.json`/zip/md5/`manifest.json` generation, `make release`, GitHub
   Actions workflow, first release, install through the repository URL end to end.
7. **Cutover** — see below.
8. **Cleanup** — delete `jellyfin-watchnotify/`, rewrite `README.md` and `CLAUDE.md` for the
   plugin, drop the Docker/Makefile remnants.

## 11. Cutover

1. Install the plugin from the repository URL, configure it, restart.
2. Watch one episode with the webhook plugin **still active**, and confirm both paths produce
   the same SCN push / Joplin line (expect one duplicate of each — dedup is per-process).
3. Delete the Generic Destination in the Webhook plugin; watch again; confirm the plugin
   alone still works.
4. Uninstall `jellyfin-plugin-webhook`.
5. Stop and remove the `ccc-jellyfin-watchnotify` container and its image.
6. Delete the Go source (phase 8).

## 12. Risks and decisions to revisit

- **`PlaybackStopped` under-reporting.** Handled by the `UserDataSaved(PlaybackFinished)`
  fallback; both paths share the dedup ledger. Worth verifying against your actual clients
  during phase 3 before trusting it.
- **Secrets in plaintext.** `ScnKey` / `JoplinToken` end up in
  `plugins/configurations/Jellyfin.Plugin.WatchNotify.xml`, readable by anyone with the config
  volume — no worse than the current `.env`, but the config page should mask them.
- **State is lost on restart** (start times, dedup ledger, in-flight retries), same as today.
  The persisted event log is the exception. Persisting the retry queue is possible later if
  restarts turn out to drop notifications.
- **ABI churn.** A 12.0 build needs net10.0 and `Jellyfin.Controller` 12.0.x; the `User`
  entity already moved namespace once (`Jellyfin.Data.Entities` → `Jellyfin.Database.Implementations.Entities`
  in 10.11), so expect small breaks. Keep both versions in `versions[]` rather than
  retargeting in place.
- **10.10 support** is not planned (net8.0 + a separate ABI entry). Add only if needed.
- **Item-added events** fire before metadata providers finish; the webhook plugin retries via
  a scheduled task. Since we only log these, ignore the incompleteness rather than replicate
  the retry machinery.
