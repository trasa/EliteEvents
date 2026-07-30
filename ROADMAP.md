# Roadmap — merge Visitors + Dashboard into one ASP.NET/htmx app

Status: **Complete.** `elite.meancat.com` serves from Kubernetes; the droplet and the managed
Valkey were destroyed on 2026-07-30, so there is no longer a rollback path to the old stack. All
three optional Phase 6 items are done as well.

> **Do not deploy the droplet stack from this branch.** `EliteEvents.Visitors` no longer ingests —
> `Dockerfile` / `build-image` / `docker-compose.yaml` still build and run only that project, so
> pushing the image from here would leave production with a web tier and no writer. Containers are
> rebuilt for k8s in Phase 4; nothing in this branch is deployable until then.

Local ports: web `5240`, ingestion `5239`, the retired Visitors app `5238`.

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
EliteEvents.Eddn        (class library)   [Phases 1-2 done]
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
| Registry | **DigitalOcean Container Registry**, Basic tier, replacing the public Docker Hub repos |
| k8s hostname | `k8s.meancat.com` while both stacks run; `elite.meancat.com` moves to it at cutover |
| Data | **Data loss is acceptable.** The k8s stack starts on an empty Redis and refills from the EDDN firehose. No migration, no export/import. |
| Redis in k8s | **In-cluster StatefulSet**, single replica — cheaper than a second managed instance, and the data is disposable and TTL'd |
| Ingress / Caddy | Out of scope here — handled in the k8s step (Phase 4) |

### Why side-by-side is safe

Each stack owns its own Redis: the droplets keep their DigitalOcean managed Redis, the k8s stack
gets its own. Both ingest from EDDN at the same time and write to **different** stores, so there is
no double-counting and no shared-writer hazard. EDDN is a broadcast firehose — a second subscriber
costs it nothing. Cutover is a DNS/ingress change, not a data operation.

## Open questions

- [x] Phase 6 items — which, if any, are in scope. **All three, done 2026-07-30.**

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

- [x] **An unreachable Redis kills the whole host.** A `RedisConnectionException` from a docking
      write propagates out of `EddnStreamReceiver.ExecuteAsync`, and the default
      `BackgroundServiceExceptionBehavior.StopHost` stops the app. Today `restart: unless-stopped`
      masks it; under k8s this is a crash-loop on any Redis blip. **Fixed in Phase 2** — caught and
      logged per-message, and readiness rather than process death reports the outage.
- [x] **`/` and `/system-search` return 500 when Redis is down**, because both call
      `CachedSystemCount` in `OnInitializedAsync` with no try/catch, unlike every other page.
      **Fixed in Phase 3** when these pages were rewritten — both now catch, log, and render a
      generic line.

## Phase 2 — `EliteEvents.Ingestion`

ASP.NET minimal host rather than a bare Worker, because k8s probes want HTTP. Exposes **only**
`/health/live` and `/health/ready`. No UI, no static files.

- [x] Move in `EddnStreamReceiver`, `JournalMessageHandler`, `StreamHealthTracker`,
      `EddnStreamHealthCheck`, and the handler DI registration.
- [x] Receiver writes a throttled (~5s) **`heartbeat:eddn`** key holding the last-message
      timestamp. `EddnStreamHealthCheck` reads an in-process field today; once ingestion and web
      are separate processes that liveness signal has to travel through Redis for the web tier and
      any uptime monitor to see it.
- [x] Liveness = process responsive; readiness = Redis reachable **and** the EDDN stream not
      silent past `EddnOptions.ReconnectAfterSilence`. Keep liveness lenient so k8s does not
      restart a pod that is merely waiting out a quiet EDDN period.

### Phase 2 outcome

`EliteEvents.Ingestion` owns the firehose; `EliteEvents.Visitors` is now read-only and keeps
serving the UI off the same Redis. Both were run together against local Redis: ingestion writes
dockings and publishes `eddn:events`, and every Visitors route still returns 200.

