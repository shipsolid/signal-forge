# Networking & TLS

Network-plane security for signal-forge: NetworkPolicies, Ingress TLS via cert-manager, and the documented gaps.

## NetworkPolicy model

The manifests in [k8s/infra/network-policies.yaml](../../k8s/infra/network-policies.yaml) implement a **default-deny-plus-tiered-allows** model for the `otel-lab` namespace.

### ⚠️ k3d caveat

**Stock k3d uses flannel, which does not enforce NetworkPolicies.** The manifests are silently accepted but no pod-to-pod traffic is actually blocked. To enforce them locally, recreate the cluster with flannel disabled:

```
k3d cluster create otel-lab \
  --k3s-arg '--flannel-backend=none@server:*' \
  --k3s-arg '--disable-network-policy=false@server:*'
# then install Calico or Cilium
```

For real clusters (EKS with the VPC CNI + NetworkPolicy, GKE with Dataplane V2, AKS with Azure CNI Overlay + Cilium) the manifests apply and enforce without any change.

### Policies and what they allow

| Policy                          | `podSelector`       | Type           | Allows                                        |
| ------------------------------- | ------------------- | -------------- | --------------------------------------------- |
| `default-deny-all`              | `{}` (all pods)     | Ingress+Egress | nothing                                       |
| `allow-dns-egress`              | `{}`                | Egress         | UDP/TCP 53 to kube-system                     |
| `allow-ingress-from-controller` | `tier=app`          | Ingress        | any namespace → TCP 5000/5001/8000/8080       |
| `allow-app-to-app`              | `tier=app`          | Egress         | tier=app → TCP 5000/5001/8000/8080            |
| `allow-app-to-datastore`        | `tier=app`          | Egress         | tier=datastore → 3306/5432/6379/5672          |
| `allow-datastore-from-app`      | `tier=datastore`    | Ingress        | tier=app → all datastore ports                |
| `allow-app-to-alloy-receiver`   | `tier=app`          | Egress         | ns=monitoring, app=alloy-receiver → 4317/4318 |
| `allow-frontend-egress-https`   | `app=otel-frontend` | Egress         | 0.0.0.0/0:443 (minus cluster CIDRs)           |

### Why `allow-ingress-from-controller` uses `namespaceSelector: {}`

The ingress controller on k3d is Traefik in `kube-system`. On EKS it's the ALB controller in `ingress-nginx` or elsewhere. Rather than hardcode the namespace, the policy allows ingress from any namespace — relying on the `namespaceSelector` label filtering the caller side (only one cluster-level ingress controller exists in practice). If you need tighter control, replace the empty selector with:

```yaml
namespaceSelector:
  matchLabels:
    kubernetes.io/metadata.name: kube-system
```

### Egress to Grafana Cloud

The frontend needs to reach `faro-api-...grafana.net:443` for RUM. The policy allows all of `0.0.0.0/0:443` except the cluster's pod and service CIDRs. This is intentionally broad because:

