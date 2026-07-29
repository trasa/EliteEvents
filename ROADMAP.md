# Roadmap — merge Visitors + Dashboard into one ASP.NET/htmx app

Status: **Phase 1 complete** (uncommitted). Branch: `blazor`.

## Goal

Replace the two front ends (`EliteEvents.Visitors`, Blazor Server; `EliteEvents.Dashboard`,
Next.js/TypeScript) with a single ASP.NET Core + htmx web app that becomes the new
`https://elite.meancat.com`. Redis serialization lives in **one** language, in **one** place
(`EliteEvents.Eddn`), and every Redis call is in-process C# — no HTTP hops between our own apps.

Ingestion splits out of the web app into its own container so the web tier can scale while
EDDN ingestion stays a single writer (`replicas: 1`). The web app becomes **stateless** — static
SSR, no Blazor Server circuit — so pods are interchangeable and need no session affinity.

## Target topology

```
EliteEvents.Eddn        (class library)
    EDDN stream + generated message types + NEW: Redis storage layer
        |
        +-- EliteEvents.Ingestion   worker + /health   writes Redis   [replicas: 1]
        +-- EliteEvents.Web         ASP.NET + htmx     reads Redis    [stateless, scalable]
        +-- EliteEvents.JournalWeb  untouched legacy

deleted: EliteEvents.Visitors, EliteEvents.Dashboard
```

Redis is the only coupling between the two containers.

## Decisions made

| Decision | Choice |
|---|---|
| Server-side rendering | **Razor Components, static SSR** — `AddRazorComponents()` *without* `AddInteractiveServerComponents()`. Keeps `.razor` syntax, no circuit, no WebSocket, no `@rendermode`. Minimal-API endpoints return `RazorComponentResult<T>` for htmx fragments. |
| Connection string | Collapse `ConnectionStrings:redis-{environment}` to plain `ConnectionStrings:Redis` (`ConnectionStrings__Redis` env var) |
| Old Blazor app | Retire `EliteEvents.Visitors`; `elite-visitors.meancat.com` redirects to `elite.meancat.com` |
| Next.js Dashboard | Drop it — delete `EliteEvents.Dashboard/` |
| Look and feel | Elite theme from Visitors (dark + orange, EUROCAPS/Sintony, Bootstrap 5) |
| Process split | Two containers: web app and EDDN service |
| Rollout | Bring the new stack up in **DigitalOcean k8s (`doctl`)** and run it side-by-side with the existing droplets, then cut over |
| Data | **Data loss is acceptable.** The k8s stack starts on an empty Redis and refills from the EDDN firehose. No migration, no export/import. |
| Redis in k8s | **In-cluster StatefulSet**, single replica — cheaper than a second managed instance, and the data is disposable and TTL'd |
| Ingress / Caddy | Out of scope here — handled in the k8s step (Phase 4) |

### Why side-by-side is safe

Each stack owns its own Redis: the droplets keep their DigitalOcean managed Redis, the k8s stack
gets its own. Both ingest from EDDN at the same time and write to **different** stores, so there is
no double-counting and no shared-writer hazard. EDDN is a broadcast firehose — a second subscriber
costs it nothing. Cutover is a DNS/ingress change, not a data operation.

## Open questions

- [ ] Phase 6 items — which, if any, are in scope.

---

## Phase 1 — Extract the Redis layer into `EliteEvents.Eddn.Storage`

Pure refactor. `EliteEvents.Visitors` still builds and runs at the end of this phase; that is how
it gets verified.

New under `EliteEvents.Eddn/Storage/`:

- [x] **`RedisKeys`** — every key format and the uppercase normalization in one static class.
      Today `DockingRedisService`, `CachedSystemCount`, `RedisHealthCheck` and the Next
      `queries.ts` each hand-build key strings. This is the core "one schema, one language" win.
- [x] **`EliteRedisOptions` + `AddEliteRedis()`** — the `IConnectionMultiplexer` factory currently
      inlined at `EliteEvents.Visitors/Program.cs:34-48`, including `REDIS_AUTH_FILE` handling
      (which carries over cleanly to a k8s Secret mounted as a file). Switches to the
      `ConnectionStrings:Redis` key.
- [x] **Models** — `StationDockingInfo`, `CarrierDockingInfo`, `SystemVisitInfo`, plus a typed
      `LiveEvent` record replacing the anonymous object in `JournalMessageHandler`.
- [x] **`IDockingWriter` / `DockingWriter`** — `RecordStationDocking`,
      `RecordFleetCarrierDocking`, `RecordSystemVisit`. Ingestion only.
- [x] **`IDockingReader` / `DockingReader`** — `GetSystemDocking`, `GetCarrierDocking`,
      `GetSystemVisits`, `GetMatchingSystems`, `GetMatchingCarriers`, plus `CachedSystemCount`.
      Web only.
- [x] **`IEventPublisher` / `IEventSubscriber`** — the `eddn:events` pub/sub channel, typed on
      `LiveEvent`.