Departures from the plan as written, and why:

- **`EddnStreamHealthCheck` landed in `EliteEvents.Eddn/Storage/`, not in Ingestion.** Once the
  signal it reads is a Redis key, it is shared code — the web tier's `/health` uses the identical
  check. `IStreamHeartbeatWriter` / `IStreamHeartbeatReader` (`RedisStreamHeartbeat`) live beside
  it and are registered by `AddEliteRedis()` itself rather than by the reader or writer bundle:
  ingestion writes the heartbeat *and* reads its own back, which makes readiness cover the whole
  round trip.
- **The redundant `IJournalMessageHandler` DI registration was dropped.** `MessageHandlerProvider`
  only ever resolves `IMessageHandler<JournalMessage, MessageEvent>`; registering the marker
  interface as well just built a second, unused handler instance.
- **Heartbeat writes swallow their own failures** and leave the throttle advanced, so an
  unreachable Redis costs one warning per interval instead of one per message. Repeated
  processing failures in the receiver are likewise logged once at the start of an outage and then
  at most every 30s, with a recovery line when messages start landing again.

Verified: `/health/live` stays 200 while `/health/ready` reports 503 for both a missing heartbeat
and an unreachable Redis; with Redis pointed at a dead port the ingestion host keeps running and
logs `Failed to process an EDDN message` instead of stopping; deleting `heartbeat:eddn` turns the
Visitors `/health` unhealthy and it recovers on the next heartbeat write from the other process —
which is the cross-process signal working.

**Carry into Phase 3:** the web tier must keep `eddn-stream` out of whatever check set its
*readiness* probe uses (report it on `/health` for the uptime monitor only). A silent EDDN period
is not a reason to pull every web pod out of the service — those pods serve 30-day-TTL data just
fine.

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

- [x] **SSE ticker:** one shared `ISubscriber` subscription per pod, fanned out to that pod's
      connected clients via a per-client `Channel<T>` — fixes the per-client-connection design the
      Next app flagged as a TODO in its own README. The server renders the `<li>` fragment and
      pushes HTML (the htmx-native approach), so `sse-swap="message"` + `hx-swap="afterbegin"`
      does the work with no client JS.

### Statelessness checklist

The point of static SSR is that any pod can serve any request. Things to actually verify:

- [x] No `@rendermode` anywhere, no `AddInteractiveServerComponents()`, no `blazor.web.js` —
      confirms there is no circuit and therefore no need for session affinity.
- [x] Every page and fragment endpoint is a **GET**. No POST means no antiforgery token, which
      means no shared Data Protection key ring is required. If a POST is ever added, the key ring
      must move to Redis (`AddStackExchangeRedisDataProtection`) or every pod restart invalidates
      tokens — worth a comment in `Program.cs` so this isn't rediscovered the hard way.
- [x] Each pod holds its own Redis pub/sub subscription, so SSE fan-out works correctly across
      replicas without coordination — every connected client sees every event regardless of which
      pod it landed on.
- [x] `CachedSystemCount` caches in Redis (`cache:system:count`), not in process memory, so the
      60s cache stays coherent across pods. Already true today; keep it that way.

### Phase 3 outcome

`EliteEvents.Web` serves every route in the table above at `localhost:5240`, reading the same
Redis `EliteEvents.Ingestion` writes. Verified in a browser as well as by curl: the ticker
prepends server-rendered rows as EDDN events arrive and stays capped at 40, the leaderboard panel
swaps itself every 15s, and a search submits over `hx-get`, swaps the results card, and updates
the address bar without a page load.

Decisions taken while building it:

- **`/api/stream` carries both formats on one connection.** The unnamed `message` event still
  carries the exact JSON the Next ticker consumed, so that contract is untouched; a named `ticker`
  event carries the rendered `<li>`. The roadmap's `sse-swap="message"` became `sse-swap="ticker"`
  — one attribute, and it saves running two endpoints and two subscriptions for one feed.
