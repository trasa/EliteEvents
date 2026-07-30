# Running the stack on DigitalOcean Kubernetes

Everything here is a one-time provisioning runbook plus the manifests the deploy scripts apply.
Nothing in this directory creates cloud resources on its own.

Target: `k8s.meancat.com`, running side by side with the droplet stack on its own Redis. The two
never share data, so both can ingest from EDDN at the same time without double-counting.

## What it costs

| Resource | Monthly |
|---|---|
| DOKS control plane (non-HA) | free |
| 2 × `s-1vcpu-2gb` nodes | $24 |
| Load balancer (ingress-nginx) | $12 |
| Container registry, Basic tier | $5 |
| 5 GiB block storage for Redis | ~$0.50 |
| **Total** | **~$42** |

The droplet stack keeps running alongside until Phase 5, so expect to pay for both during cutover.

> **`--ha=false` is not optional.** On Kubernetes 1.36+ `doctl` defaults the control plane to
> highly-available, which adds **$40/mo** — more than everything above combined. A single control
> plane failing stops you changing the cluster; it does not stop the pods serving.

Everything goes in **sfo3**, where the existing droplet and managed Valkey already live.

## Environment

```bash
PROJECT=2ca85a53-3472-4ba6-8ccf-d756be2281f2   # the elite-dangerous DO project
REGION=sfo3
CLUSTER=elite
REGISTRY=meancat
```

Resources are assigned to the `elite-dangerous` project for budget tracking. The container
registry is the one exception: DOCR is account-level, has no project URN, and always bills outside
any project.

## One-time provisioning

```bash
doctl auth init                       # tokens expire; re-run when API calls start returning 401

# 1. Container registry. Basic tier: 5 repositories, 5 GB. The name is globally unique across
#    DigitalOcean and appears in every image path, so it is effectively permanent.
doctl registry create "$REGISTRY" --subscription-tier basic --region "$REGION"

# 2. Cluster. Roughly five minutes.
doctl kubernetes cluster create "$CLUSTER" \
  --region "$REGION" \
  --version latest \
  --ha=false \
  --node-pool "name=pool-1;size=s-1vcpu-2gb;count=2;auto-scale=false" \
  --wait

# kubeconfig is merged and made the current context by the create; to fetch it again later:
doctl kubernetes cluster kubeconfig save "$CLUSTER"

# 3. Put the cluster in the project. There is no --project-id on cluster create.
CLUSTER_ID=$(doctl kubernetes cluster get "$CLUSTER" --format ID --no-header)
doctl projects resources assign "$PROJECT" --resource="do:kubernetes:$CLUSTER_ID"

# 4. Let the cluster pull from the registry. This creates a dockerconfigjson secret named after
#    the registry — `meancat`, not `registry-meancat` — syncs it into every namespace including
#    ones created later, and adds it to each default ServiceAccount. The Deployments name it
#    explicitly rather than relying on the ServiceAccount.
doctl kubernetes cluster registry add "$CLUSTER"

# 5. Ingress controller. Provisions the DO load balancer — naming it here makes it findable below.
helm repo add ingress-nginx https://kubernetes.github.io/ingress-nginx
helm repo update
helm install ingress-nginx ingress-nginx/ingress-nginx \
  --namespace ingress-nginx --create-namespace \
  --set controller.publishService.enabled=true \
  --set controller.service.annotations."service\.beta\.kubernetes\.io/do-loadbalancer-name"=elite-k8s-lb

# Wait for DigitalOcean to finish provisioning it (a few minutes), then put it in the project too.
kubectl -n ingress-nginx get svc ingress-nginx-controller -w
LB_ID=$(doctl compute load-balancer list --format ID,Name --no-header | awk '$2=="elite-k8s-lb"{print $1}')
doctl projects resources assign "$PROJECT" --resource="do:loadbalancer:$LB_ID"

# Verify from the load balancer, not the project: `doctl projects resources list` does not report
# DOKS-managed load balancers, so an assigned LB still shows up nowhere in that listing.
doctl compute load-balancer get "$LB_ID" -o json | grep project_id

# 6. cert-manager for Let's Encrypt.
helm repo add jetstack https://charts.jetstack.io
helm repo update
helm install cert-manager jetstack/cert-manager \
  --namespace cert-manager --create-namespace \
  --set crds.enabled=true

# 7. Let's Encrypt issuers. Cluster-scoped, so they are applied here rather than being part of
#    the app's kustomization.
kubectl apply -f k8s/cluster-issuer.yaml
```

