# elite-events-operator

A Kubebuilder/controller-runtime operator that manages `kind: FeedListener` — an EDDN
subscription declared as a resource, reconciled into the workloads that service it.

## Why this is an operator and not a Deployment

Three properties of an EDDN subscription are things a Deployment cannot express:

1. **The consumers are not interchangeable.** EDDN is a broadcast firehose whose frames carry no
   topic, so every subscriber receives every message. Running N identical replicas would write
   every docking N times. `spec.consumers` is therefore a *shard count*: each pod is assigned a
   distinct index and handles only the slice of the feed that hashes to it. Keeping that
   partition exhaustive and non-overlapping across scale-up, scale-down and rollout is the
   controller's core invariant — and a Deployment, which by definition gives every replica an
   identical pod spec, has no way to state it.

2. **Deleting the resource leaves state behind.** `index:systems` and `index:carriers` carry no
   TTL on purpose — `ZRANGEBYLEX` is only defined when every member scores the same, so the index
   cannot be pruned by score, and a TTL on the key would drop the whole thing. They stay correct
   only while a listener reconciles them. Owner-reference garbage collection cannot help: the
   state is in Redis, outside the object graph. A finalizer stops the consumers, runs a drain
   Job, and only then releases the resource.

3. **Health is not readiness.** A listener whose pods are all `Ready` can be receiving nothing at
   all. `status.conditions` separates `Available` (pods are up) from `Streaming` (the
   subscription is actually delivering, on every shard).

## Layout

| Path | What |
|---|---|
| `api/v1alpha1/feedlistener_types.go` | The CRD: spec, status, conditions, finalizer name |
| `internal/controller/resources.go` | Pure builders for the ConfigMap, Service, and per-shard Deployment |
| `internal/controller/feedlistener_controller.go` | Reconcile: children, owner refs, shard pruning |
| `internal/controller/status.go` | Per-pod `/health/stream` polling → conditions |
| `internal/controller/finalizer.go` | Ordered teardown: stop consumers → drain Job → release |

Each shard is its own `Deployment` with `replicas: 1` and `strategy: Recreate`, not one
Deployment with N replicas. That is what lets each pod carry a distinct `Eddn__ShardIndex`, and
`Recreate` is what guarantees a rollout never runs two pods of the same shard at once — the
overlap would double-count exactly the way unfiltered replicas do.

## Contract with the application

The controller does not speak Redis. Key formats live in `RedisKeys` in `EliteEvents.Eddn` and
nowhere else, so the drain runs the **ingestion image itself** with `--purge-indexes` rather than
deleting keys by name from Go. What the two sides do agree on:

| | |
|---|---|
| `Eddn__ShardIndex` / `Eddn__ShardCount` | Per-shard env / shared ConfigMap → `EddnOptions` |
| `Eddn__ReconnectAfterSilence` | Rendered as `HH:MM:SS` — .NET's `TimeSpan` parser rejects Go's `2m0s` |
| `GET /health/stream` | JSON: `lastMessageUtc`, `messagesReceived`, `messagesHandled`, `shardIndex`, `shardCount` |
| `--purge-indexes` | One-shot teardown mode, run by the drain Job |

## Prerequisites

- Go **1.26** (`go.mod` requires it). The `Makefile` pins `GOTOOLCHAIN=go1.26.5` so a machine
  with an older ambient `go` still builds — without it, coverage instrumentation looks for
  `covdata` in the wrong `GOROOT`.
- `kubebuilder` v4.15+ on `PATH` for scaffolding (not needed to build or test).

## Commands

```bash
make test              # unit tests + envtest suite (downloads control-plane binaries)
make manifests generate # regenerate CRD, RBAC and deepcopy after editing types
make build             # build the manager binary
make docker-build IMG=registry.digitalocean.com/meancat/elite-operator:<tag>

# Install just the CRD (safe; creates no workloads)
make install

# Deploy the controller into elite-events-operator-system
make deploy IMG=registry.digitalocean.com/meancat/elite-operator:<tag>
```

`../build-image` and `../push-image` build and push `elite-operator` alongside the two .NET
images, tagged from the same nbgv version — the controller and the ingestion image it schedules
have to agree on the config keys and the `--purge-indexes` flag.

## Operating

```bash
kubectl get feedlisteners -n elite          # or: kubectl get feeds -n elite
kubectl describe feed eddn -n elite         # conditions explain Silent vs Pending
kubectl scale feed eddn -n elite --replicas=2   # scale subresource maps to spec.consumers
```

`Phase` is a summary; the conditions are the truth:

| Phase | Meaning |
|---|---|
| `Pending` | No consumer is Ready |
| `Progressing` | Some but not all consumers are Ready |
| `Streaming` | Every shard received a message within `reconnectAfterSilence` |
| `Silent` | Consumers are up but at least one shard is receiving nothing |
| `Degraded` | Reconciliation failed; see the `Degraded` condition message |
| `Terminating` | Finalizer is draining |

A shard reporting a healthy `messagesReceived` but zero `messagesHandled` is a partition problem,
not a feed problem.