- **The ticker is seeded from a 40-event per-pod ring buffer**, so a fresh page load shows recent
  activity instead of an empty box. It is a cache, not session state: two pods may seed slightly
  different rows and neither needs the other.
- **~40 lines of JS after all** (`wwwroot/js/ticker.js`): htmx does every swap, but something has
  to cap the list — a tab left open overnight would otherwise accumulate hundreds of thousands of
  `<li>`s — and the same file drives the connected/offline dot.
- **Redis errors no longer reach the page.** The old Blazor pages rendered `ex.Message` straight
  into the HTML, and a StackExchange.Redis exception message carries the endpoint host, client
  name and library version. Pages now log the exception and show one generic line.
- **`HtmlRenderer` needs a scope**, so `TickerFragmentRenderer` holds one `AsyncServiceScope` for
  the app's lifetime rather than building a renderer per event.
- **No `UseHttpsRedirection`.** TLS terminates at the proxy and the container only speaks HTTP;
  HSTS is still emitted in production.
- `/health/live` and `/health/ready` exist here too, so Phase 4 can wire the same probe shape to
  both deployments. Readiness is Redis-only, per the Phase 2 note above.

Behaviour differences worth knowing:

- `/api/most-visited` inherits `GetSystemVisitsAsync`'s "score > 1" filter, which the Next query
  did not have. Systems visited exactly once are omitted from the JSON as well as the UI.
- Detail pages moved to `/system/{name}` and `/carrier/{id}`; the old paths 301 to them.

## Phase 4 — Containers and k8s, running side-by-side

Registry is **DigitalOcean Container Registry**, not Docker Hub — one fewer external account, and
DOKS mounts the pull secret itself.

- [x] `Dockerfile.web` + `Dockerfile.ingestion` at repo root (both need solution-level build context).
- [x] `build-image` / `push-image` / `.github/workflows/build-push-images.yml` build and push
      `registry.digitalocean.com/meancat/elite-web` + `.../elite-ingestion`.
- [x] k8s manifests: Deployment + Service for web (n replicas), Deployment for ingestion
      (`replicas: 1`, `strategy: Recreate` so a rollout never runs two writers), a Secret for the
      Redis password mounted at `REDIS_AUTH_FILE`, and probes wired to the health endpoints from
      Phases 2–3.
- [x] Redis StatefulSet: single replica, headless Service, modest PVC. Set `maxmemory` plus
      `allkeys-lru` — every key here is already TTL'd and disposable, so eviction under pressure is
      the correct behaviour and beats the pod getting OOM-killed. Keep the password Secret even
      in-cluster so `AddEliteRedis()` follows an identical code path in both environments; the
      connection string drops `ssl=true` (in-cluster, unlike DO managed), which the
      `ConnectionStrings__Redis` env var handles with no code change.
- [x] Ingress (replaces Caddy) + TLS, on a test hostname first — `k8s.meancat.com`, ingress-nginx
      plus cert-manager.
- [x] **Bring the stack up via `doctl`** — done 2026-07-30. Live at `https://k8s.meancat.com`.

The droplets keep running untouched throughout this phase, on their own managed Redis.

### Phase 4 outcome

Everything except the provisioning itself is written and verified. Both images build from a
**clean** context and the whole stack was deployed to a local Kubernetes cluster (Docker Desktop,
with the DO-specific bits patched out) where it came up Ready in 20s: ingestion filled an empty
in-cluster Redis from the live firehose, both web replicas passed readiness, `/api/*`, `/health*`
and the SSE ticker all served correctly through the Service, and `heartbeat:eddn` round-tripped
between the two pods. The namespace was deleted afterwards.

Two latent bugs surfaced while making the images build, both pre-existing:

