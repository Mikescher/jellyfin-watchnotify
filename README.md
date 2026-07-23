# jellyfin-watchnotify

A small Go server that receives [jellyfin-plugin-webhook](https://github.com/jellyfin/jellyfin-plugin-webhook)
events and, when a movie or episode is watched to completion, fires two actions:

1. **SCN push** — a [Simple Cloud Notifier](https://simplecloudnotifier.blackforestbytes.com/api)
   notification with who / what / start / end / percentage.
2. **Joplin line** — appends a line to a shared watch-log note via a custom Joplin API server.

Standard library only, no external dependencies.

## Jellyfin plugin setup

Install the **Webhook** plugin, add a **Generic Destination**, and configure:

- **Webhook URL**: `http://<host>:<port>/webhook`
- **Notification Types**: Playback Progress, Playback Start, Playback Stop, Session Start, Item Added
- **Send All Properties (ignores template)**: ✅ enabled

## "Watched" logic

On a **Playback Stop** for a Movie or Episode, the item counts as watched when
Jellyfin's `PlayedToCompletion` is true **OR** `position / runtime >= WATCHED_THRESHOLD`
(default `0.90`, matching Jellyfin's own `MaxResumePct=90`). This tolerates skipped
intros/outros. Duplicate stops for the same user+item within `DEDUP_TTL` are ignored.

The watch **start** time comes from the matching Playback Start event (tracked in memory);
the **end** time is the Playback Stop event time. Session/dedup state is in-memory and reset on restart.

## Configuration

All configuration is via environment variables — see [`.env.example`](.env.example).

| Var | Default | Purpose |
|---|---|---|
| `PORT` | `80` | Listen port |
| `LOG_LEVEL` | `info` | `debug` logs full raw payloads |
| `LOG_FORMAT` | `text` | `text` or `json` |
| `WATCHED_THRESHOLD` | `0.90` | Watched fraction fallback |
| `DEDUP_TTL` | `6h` | Duplicate-suppression window |
| `START_COALESCE_WINDOW` | `10m` | Seeks within this gap keep the original start time |
| `SCN_USER_ID` / `SCN_KEY` | — | SCN credentials (both required to enable SCN) |
| `SCN_CHANNEL` | — | Optional SCN channel |
| `SCN_PRIORITY` | `1` | 0 / 1 / 2 |
| `SCN_ACCOUNTS` | `ALL` | `ALL` or csv of usernames |
| `JOPLIN_BASE_URL` | `http://10.8.0.4:4466` | Joplin API server |
| `JOPLIN_TOKEN` | — | Bearer token (required to enable Joplin) |
| `JOPLIN_NOTE_ID` | — | Shared note id (required to enable Joplin) |
| `JOPLIN_ANCHOR` | — | Insert new lines before this marker (required when Joplin is enabled) |
| `JOPLIN_POSITION` | `before` | `before` / `after` |
| `JOPLIN_EMPTYLINE_GAP` | `0` | Passed as `search_emptyline_gap`: blank lines kept between the inserted line and the anchor |
| `JOPLIN_ACCOUNTS` | — | `ALL` or csv of usernames (required to enable Joplin) |

An integration only runs when its required variables are present; the startup log
reports which are enabled.

The Joplin line looks like:

```
[2026-07-18 19:04:22]: The Matrix                    // From Jellyfin [alice]
[2026-07-18 20:04:22]: Firefly [S01E02]              // From Jellyfin [family-acc]
```

## Running

Local:

```sh
cp .env.example .env   # fill in credentials
make run               # loads .env, runs on $PORT
```

Docker (server listens on 80; map to any host port):

```sh
make docker
docker run --rm -p 8096:80 --env-file .env jellyfin-watchnotify
docker logs -f <container>   # incoming webhooks + actions are logged here
```

Set `IMAGE` for pushing: `make push-docker IMAGE=registry.example.com/jellyfin-watchnotify`.

## Quick test

```sh
# Playback Start (records the watch-start time)
curl -s localhost:8080/webhook -H 'Content-Type: application/json' -d '{
  "NotificationType":"PlaybackStart","Id":"sess1","ItemId":"m1","ItemType":"Movie",
  "Name":"The Matrix","Year":1999,"UserId":"u1","NotificationUsername":"alice",
  "RunTimeTicks":81600000000,"PlaybackPositionTicks":0,
  "Timestamp":"2026-07-18T19:04:22.0000000+02:00","DeviceName":"Living Room TV","ClientName":"Jellyfin Android"
}'

# Playback Stop at 95% -> "watched" -> SCN + Joplin attempts
curl -s localhost:8080/webhook -H 'Content-Type: application/json' -d '{
  "NotificationType":"PlaybackStop","Id":"sess1","ItemId":"m1","ItemType":"Movie",
  "Name":"The Matrix","Year":1999,"UserId":"u1","NotificationUsername":"alice",
  "RunTimeTicks":81600000000,"PlaybackPositionTicks":77520000000,"PlayedToCompletion":true,
  "Timestamp":"2026-07-18T20:20:00.0000000+02:00","DeviceName":"Living Room TV","ClientName":"Jellyfin Android"
}'
```
