# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A real-time analytics platform for Elite Dangerous. It ingests player journal events from the [Elite Dangerous Data Network (EDDN)](https://github.com/EDCD/EDDN) and exposes visitor statistics (station docking counts, fleet-carrier activity, most-visited systems) through a Blazor Server web UI. Live at `elite-visitors.meancat.com`.

## Projects

The solution has three projects, but **only `EliteEvents.Visitors` is deployed.**

- **`EliteEvents.Visitors`** (.NET 10, Blazor Server) — the production app. Hosts both the EDDN ingestion background service *and* the web UI in a single process. Stores everything in Redis. This is what the Dockerfile builds and `docker-compose.yaml` runs.
- **`EliteEvents.Eddn`** — reusable class library for EDDN connectivity, message deserialization, and the typed message-handler dispatch pipeline. Referenced by both web projects.
- **`EliteEvents.Journalweb`** — a separate/legacy Blazor app that reads local Elite Dangerous journal files via `EliteJournalReader` and uses SignalR. **Not in the Dockerfile or docker-compose, not deployed.** Don't assume changes here affect production.
- **`EliteEvents.Dashboard`** - Not part of the .sln but a Next.js/Typescript that displays results from redis; displays as https://elite.meancat.com/ Real-time galactic activity from the Elite Dangerous Data Network. Implemented to show a potential employer I could do TypeScript.

## Architecture (the ingestion → storage → UI flow)

1. **`EddnStream`** (`EliteEvents.Eddn`) opens a ZeroMQ `SubscriberSocket` to `tcp://eddn.edcd.io:9500`, subscribes to all topics, and zlib-decompresses each frame into a JSON string.
2. **`EddnStreamReceiver`** (a `BackgroundService` in Visitors) loops on `Receive()`, parses the JSON, and asks **`MessageFactory`** to map the `$schemaRef` to a typed message (`JournalMessage`, `ApproachSettlementMessage`).
3. For a `JournalMessage`, it looks up handlers via **`MessageHandlerProvider<TMessage, TMessageEvent>`** — a generic registry keyed by the journal `MessageEvent` enum. A handler declares which events it wants through its `Handles` array; the provider builds the event→handlers map from all registered `IMessageHandler`s at construction.
4. **`JournalMessageHandler`** is the only real handler. It reacts to `Docked` (records station or fleet-carrier docking) and `FSDJump` (records a system visit), delegating to **`DockingRedisService`**.
5. **`DockingRedisService`** owns the Redis schema and all reads/writes. Blazor pages inject it directly to render search results.

To handle a new EDDN event: add a handler implementing `IMessageHandler<JournalMessage, MessageEvent>` (or the marker `IJournalMessageHandler`), list the events in `Handles`, register it in `Program.cs`, and extend `JournalMessageHandler`'s switch or add a new handler class. The provider dispatches to all handlers registered for an event.

## Redis data model (defined entirely in `DockingRedisService`)

System and carrier names are **uppercased** before use as keys. Most keys get a rolling **30-day TTL**, refreshed on each write.

- `carrier:{ID}:daily:{yyyy-MM-dd}` — string counter, dockings that day
- `carrier:{ID}:days` — sorted set of active dates (score = unix timestamp)
- `system:{NAME}:station:{STATION}` — hash: `count`, `type`, `last_seen`
- `system:{NAME}:stations` — sorted set indexing a system's stations by last-visit time
- `systems:visits` — global most-visited leaderboard. **Different TTL:** expires weekly at Thursday 07:30 UTC, computed by `WeeklyExpirationCalculator` (a Cronos cron expression).
- `cache:system:count` — 60s cached total system count (`CachedSystemCount`), avoids a full `KEYS`/`SCAN` per request.

Search (`GetMatchingSystemsAsync` / `GetMatchingCarriersAsync`) uses `SCAN` with glob patterns, which is why `DockingRedisService` holds both an `IServer` (for KEYS/SCAN) and an `IDatabase` (for everything else).

## EDDN message types are code-generated

`EliteEvents.Eddn/Generated/*.g.cs` are **generated and gitignored** (`.gitignore` ignores `*.g.cs`). The `EliteEvents.Eddn.csproj` runs `nswag jsonschema2csclient` against the live EDDN JSON schemas (`https://eddn.edcd.io/schemas/...`) as a pre-compile target **only in Debug builds** — so the `MessageEvent` enum and message DTOs only exist after a Debug build. A custom `ReplaceIntWithLong` MSBuild task post-processes the generated files (rewrites `public int` → `public long`) to avoid overflow on Elite's large IDs.

The hand-written `Generated/Journal.cs` / `ApproachSettlement.cs` (no `.g`) add `partial class` extensions and the `IJournalMessageHandler` marker interface to the generated types.

## Commands

```bash
# Build (Debug regenerates EDDN schema clients via nswag; needs network access to eddn.edcd.io)
dotnet build

# Run the production app locally (serves UI at http://localhost:5238)
dotnet run --project EliteEvents.Visitors

# Build/push the deployed container (linux/amd64)
./build-image      # docker buildx build --platform linux/amd64 -t trasa/elite-visitors:latest .
./push-image       # docker push trasa/elite-visitors:latest
```

There are **no test projects** in the solution.

### Local development setup

Running locally needs a Redis instance. Local secrets go in `EliteEvents.Visitors/appsettings.LocalUser.json` (gitignored, Development-only) — it provides the `ConnectionStrings:redis-development` value (e.g. `localhost:6379,password=...`). The connection-string key is `redis-{environment}` where `{environment}` comes from the `Environment` config value (default `local`).

In production the Redis password is read from the file at `REDIS_AUTH_FILE` (a Docker secret) and appended to the connection string; `AbortOnConnectFail` is forced off.

## Deployment

- Versioning: **Nerdbank.GitVersioning** (`version.json`, `nbgv` tool). CI fetches full git history (`fetch-depth: 0`) for this.
- `docker-compose.yaml` runs the `visitors` container plus a **Caddy** reverse proxy (`Caddyfile`) terminating TLS and proxying to `127.0.0.1:8080`. Both use host networking.
- GitHub Actions are **manual (`workflow_dispatch`) only**: `build-push-images.yml` builds+pushes the image; `deploy.yml` SCPs compose/Caddy files to the DO host and runs `deploy-stack` (pulls image, `docker compose up -d`).

## Notes

- `redis_key_fixer.py` is a one-off maintenance script (normalizes existing system keys to uppercase) — not part of the running app.
- `Program.cs` has commented-out SignalR hub wiring (`MapHub<EliteHub>`); the Visitors UI is pure Blazor Server interactivity, no custom hub.