- **CI could never have built an image.** `EliteEvents.Eddn/Generated/*.g.cs` are gitignored and
  the NSwag target only ran in Debug, so a Release build outside a developer's working tree had no
  message types to compile. It worked locally only because `docker build` copied the generated
  files off the host. The target now also fires when the output is missing, and — because MSBuild
  expands the `**/*.cs` glob at project load — re-adds the freshly generated files to `@(Compile)`
  so one pass generates *and* compiles them. `dotnet tool restore` moved into both Dockerfiles.
- **`appsettings.LocalUser.json` was being baked into the production image.** There was no
  `.dockerignore` at all, so every build shipped the whole working tree including the local Redis
  password. Now excluded, along with `bin`/`obj`/`.git`/the generated clients.

Decisions:

- **DOCR Basic ($5/mo)** over the free Starter tier, which allows one repository and 500 MB — two
  images need two repos.
- **2 × `s-1vcpu-2gb`** nodes: the cheapest shape where web's two replicas can actually land on
  different nodes, which is the whole point of the stateless split.
- **Redis password via an init container.** It appends `requirepass` to the rendered config in a
  memory-backed `emptyDir`, so the secret is in neither the ConfigMap nor `ps` output. Probes use
  `REDISCLI_AUTH` so it stays off the probe command lines too.
- **`cluster-issuer.yaml` is outside `kustomization.yaml`.** Kustomize stamps `namespace: elite`
  onto ClusterIssuers because it can't know a CRD is cluster-scoped; they belong with the
  cert-manager install anyway.
- **`deploy-k8s` applies the manifests then pins the tag** with `kubectl set image`, so committed
  YAML never carries a version that is stale the moment it is written.
- The old root `Dockerfile` and the droplet `build-image` targets still work — `.dockerignore`
  deliberately keeps `EliteEvents.Visitors` in the context until Phase 5 retires that path.

### Provisioned 2026-07-30

Live at **https://k8s.meancat.com**, everything in **sfo3** alongside the existing droplet and
managed Valkey.

| Resource | Identifier |
|---|---|
| Registry | `meancat` (Basic, sfo3) — account-level, cannot be assigned to a project |
| Cluster | `elite`, `f9d0a98f-91cb-42ee-9bc4-4814325bec84`, 1.36.0-do.3, 2 × `s-1vcpu-2gb`, **HA control plane false** |
| Load balancer | `elite-k8s-lb`, `2d76117d-…`, 164.90.244.224 |
| DNS | `elite`, `elite-visitors` and `k8s` all A → 164.90.244.224, TTL 300 (see Phase 5) |
| Project | everything except the registry assigned to `elite-dangerous` (`2ca85a53-…`) |

Verified end to end: all pages and JSON APIs 200 over valid TLS, SSE streaming live EDDN frames
through nginx, in-cluster Redis filling from the firehose, ticker and 15s leaderboard poll both
working in a real browser.

Two things provisioning taught us that the manifests were wrong about:

- **The DOCR pull secret is named after the registry** — `meancat`, not `registry-meancat`. DOKS
  syncs it into every namespace, including ones created later, and attaches it to each default
  ServiceAccount. The wrong name would have produced `ImagePullBackOff` with an authentication
  error rather than anything pointing at the real cause.
- **`--ha=false` had to be explicit.** Kubernetes 1.36+ defaults the control plane to HA, which is
  $40/mo — more than the rest of the stack combined.

And one reporting quirk worth remembering: `doctl projects resources list` does **not** show
DOKS-managed load balancers, so an assigned LB appears in no project at all. Check `project_id` on
the load balancer itself.

Certificates were issued against `letsencrypt-staging` first, confirmed, then reissued from
`letsencrypt-prod` by deleting the `elite-tls` secret — the annotation in `40-ingress.yaml` is now
prod.

## Phase 5 — Cut over and delete

- [x] Point `elite.meancat.com` at the k8s ingress.
- [x] Redirect `elite-visitors.meancat.com` → `elite.meancat.com` (plus `www.`), 301 over its own
      certificate. **Path is not preserved** — see below.