- [x] `WeeklyExpirationCalculator` moves here (the Cronos dependency moves to the library).

Splitting reader from writer makes "the web container never writes" a structural property rather
than a convention.

**Note:** `StackExchange.Redis` and `Cronos` become `EliteEvents.Eddn` dependencies, and
`EliteEvents.JournalWeb` references that library — so it inherits them. Harmless; it won't use them.

### Phase 1 outcome

Verified: solution builds clean; a throwaway harness confirmed all Redis key formats, scan
patterns, TTLs, weekly-reset cron dates, and the `eddn:events` JSON are byte-identical to the
pre-refactor code; the app boots, resolves the new DI graph, and serves every route.

Deliberate behavior changes, all improvements:

- **The Redis password is no longer written to stdout.** The old factory appended the secret to
  the connection string and then `Console.WriteLine`d the result, leaking it into container logs
  on every start. The password is now set on the parsed `ConfigurationOptions` — which also means
  a password containing `,` or `=` can no longer corrupt the parse — and the startup log prints
  only endpoints, SSL, and whether a password was found.
- **Live-ticker publish failures no longer propagate.** `RedisEventPublisher` logs and swallows;
  the ticker is decorative and must not cost us the docking record the same handler just wrote.
- **`LiveEvent` omits null `station`/`stationType`**, so an `fsdjump` frame stays exactly the
  three-key object the old anonymous type produced.

Quirks preserved as-is rather than silently "fixed":

- `RedisKeys.ExtractName` returns segment 1 of any key, so `systems:visits` parses to `visits`.
  Unreachable — stored names and search patterns are upper-cased, that key is not.
- Substring search matches the station segment too, so a query can hit a system by way of one of
  its station names.
- `GetSystemVisitsAsync` fetches the whole leaderboard, filters `score > 1`, then applies `topN`.

### Pre-existing issues found while verifying (not introduced here)

- [ ] **An unreachable Redis kills the whole host.** A `RedisConnectionException` from a docking
      write propagates out of `EddnStreamReceiver.ExecuteAsync`, and the default
      `BackgroundServiceExceptionBehavior.StopHost` stops the app. Today `restart: unless-stopped`
      masks it; under k8s this is a crash-loop on any Redis blip. **Fix in Phase 2** — catch and
      log per-message, and let readiness rather than process death report the outage.
- [ ] **`/` and `/system-search` return 500 when Redis is down**, because both call
      `CachedSystemCount` in `OnInitializedAsync` with no try/catch, unlike every other page.
      **Fix in Phase 3** when these pages are rewritten.

## Phase 2 — `EliteEvents.Ingestion`

ASP.NET minimal host rather than a bare Worker, because k8s probes want HTTP. Exposes **only**
`/health/live` and `/health/ready`. No UI, no static files.

- [ ] Move in `EddnStreamReceiver`, `JournalMessageHandler`, `StreamHealthTracker`,
      `EddnStreamHealthCheck`, and the handler DI registration.
- [ ] Receiver writes a throttled (~5s) **`heartbeat:eddn`** key holding the last-message
      timestamp. `EddnStreamHealthCheck` reads an in-process field today; once ingestion and web
      are separate processes that liveness signal has to travel through Redis for the web tier and
      any uptime monitor to see it.
- [ ] Liveness = process responsive; readiness = Redis reachable **and** the EDDN stream not
      silent past `EddnOptions.ReconnectAfterSilence`. Keep liveness lenient so k8s does not
      restart a pod that is merely waiting out a quiet EDDN period.

## Phase 3 — `EliteEvents.Web`

Elite theme carried over wholesale: `app.css`, EUROCAPS/Sintony fonts, Bootstrap 5, the sidebar
`NavMenu`, `wwwroot/images`. htmx + the SSE extension vendored into `wwwroot/lib/htmx`
(self-hosted, matching how Bootstrap is handled today).

Routes, merging both apps:

| Route | From | Notes |
|---|---|---|
| `/` | Dashboard home | leaderboard + live-ticker panels restyled to the Elite theme; Visitors' About/Credits folded in below |
| `/most-visited` | Visitors | full leaderboard |
| `/system-search` | Visitors | htmx `hx-get` on submit, swaps the results card |
| `/system/{name}` | both | canonical; 301 from `/system-details/{name}` |
| `/carrier-search` | Visitors | |
| `/carrier/{id}` | Visitors | canonical; 301 from `/carrier-details/{id}` |
| `/api/most-visited`, `/api/system/{name}`, `/api/carrier/{id}` | Dashboard | JSON, same shapes — keeps existing elite.meancat.com API consumers working |
| `/api/stream` | Dashboard | SSE |
| `/health`, `/api/health` | both | both paths; each old app used a different one |

- [ ] **SSE ticker:** one shared `ISubscriber` subscription per pod, fanned out to that pod's
      connected clients via a per-client `Channel<T>` — fixes the per-client-connection design the
      Next app flagged as a TODO in its own README. The server renders the `<li>` fragment and
      pushes HTML (the htmx-native approach), so `sse-swap="message"` + `hx-swap="afterbegin"`
      does the work with no client JS.