### DNS

meancat.com is hosted at DigitalOcean, so the record can be created from the CLI:

```bash
LB_IP=$(kubectl -n ingress-nginx get svc ingress-nginx-controller \
  -o jsonpath='{.status.loadBalancer.ingress[0].ip}')

doctl compute domain records create meancat.com \
  --record-type A --record-name k8s --record-data "$LB_IP" --record-ttl 300
```

cert-manager cannot issue until that resolves — the HTTP-01 challenge is served through this same
ingress.

### Redis password

Never committed, created once:

```bash
kubectl create namespace elite
kubectl -n elite create secret generic redis-auth \
  --from-literal=password="$(openssl rand -base64 32)"
```

Both apps read it as a *file* (`REDIS_AUTH_FILE=/etc/redis-auth/password`) rather than an env var —
the same code path the droplet stack uses with a Docker secret, so `AddEliteRedis()` is identical
in both environments. Redis itself gets it via an init container that appends `requirepass` to the
rendered config, keeping the password out of the ConfigMap and out of `ps`.

## Deploying

```bash
doctl registry login
./build-image            # both images, linux/amd64, tagged from nbgv
./push-image
./deploy-k8s "$(cat .image-version)"

kubectl -n elite get pods -o wide

# The Redis PVC creates a DO block-storage volume; add it to the project as well.
VOL_ID=$(doctl compute volume list --format ID,Name --no-header | awk '/pvc-/{print $1}')
doctl projects resources assign "$PROJECT" --resource="do:volume:$VOL_ID"
```

`deploy-k8s` rewrites the `newTag` values in `kustomization.yaml` and then applies. **Commit that
file after a deploy** — it is how the repo records the running version.

### Changing config, not code

A bare `kubectl apply -k k8s/` is fine. Because the committed tags are the deployed tags, applying
a clean checkout is a no-op for the Deployments and touches only what you actually edited:

```bash
kubectl apply -k k8s/
```

This was not always true. The tags used to be committed as `:latest`, with the real version pinned
after the fact by `kubectl set image`, so *any* bare apply reset both Deployments to `:latest` and
rolled production off whatever was pinned — which is exactly what happened on 2026-07-30. The tag
now goes into the manifests before they are applied, so `apply` is the entire deploy and there is
no second field manager fighting over the image.

Two consequences worth internalising:

- **Editing `kustomization.yaml`'s `images:` block is a deploy instruction, not a default.** It is
  no longer a placeholder you can ignore.
- **Re-running `./deploy-k8s <same tag>` rolls nothing.** Every object reports `unchanged` and the
  pods are left alone. The old script always rolled, because it always re-pinned away from
  `:latest`.

If an apply reports something as `configured` that you did not edit, note that the *first* apply
after any manifest change is legitimately `configured` — it has to rewrite
`last-applied-configuration`. Apply a second time; if it still says `configured`, that is real.
Use `kubectl diff -k k8s/` to see what, bearing in mind it normalises server-defaulted fields away
(see the `volumeClaimTemplates` comment in `10-redis.yaml`).

### First certificate

Issue against staging before production — Let's Encrypt's rate limits punish a misconfigured
HTTP-01 loop, and a staging failure costs nothing:

```bash
# in k8s/40-ingress.yaml: cert-manager.io/cluster-issuer: letsencrypt-staging
kubectl apply -k k8s/
kubectl -n elite describe certificate elite-tls     # watch for "Certificate issued successfully"

# then switch the annotation back to letsencrypt-prod and force a fresh issuance
kubectl -n elite delete secret elite-tls
kubectl apply -k k8s/
kubectl -n elite get certificate -w
```

Or run the **Build and Push Images** workflow followed by **Deploy to k8s.meancat.com**, which
needs:

- secret `DIGITALOCEAN_ACCESS_TOKEN` — a DO API token with read/write
- variable `DOKS_CLUSTER_NAME` — `elite`

## Layout

