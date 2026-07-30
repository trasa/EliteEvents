# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A real-time analytics platform for Elite Dangerous. It ingests player journal events from the
[Elite Dangerous Data Network (EDDN)](https://github.com/EDCD/EDDN) and exposes visitor statistics
(station docking counts, fleet-carrier activity, most-visited systems) plus a live event ticker.
Live at `elite.meancat.com`, running on DigitalOcean Kubernetes.

## Projects

Three .NET projects are deployed as two containers, plus a Go operator and one legacy app that
isn't deployed.

- **`EliteEvents.Eddn`** — class library. EDDN connectivity, generated message types, the typed
  handler-dispatch pipeline, **and the whole Redis storage layer** (`Storage/`). Both containers
  reference it; it is the only place Redis keys are constructed.
- **`EliteEvents.Ingestion`** (.NET 10) — the EDDN writer. A minimal ASP.NET host so k8s has HTTP
  probes; it exposes nothing but `/health/live` and `/health/ready`. **Must run as a single
  replica** — two subscribers would double-count every event.
- **`EliteEvents.Web`** (.NET 10) — the public site. Razor Components in **static SSR** (no
  circuit, no WebSocket) plus htmx, serving HTML, a JSON API and an SSE ticker. Stateless and
  horizontally scalable; reads Redis, never writes it.
- **`operator/`** — a Kubebuilder/controller-runtime operator (Go, its own module) managing
  `kind: FeedListener` in group `elite.meancat.com`. It reconciles a declared EDDN subscription
  into the ConfigMap, Service and per-shard Deployments that `k8s/20-ingestion.yaml` used to
  spell out by hand. **Built but not yet cut over** — see below.
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
   Each `IDockingWriter` call dispatches its writes as a single **`IBatch`** — a station docking is
   one round-trip, not six. Batches are pipelined, not transactional; that is safe here because
   ingestion is a single writer and every operation is a commutative increment or idempotent set.
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
- `index:systems` / `index:carriers` — **search indexes.** Sorted sets, one member per searchable
  name, **every member at score 0** and **no TTL**. See below

### Search and the indexes

Search and the system count both used to `SCAN` the whole keyspace with a glob. The keyspace is
dominated by per-station hashes, so that was O(keyspace) for something that only ever looks at
names. Both now go through `index:systems` / `index:carriers`:

- `GetMatchingSystemsAsync` / `GetMatchingCarriersAsync` do a `ZRANGEBYLEX` prefix lookup first,
  then fall back to a `ZSCAN` with a `*query*` MATCH **only if the prefix pass didn't fill the
  requested `limit`**. That `limit` is the knob that matters: ask for a small page and a prefix
  query never touches the fallback, which is what makes keystroke typeahead viable.
- `CachedSystemCount` is now a `ZCARD`. The 60s cache is no longer load-bearing, just a saved
  round-trip on a number rendered on every page.
- Results are **prefix matches first**, then substring matches, each alphabetical.

Two constraints drive the design and are easy to break by accident:

- **Every member must be at score 0.** `ZRANGEBYLEX` is only defined when scores are equal. This
  is why the index can't also carry a last-seen timestamp and be pruned by score.
- **The index therefore can't expire itself**, and a TTL on the key would drop the whole thing.
  `SearchIndexMaintainer` (run hourly by `SearchIndexMaintenanceService` in Ingestion) reconciles
  each index against the live keys instead, so the 30-day TTL on the data stays the single source
  of truth. One rebuild covers stale entries, backfill, and recovering an index that
  `allkeys-lru` evicted — it snapshots the index *before* scanning so a name the writer adds
  mid-scan can't be mistaken for stale and removed.

Consequence worth knowing: the web tier now depends on a key only Ingestion maintains. On the
deploy that introduces the index, search returns nothing and the count reads 0 until Ingestion's
first rebuild — seconds, and it retries on a short interval until one succeeds.

Station names are no longer searchable. The old glob matched the station segment too, so a query
could hit a system via one of its stations — but station names are stored verbatim while queries
are uppercased, so in practice that only ever fired for stations that were already all-caps.

## The FeedListener operator (`operator/`)

An EDDN subscription declared as a resource. `spec` carries the relay endpoint, consumer count,
image, Redis connection and reconnect threshold; the controller reconciles that into one
ConfigMap, one Service, and **one Deployment per shard** (`replicas: 1`, `strategy: Recreate`).

Three things justify an operator over a Deployment, and each shapes the code:

- **`spec.consumers` is a shard count, not a replica count.** EDDN broadcasts and its frames carry
  no topic (`EddnStream` calls `SubscribeToAnyTopic()`), so every subscriber gets every message
  and N unfiltered replicas would count each docking N times. Each pod receives a distinct
  `Eddn__ShardIndex` and drops messages that don't hash to it (`MessageShardFilter`). One
  Deployment per shard is what makes the indexes distinct — a single Deployment gives every
  replica an identical pod spec. The controller also **prunes shards on scale-down**: their owner
  is still alive, so nothing garbage-collects them and they would keep writing.
- **A finalizer drains Redis, in order.** `index:systems` / `index:carriers` have no TTL and are
  maintained only by a running listener, so deleting the resource would orphan them. The
  finalizer deletes the consumer Deployments, waits for their pods to be gone, then runs a Job on
  the **ingestion image** with `--purge-indexes` (`PurgeCommand` → `SearchIndexPurger`). Purging
  while a consumer still ran would be undone by its next write. If the drain Job fails past its
  backoff limit the finalizer is released anyway, loudly — a finalizer that can't be satisfied
  wedges the namespace, which is worse than stale keys.
- **`status` separates running from receiving.** The controller polls each pod's `/health/stream`
  by pod IP (not through the Service — a ClusterIP would answer from an arbitrary shard) and sets
  `Available` (pods Ready) apart from `Streaming` (every shard got a message within
  `reconnectAfterSilence`). Nothing in the k8s API changes when a relay goes quiet, so status is
  re-derived on a 30s `RequeueAfter`.

Two cross-language contracts are easy to break silently:

- **`Eddn__ReconnectAfterSilence` is rendered `HH:MM:SS`.** Go's `Duration.String()` produces
  `2m0s`, which .NET's `TimeSpan` parser rejects — options binding would fall back to the default
  and the spec value would be ignored with no error.
- **`MessageShardFilter.StableHash` must never change.** It is FNV-1a over UTF-16 code units
  **plus a Murmur3 `fmix32` avalanche**. The avalanche is load-bearing: raw FNV-1a leaves the low
  bits unmixed (multiplying by an odd prime preserves them), so `% 2`, `% 4`, `% 8` and `% 16`
  all put the entire feed in one bucket — measured, and caught by a test. Changing the hash during
  a rolling update repartitions the feed mid-flight, so the digest is pinned by test literals.

The controller never speaks Redis: `RedisKeys` stays the only definition of the keyspace, which is
why teardown runs the app's own image rather than deleting keys by name from Go.

**Cutover status:** `k8s/25-feedlistener.yaml` exists but is deliberately **not** in
`kustomization.yaml`; listing it alongside `20-ingestion.yaml` would run two writers. The runbook
is in `k8s/README.md`.

## Health endpoints

Both containers expose `/health/live` (**no checks at all** — the process answering is the signal)
and `/health/ready`. Ingestion additionally serves `/health/stream` — JSON with `lastMessageUtc`,
`messagesReceived`, `messagesHandled`, `shardIndex` and `shardCount`, read per-pod by the
FeedListener controller. It describes *this instance only*, which is the point: a shard reporting
healthy receives but zero handled is a partition fault, not a feed outage. Liveness is deliberately lenient so k8s doesn't restart a pod that is merely
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

# Tests — no Redis, no network of their own (but see the build note above)
dotnet test EliteEvents.Eddn.Tests

# Operator (Go, separate module — see operator/README.md)
cd operator && make test      # unit tests + envtest; Makefile pins GOTOOLCHAIN=go1.26.5
cd operator && make manifests generate   # after editing api/v1alpha1/feedlistener_types.go

# Images and deployment (see k8s/README.md)
./build-image      # all three images, linux/amd64, tagged from nbgv
./push-image       # to registry.digitalocean.com/meancat
./deploy-k8s <tag> # write the tag into kustomization.yaml, apply, wait for both rollouts
                   # commit kustomization.yaml afterwards — it records the deployed version
```

### Tests

**`EliteEvents.Eddn.Tests`** (xUnit) is the only .NET test project. It covers `RedisKeys`,
`WeeklyExpirationCalculator` and `MessageShardFilter` — pure logic, so it needs no Redis and runs
in well under a second. The operator has its own suite under `operator/` (`make test`): unit tests
for the resource builders plus an envtest suite that runs the reconciler against a real API
server.

`MessageShardFilter` is tested for the partition property, not the hash: every message must be
owned by exactly one shard (a gap loses events, an overlap double-counts them), and every shard
must get a working share. That second test is what caught raw FNV-1a sending 100% of the feed to
one shard at every power-of-two consumer count — the partition was still exactly-once, so only the
distribution test failed.

`RedisKeys` is tested as a **wire format**, not an implementation detail: ingestion and the web
tier are separate containers whose only agreement is the shape of those strings, so a "harmless"
change there splits the keyspace silently — the writer keeps writing, the reader finds nothing,
the site just empties. The tests therefore assert key literals verbatim rather than rebuilding
them from the same interpolation the production code uses. Several tests deliberately pin known
quirks (search patterns matching the station segment, `ExtractName` returning `visits` for
`systems:visits`); if one of those is ever intentionally changed, the failing test should be
deleted on purpose, not worked around.

Note that `dotnet test` builds `EliteEvents.Eddn` in Debug, which runs the NSwag target — so it
needs network access to eddn.edcd.io like any other Debug build, even though the tests themselves
touch nothing external.

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