### Statelessness checklist

The point of static SSR is that any pod can serve any request. Things to actually verify:

- [ ] No `@rendermode` anywhere, no `AddInteractiveServerComponents()`, no `blazor.web.js` —
      confirms there is no circuit and therefore no need for session affinity.
- [ ] Every page and fragment endpoint is a **GET**. No POST means no antiforgery token, which
      means no shared Data Protection key ring is required. If a POST is ever added, the key ring
      must move to Redis (`AddStackExchangeRedisDataProtection`) or every pod restart invalidates
      tokens — worth a comment in `Program.cs` so this isn't rediscovered the hard way.
- [ ] Each pod holds its own Redis pub/sub subscription, so SSE fan-out works correctly across
      replicas without coordination — every connected client sees every event regardless of which
      pod it landed on.
- [ ] `CachedSystemCount` caches in Redis (`cache:system:count`), not in process memory, so the
      60s cache stays coherent across pods. Already true today; keep it that way.

## Phase 4 — Containers and k8s, running side-by-side

- [ ] `Dockerfile.web` + `Dockerfile.ingestion` at repo root (both need solution-level build context).
- [ ] `build-image` / `push-image` / `.github/workflows/build-push-images.yml` build and push
      `trasa/elite-web` + `trasa/elite-ingestion`.
- [ ] k8s manifests: Deployment + Service for web (n replicas), Deployment for ingestion
      (`replicas: 1`, `strategy: Recreate` so a rollout never runs two writers), a Secret for the
      Redis password mounted at `REDIS_AUTH_FILE`, and probes wired to the health endpoints from
      Phases 2–3.
- [ ] Redis StatefulSet: single replica, headless Service, modest PVC. Set `maxmemory` plus
      `allkeys-lru` — every key here is already TTL'd and disposable, so eviction under pressure is
      the correct behaviour and beats the pod getting OOM-killed. Keep the password Secret even
      in-cluster so `AddEliteRedis()` follows an identical code path in both environments; the
      connection string drops `ssl=true` (in-cluster, unlike DO managed), which the
      `ConnectionStrings__Redis` env var handles with no code change.
- [ ] Ingress (replaces Caddy) + TLS, on a test hostname first.
- [ ] Bring the stack up via `doctl`, confirm ingestion fills the empty Redis and the UI reads it.

The droplets keep running untouched throughout this phase, on their own managed Redis.

## Phase 5 — Cut over and delete

- [ ] Point `elite.meancat.com` at the k8s ingress.
- [ ] Redirect `elite-visitors.meancat.com` → `elite.meancat.com`.
- [ ] Tear down the droplet stack (`docker-compose.yaml`, `Caddyfile`, `deploy-stack`,
      `.github/workflows/deploy.yml`) once the k8s stack has proven itself.
- [ ] Delete `EliteEvents.Visitors/` and `EliteEvents.Dashboard/`; update `EliteEvents.sln`.
- [ ] Rewrite `CLAUDE.md` for the new topology, including the corrections listed below.

## Phase 6 — optional

1. **Search index.** `GetMatchingSystems` runs a full `SCAN` with a `*glob*` pattern per search —
   O(keyspace) against a Redis holding every system seen in 30 days. Ingestion could maintain
   `index:systems` / `index:carriers` sorted sets for `ZRANGEBYLEX` prefix lookups. Prerequisite
   for htmx keystroke-triggered typeahead.
2. **Batch the writes.** Each `Docked` event is 6 sequential round-trips today; `IBatch` makes it one.
3. **First test project.** `EliteEvents.Eddn.Tests` over `RedisKeys` and
   `WeeklyExpirationCalculator` — pure logic, no Redis required. Extracting the storage layer is
   the natural moment, and the solution currently has zero tests.

---

## CLAUDE.md corrections to fold into Phase 5

Verified against the working tree on 2026-07-28:

| Documented | Actual |
|---|---|
| compose runs `visitors` + `caddy` | also runs `dashboard` (`trasa/elite-dashboard:latest`), built by hand via `build-image` — no CI job covers it |
| *(absent)* | `/health` endpoint plus `RedisHealthCheck` and `EddnStreamHealthCheck`. The commit message says `/api/health`; `Program.cs:84` maps `/health` |
| *(absent)* | `EventTickerService` publishes JSON to the Redis channel `eddn:events` — this is the contract the Next ticker consumes |
| *(absent)* | `EddnStreamReceiver` silence detection + reconnect, driven by `EddnOptions.ReconnectAfterSilence` |
| *(absent)* | `StreamHealthTracker` shares last-message time between the receiver and the health check |
| `EliteEvents.Journalweb` | directory is `EliteEvents.JournalWeb`, and it **does** reference `EliteEvents.Eddn` |

`EliteEvents.Dashboard/.claude/NOTES.md` is also stale — it still calls the Next app
"EliteEvents.Web" and lists the pub/sub publish as an unfinished TODO, though it shipped in
commit `5f5f73b`. That file goes away with the directory.