- [x] Tear down the droplet stack (`docker-compose.yaml`, `Caddyfile`, `deploy-stack`,
      `.github/workflows/deploy.yml`, the root `Dockerfile`) once the k8s stack has proven itself.
- [x] **Destroy the managed Valkey** — done 2026-07-30, after the droplet. `elite-visitors-redis`,
      `db-s-1vcpu-1gb`, sfo3, `160d042a-c022-434d-8c90-3932d7e1157c`, $15/mo. Its only clients were
      the droplet containers, so it went *after* the droplet, not before. In-cluster Redis replaces
      it outright: the data is 30-day TTL'd and disposable, so there was nothing to migrate and a
      managed database was always more durability than this needs.
- [x] Delete `EliteEvents.Visitors/` and `EliteEvents.Dashboard/`; update `EliteEvents.sln`.
- [x] Rewrite `CLAUDE.md` for the new topology, including the corrections listed below. `README.md`
      rewritten too — it still described two front ends, and called the *Next* app
      "EliteEvents.Web", which is now a different project entirely.

### Cutover outcome — 2026-07-30

`elite.meancat.com` serves from Kubernetes; `elite-visitors.meancat.com` and its `www.` alias 301
to it. All three hostnames plus `k8s.meancat.com` share the load balancer.

What went wrong on the way, worth remembering:

- **The cutover caused a short outage of `elite.meancat.com`.** Adding a hostname to an existing
  Ingress makes cert-manager re-issue, and until the new certificate lands nginx serves its
  self-signed default for that name — TLS simply fails. Then cert-manager wedged: Let's Encrypt
  had *issued* the certificate and the Order held it, but repeated optimistic-locking conflicts
  meant the status was never written and the secret never updated. `kubectl delete
  certificaterequest elite-tls-3` unstuck it and the secret populated in under 10 seconds.
  Next time: create the new hostname's certificate *before* moving DNS.
- **`kubectl apply -f k8s/45-...` put the Ingress in the wrong namespace.** It is kustomize that
  injects `namespace: elite`; a bare `-f` on one file lands it in whatever context is current.
  nginx still served it (it watches all namespaces), so the redirect worked while its certificate
  quietly failed. Always `apply -k`.
- **ingress-nginx rejects `$request_uri` in an annotation.** The admission webhook validates
  annotation values as literal URLs, so `permanent-redirect` can only send everything to the site
  root. Preserving the path would mean doing the redirect in the app; judged not worth it.
- **A stale `<Compile Remove>` path was adding CS2002 warnings.** The Phase 4 NSwag fix used
  `$(ProjectDir)`-absolute paths while the default glob yields relative ones, so the Remove matched
  nothing and the Include duplicated every generated file. Relative paths now.
- **`kubectl apply -k` silently un-pins the image.** `kustomization.yaml` carries `:latest` so a
  fresh checkout applies cleanly, which means any apply — including an ingress-only change —
  resets both Deployments to `:latest` and rolls the pods, discarding whatever `deploy-k8s`
  pinned. It caused no harm here (both tags were the same digest) but it defeats immutable tags.
  `./deploy-k8s <tag>` is now the documented path for config changes too.

  **This was not a theoretical hazard.** On 2026-07-30, while diagnosing the StatefulSet noise
  below, a handful of bare `kubectl apply -k k8s/` calls rolled production off the pinned tag and
  back onto `:latest` — exactly as documented, and still surprising in the moment.

  **Fixed the same day.** `deploy-k8s` now writes the tag into `kustomization.yaml`'s `images:`
  block and then applies, so `apply` is the entire deploy and `kubectl set image` is gone along
  with the second field manager it created. The committed tags are the deployed tags, which makes
  a bare `kubectl apply -k k8s/` from a clean checkout a genuine no-op, and makes the running
  version visible in git. Redeploying the same tag now rolls nothing at all — the old script
  always rolled, because it always re-pinned away from `:latest`.

  The trade is that `kustomization.yaml` must be committed after a deploy, and that its `images:`
  block is now a deploy instruction rather than an ignorable default. `deploy-k8s` prints the
  commit command when it leaves the file dirty, and refuses to apply at all if the rendered
  manifests don't come out pinned to the requested tag on both images — a typo in that block would
  otherwise deploy something nobody asked for.

