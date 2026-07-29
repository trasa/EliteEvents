# Running the stack on DigitalOcean Kubernetes

Everything here is a one-time provisioning runbook plus the manifests the deploy scripts apply.
Nothing in this directory creates cloud resources on its own.

Target: `k8s.meancat.com`, running side by side with the droplet stack on its own Redis. The two
never share data, so both can ingest from EDDN at the same time without double-counting.

## What it costs

| Resource | Monthly |
|---|---|
| DOKS control plane | free |
| 2 × `s-1vcpu-2gb` nodes | $24 |
| Load balancer (ingress-nginx) | $12 |
| Container registry, Basic tier | $5 |
| 5 GiB block storage for Redis | ~$0.50 |
| **Total** | **~$42** |

The droplet stack keeps running alongside until Phase 5, so expect to pay for both during cutover.

## One-time provisioning

```bash
doctl auth init                       # the stored token expires; re-run when API calls 401

REGION=nyc3
CLUSTER=elite
REGISTRY=meancat

# 1. Container registry (Basic: 5 GB, unlimited repos). The name is globally unique.
doctl registry create "$REGISTRY" --subscription-tier basic --region "$REGION"

# 2. Cluster.
doctl kubernetes cluster create "$CLUSTER" \
  --region "$REGION" \
  --version latest \
  --node-pool "name=pool-1;size=s-1vcpu-2gb;count=2;auto-scale=false" \
  --wait

# kubeconfig is merged and made current by the create; to fetch it again later:
doctl kubernetes cluster kubeconfig save "$CLUSTER"

# 3. Let the cluster pull from the registry. This creates the `registry-meancat` pull secret and
#    propagates it to namespaces, which is the name the Deployments reference.
doctl kubernetes cluster registry add "$CLUSTER"

# 4. Ingress controller. Provisions the DO load balancer.
helm repo add ingress-nginx https://kubernetes.github.io/ingress-nginx
helm repo update
helm install ingress-nginx ingress-nginx/ingress-nginx \
  --namespace ingress-nginx --create-namespace \
  --set controller.publishService.enabled=true

# 5. cert-manager for Let's Encrypt.
helm repo add jetstack https://charts.jetstack.io
helm repo update
helm install cert-manager jetstack/cert-manager \
  --namespace cert-manager --create-namespace \
  --set crds.enabled=true

# 6. Let's Encrypt issuers. Cluster-scoped, so they are applied here rather than being part of
#    the app's kustomization.
kubectl apply -f k8s/cluster-issuer.yaml
```

### DNS

```bash
kubectl -n ingress-nginx get svc ingress-nginx-controller -o jsonpath='{.status.loadBalancer.ingress[0].ip}'
```

Point an A record for `k8s.meancat.com` at that IP. cert-manager cannot issue until the record
resolves — the HTTP-01 challenge is served through this same ingress.

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
| `20-ingestion.yaml` | the EDDN writer: `replicas: 1`, `strategy: Recreate` |
| `30-web.yaml` | web Deployment (2 replicas) + Service |
| `40-ingress.yaml` | ingress-nginx rules, TLS, SSE-friendly proxy settings |
| `cluster-issuer.yaml` | Let's Encrypt staging + prod issuers — cluster-scoped, applied at provisioning time, not part of `kustomization.yaml` |

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
