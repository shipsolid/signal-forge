---
title: "Immutable CI/CD promotion"
description: "How SignalForge validates, builds, signs, and promotes one immutable four-service release from CI through DEV, QA, and protected PROD."
tags: ["ShipSolid", "Signal Forge", "Deployment", "CI/CD", "Security"]
updated: 2026-09-06
relations:
  - slug: projects/app-signal-forge/operations/supply-chain
    kind: depends_on
  - slug: projects/app-signal-forge/observability/otel-contracts
    kind: depends_on
  - slug: projects/app-signal-forge/operations/runbooks
    kind: related
---

## Immutable CI/CD promotion

The release contract is **build once → secure once → attest once → promote the same digest**. CI
builds each of the four application images exactly once; CD never recompiles source or invokes a
container build. A human-friendly commit tag and `latest` may exist in GHCR, but neither is a
deployment input.

The repository contains a complete promotion workflow, but does not contain GitHub Environment
variables, secrets, cluster credentials, or live Grafana query credentials. Therefore, CD is
**render-only by default**. A repository maintainer must deliberately configure an Environment and
set `DEPLOY_ENABLED=true` before it can mutate a cluster. This is a safety boundary, not a claim
that a live DEV, QA, or PROD target currently exists.

## CI: validation and release creation

`[.github/workflows/ci.yml](https://github.com/shipsolid/signal-forge/blob/main/.github/workflows/ci.yml)`
runs on matching pull requests, pushes to `main`, and manual dispatch.

```text
PR or main
  -> Gitleaks + repository policy
  -> unit tests, frontend production build, protobuf contract
  -> CodeQL + dependency analysis + Trivy IaC scan
  -> observability-as-policy + promtool + Alloy validation
  -> quality gate
  -> build each application image once
  -> local image scan + CycloneDX SBOM
  -> (main only) push registry digest + keyless sign/attest/verify
  -> immutable release-manifest.json
```

All preceding controls are **BLOCKING**. A pull request proves that the source can pass the same
validation and image build/scan path but does not receive GHCR credentials and cannot publish a
release. Only a successful trusted `main` run publishes images and release metadata.

### Release artifact

CI creates one metadata record for each required service:

- `otel-frontend`
- `gateway-api`
- `order-api`
- `notification-svc`

`release-manifest.json` is the sole supported hand-off to CD. It binds the Git commit, CI run and
attempt, SBOM artifact name, Sigstore evidence, and each exact
`ghcr.io/<owner>/signal-forge/<service>@sha256:...` reference. CI rejects a partial four-service
set before publishing the manifest, so a promotion and a rollback always operate on one complete
release.

### Security and observability controls

| Control | Classification | What fails the release |
| --- | --- | --- |
| Gitleaks, lint/repository policy, tests, protobuf contract | BLOCKING | Any failed command |
| CodeQL | BLOCKING | Analysis/upload failure |
| SCA | BLOCKING for .NET/Python and runtime-critical frontend findings | Vulnerable dependency policy breach |
| Trivy IaC | BLOCKING for CRITICAL; HIGH is a warning | Critical misconfiguration |
| Trivy image | BLOCKING | HIGH/CRITICAL, fixed CVE in the release image |
| Observability-as-policy | BLOCKING | Invalid telemetry contract, rendered collector configuration, dashboard shape, required assets, or forbidden span-metric dimension |

The static observability policy checks `service.name` identity, common resource attributes
(`service.namespace`, `service.version`, `deployment.environment`), Alloy/Helm renderability,
Prometheus rule syntax, local Grafana dashboard structure, required SLO/runbook assets, and a
focused denylist of obviously unbounded span-metric dimensions. It does **not** prove data reaches a
backend, dashboard queries work, alerts route, SLOs are being met, or a full cardinality/cost budget
is acceptable. Those require environment-specific runtime data.

## CD: select evidence, then promote it