1. FQDN-based egress is CNI-specific (Cilium has CNPs, stock NetworkPolicy doesn't).
2. Restricting by IP block breaks whenever Grafana Cloud rotates their edge IPs.

If you run on Cilium, replace this with a `CiliumNetworkPolicy` and `toFQDNs: [{matchPattern: "*.grafana.net"}]`.

Backend services (gateway/order/notification) do **not** have egress to the internet — their telemetry goes to the in-cluster alloy-receiver, which then forwards to Grafana Cloud from ns/monitoring. So backend pods stay behind the alloy perimeter.

### Verifying

From a deployed cluster **with a CNI that enforces policies**:

```bash
# Should succeed: app-to-datastore
kubectl -n otel-lab exec -it deploy/gateway-api -- nc -vz mysql 3306

# Should fail (timeout): direct egress to the internet from backend
kubectl -n otel-lab exec -it deploy/gateway-api -- nc -vz www.google.com 443

# Should succeed: frontend egress to HTTPS
kubectl -n otel-lab exec -it deploy/otel-frontend -- nc -vz api.grafana.net 443
```

## Ingress TLS with cert-manager

### Architecture

```
                                    (one-time bootstrap)
    ClusterIssuer selfsigned-bootstrap
            │
            │ issues
            ▼
    Certificate signal-forge-ca  (10-year self-signed root, in ns/cert-manager)
            │
            │ renewed every 11 months
            ▼
    ClusterIssuer signal-forge-ca
            │
            │ referenced by Ingress annotation
            ▼
    Ingress otel-lab-ingress  (cert-manager.io/cluster-issuer: signal-forge-ca)
            │
            │ cert-manager reconciles → issues leaf cert
            ▼
    Secret signal-forge-tls  (in ns/otel-lab)  —  consumed by Traefik for TLS termination
```

### Bootstrap order

`deploy-local.sh` installs cert-manager **before** the datastore stage, so the `ClusterIssuer` and `Certificate` CRs for the CA land before any Ingress reconciliation is attempted:

1. `apply_stage infra` — applies `k8s/infra/ingress.yaml` (Ingress + TLS annotation references the CA issuer)
2. `apply_app_env_configmap`
3. `apply_grafana_cloud_secret`
4. `install_cert_manager` — Helm install + wait for webhook + apply `cert-manager-issuer.yaml`
5. …datastores, app, post

The Ingress is created in step 1 but its TLS Secret (`signal-forge-tls`) is populated by cert-manager in step 4. The hostless rule in the Ingress keeps `http://localhost:8080` working throughout — you don't need TLS to be healthy to hit the frontend for the first time.

### Using the cert locally

The CA is self-signed by a key that only exists inside the cluster. To trust it on your host:

```bash
# Export the CA cert:
kubectl -n cert-manager get secret signal-forge-ca-key-pair -o jsonpath='{.data.ca\.crt}' | base64 -d > /tmp/signal-forge-ca.crt

# Add a /etc/hosts entry:
echo "127.0.0.1 signal-forge.local" | sudo tee -a /etc/hosts

# Trust the CA (Linux example):
sudo cp /tmp/signal-forge-ca.crt /usr/local/share/ca-certificates/
sudo update-ca-certificates
```

Then browse to `https://signal-forge.local:8443`.

Without the trust step, curl and browsers show a self-signed cert warning — that's expected and is what a real production cert-manager + LetsEncrypt flow would _not_ exhibit.

### Swapping for a real ACME issuer

The `signal-forge-ca` ClusterIssuer can be replaced by ACME (Let's Encrypt) or your enterprise CA with no Ingress change. Example:

```yaml
apiVersion: cert-manager.io/v1
kind: ClusterIssuer
metadata:
  name: signal-forge-ca         # same name — Ingress annotation unchanged
spec:
  acme:
    server: https://acme-v02.api.letsencrypt.org/directory
    email: ops@example.com
    privateKeySecretRef:
      name: letsencrypt-prod
    solvers:
      - http01:
          ingress:
            ingressClassName: traefik
```

### Disabling TLS

Set `security.tls.enabled: false` in [conf.yml](../../conf.yml). `deploy-local.sh` then skips the cert-manager Helm install and the Issuer apply. The Ingress's hostless rule keeps HTTP working; the TLS host rule is still present in the manifest but cert-manager has no Issuer to reconcile.

## What this doesn't cover

- **mTLS between services.** No service mesh. Service-to-service traffic is plaintext HTTP/gRPC behind cluster-local IPs. If you need mTLS, add Linkerd (simplest) or Istio (full-featured).
- **Traefik rate limiting / WAF.** The k3d-default Traefik has no rate limits; on prod either add `traefik.ingress.kubernetes.io/router.middlewares: ...` annotations or front with a CDN/WAF.
- **Egress filtering at the CNI layer.** FQDN egress is CNI-specific (see above).
