# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A real-time analytics platform for Elite Dangerous. It ingests player journal events from the
[Elite Dangerous Data Network (EDDN)](https://github.com/EDCD/EDDN) and exposes visitor statistics
(station docking counts, fleet-carrier activity, most-visited systems) plus a live event ticker.
Live at `elite.meancat.com`, running on DigitalOcean Kubernetes.

## Projects

Three projects are deployed as two containers, plus one legacy app that isn't.

- **`EliteEvents.Eddn`** — class library. EDDN connectivity, generated message types, the typed
  handler-dispatch pipeline, **and the whole Redis storage layer** (`Storage/`). Both containers
  reference it; it is the only place Redis keys are constructed.
- **`EliteEvents.Ingestion`** (.NET 10) — the EDDN writer. A minimal ASP.NET host so k8s has HTTP
  probes; it exposes nothing but `/health/live` and `/health/ready`. **Must run as a single
  replica** — two subscribers would double-count every event.
- **`EliteEvents.Web`** (.NET 10) — the public site. Razor Components in **static SSR** (no
  circuit, no WebSocket) plus htmx, serving HTML, a JSON API and an SSE ticker. Stateless and
  horizontally scalable; reads Redis, never writes it.
- **`EliteEvents.JournalWeb`** — a separate/legacy Blazor app that reads local Elite Dangerous
  journal files via `EliteJournalReader` and uses SignalR. **Not containerised, not deployed.**
  Don't assume changes here affect production.

## Architecture (ingestion → storage → UI)

1. **`EddnStream`** opens a ZeroMQ `SubscriberSocket` to `tcp://eddn.edcd.io:9500` and
   zlib-decompresses each frame into JSON.
2. **`EddnStreamReceiver`** (a `BackgroundService` in Ingestion) loops on `Receive()`, parses, and
   asks **`MessageFactory`** to map `$schemaRef` to a typed message. It also detects silence and
   rebuilds the socket after `EddnOptions.ReconnectAfterSilence`. Per-message failures are caught
   and logged — an exception escaping `ExecuteAsync` would stop the whole host.
3. Handlers are looked up through **`MessageHandlerProvider<TMessage, TMessageEvent>`**, a registry
   keyed by the journal `MessageEvent` enum; each handler declares its events in `Handles`.
4. **`JournalMessageHandler`** reacts to `Docked` and `FSDJump`, writing through **`IDockingWriter`**
   and publishing a **`LiveEvent`** to the `eddn:events` channel via **`IEventPublisher`**.
5. **`EliteEvents.Web`** reads through **`IDockingReader`** / **`ICachedSystemCount`**, and its
   **`LiveEventHub`** holds one Redis subscription per pod, fanned out to SSE clients through
   per-client channels.

The reader/writer split is structural: Ingestion registers `AddEliteRedisWriter()` and Web
registers `AddEliteRedisReader()`, so "the web tier never writes" is enforced by DI, not
convention.

To handle a new EDDN event: add a handler implementing `IMessageHandler<JournalMessage, MessageEvent>`
(or the marker `IJournalMessageHandler`), list the events in `Handles`, register it in
`EliteEvents.Ingestion/Program.cs`, and extend `JournalMessageHandler`'s switch or add a new class.

## Redis data model (defined entirely in `EliteEvents.Eddn/Storage/RedisKeys.cs`)

System and carrier names are **uppercased** before use as keys. Most keys get a rolling **30-day
TTL**, refreshed on each write.

- `carrier:{ID}:daily:{yyyy-MM-dd}` — string counter, dockings that day
- `carrier:{ID}:days` — sorted set of active dates (score = unix timestamp)
- `system:{NAME}:station:{STATION}` — hash: `count`, `type`, `last_seen`
- `system:{NAME}:stations` — sorted set indexing a system's stations by last-visit time
- `systems:visits` — global most-visited leaderboard. **Different TTL:** expires weekly at
  Thursday 07:30 UTC, computed by `WeeklyExpirationCalculator` (a Cronos cron expression)
- `cache:system:count` — 60s cached total system count (`CachedSystemCount`)
- `heartbeat:eddn` — unix-ms timestamp of the last EDDN message, written by Ingestion at most
  every 5s. This is how the web tier's health check sees ingestion liveness across containers
- `eddn:events` — pub/sub channel carrying `LiveEvent` frames for the ticker

Search (`GetMatchingSystemsAsync` / `GetMatchingCarriersAsync`) uses `SCAN` with glob patterns,
which is why `DockingReader` holds both an `IServer` and an `IDatabase`.

## Health endpoints

Both containers expose `/health/live` (**no checks at all** — the process answering is the signal)
and `/health/ready`. Liveness is deliberately lenient so k8s doesn't restart a pod that is merely
waiting out a quiet EDDN period or retrying Redis.

Readiness differs by tier: Ingestion requires Redis **and** a fresh EDDN heartbeat; Web requires
only Redis, because a silent firehose must not drain the Service while pods still serve 30-day
data. Web additionally serves `/health` (plain text, all checks) and `/api/health` (JSON,
`{"status":"ok","redis":"ok"}`) for uptime monitors.

## EDDN message types are code-generated

`EliteEvents.Eddn/Generated/*.g.cs` are **generated and gitignored**. The `NSwag` target in
`EliteEvents.Eddn.csproj` runs `nswag jsonschema2csclient` against the live EDDN JSON schemas
(needs network access to eddn.edcd.io) when the configuration is Debug **or the output files are
missing** — the latter is what lets a clean checkout, CI, or a container build compile at all. The
target re-adds the generated files to `@(Compile)` because MSBuild expands the `**/*.cs` glob at
project load, before the target runs. A custom `ReplaceIntWithLong` task rewrites `public int` →
`public long` to avoid overflow on Elite's large IDs.

The hand-written `Generated/Journal.cs` / `ApproachSettlement.cs` (no `.g`) add `partial class`
extensions and the `IJournalMessageHandler` marker interface.

## Commands

```bash
# Build (regenerates EDDN schema clients when missing; needs network access to eddn.edcd.io)
dotnet build

# Run locally — needs both, plus a local Redis
dotnet run --project EliteEvents.Ingestion   # writer, health on http://localhost:5239
dotnet run --project EliteEvents.Web         # site on http://localhost:5240

# Images and deployment (see k8s/README.md)
./build-image      # both images, linux/amd64, tagged from nbgv
./push-image       # to registry.digitalocean.com/meancat
./deploy-k8s <tag> # apply manifests, pin the tag, wait for both rollouts
```

There are **no test projects** in the solution.

### Local development setup

Running locally needs a Redis instance. Local secrets go in `appsettings.LocalUser.json` in each
runnable project (gitignored, Development-only) providing `ConnectionStrings:Redis`, e.g.
`localhost:6379,password=...`.

In production the Redis password is read from the file at `REDIS_AUTH_FILE` (a k8s Secret mounted
as a file); it is set on the parsed `ConfigurationOptions` rather than appended to the connection
string, so a password containing `,` or `=` can't corrupt the parse. `AbortOnConnectFail` is
forced off so the app starts and retries when Redis lags behind it.

## Deployment

- **Registry:** DigitalOcean Container Registry, `registry.digitalocean.com/meancat`, repos
  `elite-web` and `elite-ingestion`. DOKS's registry integration creates a pull secret named after
  the registry — **`meancat`**, not `registry-meancat`.
- **Cluster:** DOKS `elite` in sfo3, 2 × `s-1vcpu-2gb`, non-HA control plane. Redis runs
  **in-cluster** as a single-replica StatefulSet; there is no managed database.
- **Manifests:** `k8s/`, applied with `kubectl apply -k k8s/`. Full provisioning runbook and the
  reasoning behind the probe/redirect/TLS choices are in `k8s/README.md`.
- **Versioning:** Nerdbank.GitVersioning (`version.json`, `nbgv`). CI needs `fetch-depth: 0`.
- **Workflows** are manual (`workflow_dispatch`) only: `build-push-images.yml` builds and pushes to
  DOCR; `deploy-k8s.yml` deploys a tag that workflow already produced.

## Notes

- `redis_key_fixer.py` is a one-off maintenance script (normalises existing system keys to
  uppercase) — not part of the running app.
- `elite-visitors.meancat.com` 301s to `elite.meancat.com` via a separate Ingress. The path is not
  preserved; see the comment in `k8s/45-redirect-ingress.yaml`.
- `ROADMAP.md` records the migration from the old droplet stack (Blazor Server + a Next.js
  dashboard behind Caddy) and, importantly, *why* several things are shaped the way they are.