| File | What |
|---|---|
| `00-namespace.yaml` | `elite` |
| `10-redis.yaml` | ConfigMap, headless + ClusterIP Services, single-replica StatefulSet on a 5 GiB PVC |
| `20-ingestion.yaml` | the old hand-written EDDN writer — **not applied**; kept as the rollback target |
| `25-feedlistener.yaml` | the EDDN writer in production, as a `FeedListener` the operator reconciles |
| `30-web.yaml` | web Deployment (2 replicas) + Service |
| `40-ingress.yaml` | ingress-nginx rules, TLS, SSE-friendly proxy settings |
| `cluster-issuer.yaml` | Let's Encrypt staging + prod issuers — cluster-scoped, applied at provisioning time, not part of `kustomization.yaml` |

## Ingestion runs on the FeedListener operator

Since **2026-07-30**, ingestion is declared by `k8s/25-feedlistener.yaml` and reconciled by the
controller in `operator/`, which runs in its own namespace (`elite-events-operator-system`).
`20-ingestion.yaml` still exists but is **not** in `kustomization.yaml` — it is the rollback path
and nothing else. Never list both: two EDDN subscribers on one Redis **double-count every
docking**, silently.

### Upgrading

The ingestion tag is in `25-feedlistener.yaml`, not `kustomization.yaml`. `./deploy-k8s <tag>`
rewrites the `images:` block, which the FeedListener does not read — so it now only moves the web
tier.

```bash
./build-image && ./push-image           # writes .image-version
./deploy-k8s "$(cat .image-version)"    # web tier

# Ingestion: edit spec.image, then apply. The controller rolls the shard Deployments.
$EDITOR k8s/25-feedlistener.yaml
kubectl apply -k k8s/
kubectl -n elite get feed eddn          # PHASE should return to Streaming

# The operator itself, if it changed. `make deploy` records the tag in
# operator/config/manager/kustomization.yaml — commit that, it is the deployed version.
cd operator && make deploy IMG=registry.digitalocean.com/meancat/elite-operator:<tag>
```

Before pointing the FeedListener at a new ingestion image, check the image actually serves
`/health/stream` and understands `--purge-indexes`. An image predating the operator will sit at
`Silent` forever, and a later delete would start a drain Job that ignores the flag, boots a full
ingestion service, never exits, and hangs the finalizer.

### Rolling back to the hand-written Deployment

Patch **before** deleting, or the finalizer purges the search indexes and search returns nothing
until the next hourly `SearchIndexMaintainer` pass:

```bash
kubectl -n elite patch feed eddn --type=merge -p '{"spec":{"retainIndexesOnDelete":true}}'
kubectl -n elite delete feed eddn       # returns once the finalizer releases
kubectl apply -f k8s/20-ingestion.yaml -n elite
```

Then put `20-ingestion.yaml` back in `kustomization.yaml` in place of `25-feedlistener.yaml`.

If `delete feed` appears to hang, `kubectl -n elite get jobs` shows the drain Job and its logs say
why; it gives up after 3 attempts and releases rather than wedging the namespace.

## Things worth knowing

**A brand-new cluster starts NotReady, briefly.** The Redis health check reports Unhealthy until
some Elite Dangerous data exists, so web pods stay out of the Service until ingestion has written
its first docking — seconds after the ingestion pod starts, but it means an empty cluster serves
503 from the ingress for a moment rather than an empty page.

**Only Redis gates readiness on the web tier.** The EDDN stream check is reported on `/health` for
an uptime monitor, deliberately not in the `ready` set: a quiet firehose must not drain the
Service while the pods are still serving 30-day data.

**The ingestion Deployment must never scale past 1.** Two subscribers would double-count every
docking event. `strategy: Recreate` is what stops a rolling update from briefly running two.

**Test certificates first.** Switch the `cert-manager.io/cluster-issuer` annotation in
`40-ingress.yaml` to `letsencrypt-staging`, confirm a certificate is issued, then move it to
`letsencrypt-prod` — Let's Encrypt's rate limits are unforgiving of a misconfigured HTTP-01 loop.

**Redis memory.** `maxmemory 256mb` with `allkeys-lru` against a 768Mi container limit on a 2 GB
node. Every key is TTL'd and disposable, so eviction is the right failure mode; raise both
together if the working set outgrows it.

**SSE through the ingress.** `proxy-buffering: off` and the long read timeout in the Ingress
annotations are what keep `/api/stream` working. Without them the ticker silently stalls and then
drops every 60s while everything behind the proxy looks healthy.