- **`kubectl apply` reported `statefulset.apps/redis configured` on every run** against an object
  that had never changed — generation stayed at 1 from creation. Fixed 2026-07-30 by spelling out
  the four fields the API server defaults inside `volumeClaimTemplates` (`apiVersion`, `kind`,
  `spec.volumeMode`, `status.phase`).

  `volumeClaimTemplates` is an *atomic* list in a strategic-merge patch, so kubectl replaces the
  whole list unless the manifest deep-equals the stored copy; the defaults meant it never did, and
  kubectl re-sent the list forever. Two things made this slow to pin down: `kubectl diff` shows
  **nothing** for the object (it normalises the defaults away), so only the `-v=8` PATCH body
  reveals the cause; and `--dry-run=server` can never show the fixed state, because after any
  manifest edit the first apply is legitimately "configured" — it has to rewrite
  `last-applied-configuration`. The settled state only appears on a *second real* apply.

  Server-side apply also fixes it, and was rejected: it prints `serverside-applied` for every
  object unconditionally, which removes the changed/unchanged signal this was trying to recover,
  and it conflicts with the `kubectl set image` in `deploy-k8s` over ownership of the image field.

### Decommissioned — 2026-07-30

The droplet (`elite-visitor-web`, 164.92.109.111) and the managed Valkey
(`160d042a-c022-434d-8c90-3932d7e1157c`) were kept up for a day as a rollback path — pointing the
A records back at the droplet would have been a complete revert — then deleted once the k8s stack
had proven itself. Verified after deletion: `doctl compute droplet list` and `doctl databases list`
show neither, no DNS record still points at 164.92.109.111, and `elite` / `elite-visitors` both
resolve to the load balancer with every health endpoint returning 200.

**There is no longer a path back to the droplet stack.** Rolling back now means rolling forward — a
previous image tag via `./deploy-k8s <tag>`.

Remaining DigitalOcean footprint: the `elite` cluster (2 × `s-1vcpu-2gb`), `elite-k8s-lb`, and the
`meancat` registry — roughly $42/mo, down from ~$70.

### Loose ends left by the cutover — closed 2026-07-30

- [x] **Retired `k8s.meancat.com`.** It was the side-by-side test hostname, and its stated reason to
      exist — reaching the cluster while the public name still pointed at the droplet — died with
      the droplet. Removed from `40-ingress.yaml` (host rule and TLS SAN) and the A record deleted.
      `elite.meancat.com` is now the only public name.
- [x] **DNS TTLs back to 3600** on `elite` and `elite-visitors`. 300 was for a fast revert that no
      longer exists, and 3600 matches every other record in the zone.

**Removing a hostname does not repeat the cutover outage.** Adding one broke TLS because the new
name was not on the existing certificate; removing one leaves `elite-tls` serving the old,
still-valid certificate until cert-manager writes its replacement. `elite-tls-4` went Ready in 17
seconds, no pod restarted, and the site never stopped answering 200.

## Phase 6 — optional