`[.github/workflows/cd.yml](https://github.com/shipsolid/signal-forge/blob/main/.github/workflows/cd.yml)`
is manual by design. The operator supplies a successful **CI run ID**, not a branch, source commit,
or image tag. CD validates through GitHub's API that the selected run is a successful `main` CI run
from this repository, then downloads the artifact whose name includes that run's attempt number.
It validates the manifest again before handing it to every deployment leg.

```text
trusted CI release manifest
  -> DEV: verify evidence -> deploy digest -> health/smoke/telemetry/DAST
  -> QA:  verify the same evidence -> deploy the same digest -> health/smoke/telemetry
  -> PROD (explicit selection + Environment approval): same digest -> health/smoke/telemetry
```

Promotion is globally serialized, and each environment has its own non-cancelling concurrency lock.
Two releases cannot interleave between DEV, QA, and PROD, and a new rollout never cancels one that
may need rollback.

### Environment contract

The reusable `[deploy-environment.yml](https://github.com/shipsolid/signal-forge/blob/main/.github/workflows/deploy-environment.yml)`
binds the job to the GitHub Environment (`dev`, `qa`, or `prod`). Environment protection rules control
approval and the moment secrets become available. PROD is selected explicitly with
`confirm_production` and requires QA success; any configured reviewers or wait timers remain outside
workflow-input control.

For a real deployment, the protected environment needs:

- Variables: `DEPLOY_ENABLED=true`, `ENVIRONMENT_URL`, `FARO_COLLECTOR_URL`,
  `OTEL_EXPORTER_OTLP_ENDPOINT`, `OBSERVABILITY_GATE_ENABLED=true`, and
  `OBSERVABILITY_GATE_URL` (plus optional namespace/context values).
- Secrets: `KUBE_CONFIG`, `DB_SECRETS_ENV`, and, when required by the telemetry service,
  `OBSERVABILITY_GATE_TOKEN`.

These values are intentionally not committed. CD creates environment-specific ConfigMaps for
runtime endpoint, browser config, and telemetry resource attributes, while preserving the image
digest. The full Git SHA becomes `service.version`; the deployment environment becomes
`deployment.environment`. Database secrets are created separately from the protected Environment
and never enter the uploaded deployment plan.

### Deployment gates and rollback

Before writing kubeconfig or applying a manifest, CD verifies that every GHCR digest exists and has a
keyless Cosign signature plus a CycloneDX attestation from the trusted `main` CI workflow identity.
It renders Kustomize topology with those exact digests, strips placeholder Secrets, and uploads the
secret-free plan for audit.

| Gate | DEV | QA | PROD | Classification |
| --- | --- | --- | --- | --- |
| Server-side apply and rollout status | yes | yes | yes | BLOCKING |
| Deployment/pod exact-digest verification | yes | yes | yes | BLOCKING |
| HTTP and API-shape smoke tests | yes | yes | yes | BLOCKING |
| External observability policy | pass/block; `warn` reports warning | pass/block; `warn` reports warning | pass only; `warn` and `block` fail | policy-dependent |
| OWASP ZAP baseline | yes | no | never | blocking execution/findings; warning-level ZAP rules informational |

If any post-apply gate fails, CD restores the complete previous four-image immutable release and the
captured runtime ConfigMaps. It refuses to roll back to a partial or tag-based prior state. If no
complete prior digest set exists, rollback fails loudly rather than inventing one.

## Current boundaries and next operational work

- There is no admission controller that rejects unsigned images in-cluster. CD verifies evidence
  before apply; a cluster policy such as Sigstore policy-controller or Kyverno would add a separate
  scheduling-time control.
- The external observability gate API and its Grafana queries/approved thresholds are not defined in
  this repository. The workflow fails closed when a configured endpoint returns an unknown or
  blocking result, but it intentionally does not fabricate telemetry checks.
- SLO rules are validated as code. A production promotion becomes SLO-gated only when the external
  observability gate evaluates those rules or an equivalent reliable data source.
- The scheduled vulnerability rescan is manual-only today and examines compatibility `latest` tags;
  it is informational and never a CD input.

For local k3d deployment, use [Local Deployment](local.md). It builds/imports local images for the
lab and is intentionally separate from the immutable GHCR promotion path.
