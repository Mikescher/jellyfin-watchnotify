# jellyfin-watchnotify

Small stdlib-only Go HTTP server that receives jellyfin-plugin-webhook "Generic
Destination" events (with **Send All Properties** enabled) at `POST /webhook` and,
when a movie/episode is watched to completion, sends an SCN push and appends a line
to a Joplin note.

## Layout (flat `package main`, no external deps)
- `main.go` — config load, slog setup, HTTP server + graceful shutdown, `/webhook` + `/health`.
- `config.go` — env parsing; `AccountSet` allowlist (`ALL` or csv).
- `payload.go` — `WebhookPayload` (flat PascalCase JSON) + derived helpers; `WatchEvent`.
- `sessions.go` — in-memory start-time tracker + dedup ledger (mutex + GC ticker).
- `handler.go` — routes by `NotificationType`; "watched" logic on `PlaybackStop`.
- `scn.go` — SCN form-POST client. `joplin.go` — Joplin `POST /notes/:id/insert` client.
- `util.go` — `newUUID`, `padRight`, `fmtPct`.

## Key facts
- "Watched" = `PlayedToCompletion || position/runtime >= WATCHED_THRESHOLD` (0.90), Movie/Episode only.
- SCN: `POST` form to base URL, fields `user_id`,`key`,`title`,`content`,`channel`,`priority`,`sender_name`,`timestamp`,`msg_id`.
- Joplin: `POST {base}/notes/{id}/insert`, Bearer auth, body `{content, search:<anchor>, position:"before"}`.
- Integrations self-enable only when their required env vars are set.

## Build / test
- `go vet ./... && go build ./...`
- `make run` (loads `.env`, `PORT=8080`), `make docker`, `make push-docker IMAGE=...`.
- Docker listens on port 80.
