# jellyfin-watchnotify

A Jellyfin server plugin (`net9.0`, ABI `10.11.0.0`) that sends an SCN push and appends a
Joplin watch-log line when a movie or episode is watched to completion.

## Layout
- `Plugin.cs` — `BasePlugin<PluginConfiguration>`, `IHasWebPages`; GUID `295982d0-fc94-4c98-9bf4-54bc7cf05b30`.
- `PluginServiceRegistrator.cs` — singletons plus the hosted service.
- `Configuration/` — `PluginConfiguration` and the two dashboard pages, embedded as resources.
- `Watch/` — `WatchNotifyEntryPoint` (event wiring), `SessionTracker` (start times + dedup),
  `WatchEventFactory` (titles/progress), `Format` (shared rendering).
- `Dispatch/` — `ScnClient`, `JoplinClient`, `DispatchQueue` (per-target retry loops).
- `Logging/` — `EventLogStore`, persisted to `<data>/watchnotify/events.json`.
- `Api/WatchNotifyController.cs` — `/WatchNotify/{Status,Test,Events}`, elevation required.

## Key facts
- "Watched" = `PlayedToCompletion || position/runtime >= WatchedThreshold` (0.90), Movie/Episode only.
- `UserDataSaved(PlaybackFinished)` is a delayed second path for clients that under-report
  the stop; it shares the dedup ledger, so a watch is notified once.
- SCN: form `POST` to the base url, fields `user_id`,`key`,`title`,`content`,`channel`,`priority`,`sender_name`,`msg_id`.
- Joplin: `POST {base}/notes/{id}/insert`, Bearer auth, body `{content, search, position, search_emptyline_gap}`.
- User allowlists hold Jellyfin user GUIDs; ids are compared with `Guid.TryParse` so either
  hyphenated or bare forms work.
- Controllers must not inject `ILogger<T>` (jellyfin#11488); logging lives in the services.
- Jellyfin packages carry `ExcludeAssets=runtime`, so the published output is only our dll.

## Build / test
- `make build`, `make deploy JELLYFIN_CONFIG=…`, `make package`.
- `make release VERSION=x.y.z.w`, then tag `vx.y.z.w`; CI builds the zip and writes the
  `manifest.json` entry with that artifact's md5.