1. ~~**Search index.**~~ **Done 2026-07-30.** `index:systems` / `index:carriers` sorted sets,
   written inline by `DockingWriter` and reconciled hourly by `SearchIndexMaintainer`.

   Search does a `ZRANGEBYLEX` prefix lookup, then falls back to a `ZSCAN` `*query*` MATCH **only
   if the prefix pass didn't fill the requested `limit`** — so the `limit` parameter, not a
   separate method, is what gives typeahead its fast path. Searches were previously unbounded;
   the default is now 200.

   Two constraints shaped the rest of it:

   - **`ZRANGEBYLEX` requires every member to share a score**, so the index is stored at score 0
     and therefore cannot also carry a last-seen timestamp to be pruned by. A TTL on the key would
     drop the entire index rather than age out members.
   - So the data keys and their existing 30-day TTL stay the source of truth, and
     `SearchIndexMaintainer` reconciles against them rather than restating the expiry rule
     somewhere it could drift. One rebuild covers stale entries, backfill, *and* recovery from
     `allkeys-lru` evicting the index key — a real possibility for a single large key. It
     snapshots the index before scanning, so a name the writer adds mid-scan cannot be mistaken
     for stale and deleted.

   `CachedSystemCount` fell out of this for free: it was a full-keyspace SCAN, now a `ZCARD`.

   Behaviour changes: results are prefix matches first, then substring, where the old SCAN
   returned one flat alphabetical list; and **station names are no longer searchable as systems**.
   That quirk was pinned by a test in item 3, but it was already near-dead in production — station
   names are stored verbatim while queries are uppercased, so the glob only ever matched stations
   that happened to be all-caps. Glob metacharacters in the query are now escaped, closing a hole
   where a query of `*` turned a lookup into a full scan.

   New risk to know about: the web tier now depends on a key only Ingestion maintains. On the
   deploy that introduces it, search returns nothing and the count reads 0 until the first
   rebuild, which is why that first pass retries every 15s instead of waiting out the hour.

2. ~~**Batch the writes.**~~ **Done 2026-07-30.** Every `IDockingWriter` method dispatches one
   `IBatch`; a station docking went from 6 sequential round-trips to 1 (and the two `HSET`s
   collapsed into one). A batch is pipelined, not transactional — safe here because ingestion is a
   single writer and every operation is a commutative increment or an idempotent set, so nothing
   reads a value before writing it.

### Measured, 20,000 systems / 80,001 keys, local Redis

| Operation | Before | After | |
|---|---|---|---|
| Search, 2-char prefix, `limit: 10` (typeahead) | 74.01 ms | **0.18 ms** | 422x |
| Search, long prefix, default limit (hits the fallback) | 73.52 ms | **15.48 ms** | 5x |
| Search, substring (fallback only) | 73.49 ms | **14.94 ms** | 5x |
| System count, cold cache | 74.28 ms | **0.61 ms** | 122x |
| 1,000 station dockings, sequential | 856 ms (1,167/s) | **151 ms (6,637/s)** | 5.7x |

The two 5x rows are the fallback path: it is still O(index), but the index holds one member per
system instead of one key per station, and matching happens server-side so only hits cross the
wire. Ask for a page small enough for the prefix pass to fill and the fallback never runs.


3. ~~**First test project.**~~ **Done 2026-07-30.** `EliteEvents.Eddn.Tests` (xUnit, 49 tests)
   covers `RedisKeys` and `WeeklyExpirationCalculator` — pure logic, no Redis required.

   `RedisKeys` is tested as a wire format: key literals are asserted verbatim rather than rebuilt
   from the same interpolation the production code uses, because the two containers agree on
   nothing else and a drifted key fails silently rather than loudly. A small glob matcher lets the
   tests assert what a `SCAN` would and would not return without a Redis — which is how
   `AllSystemStationsPattern` is pinned to match one key per *system* and not per station, the
   difference between a correct system count and one inflated by the station count. Documented
   quirks (substring search reaching the station segment, `ExtractName` returning `visits` for
   `systems:visits`) are pinned as tests so they stay decisions.

   `WeeklyExpirationCalculator` is checked against explicit dates on both sides of the 07:30
   boundary, across month and year rollovers and both DST transitions, plus a year-long walk
   asserting every result is a future Thursday 07:30 UTC no more than a week out. Also pinned:
   Cronos excludes the starting instant, so a write landing exactly at 07:30 gets a full week
   rather than a zero TTL that would delete the key being built.

   Caveat: `dotnet test` builds `EliteEvents.Eddn` in Debug and therefore runs the NSwag target,
   so it needs eddn.edcd.io like any other Debug build. The tests themselves touch nothing.

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
