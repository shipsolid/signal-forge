# Supply-chain security

What CI verifies before an image can land in production: vulnerability scanning, SBOM generation, and keyless signing.

## The pipeline

[`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) — job `build-images` runs a matrix across the four services (`otel-frontend`, `gateway-api`, `order-api`, `notification-svc`) and, for each:

1. **Build** the image with Docker buildx (`load: true` so the following steps see the same artifact).
2. **Trivy scan** for HIGH/CRITICAL CVEs with an available fix (`ignore-unfixed: true`). Fails the job if any are found.
3. **SBOM** generation with Syft (CycloneDX JSON) — uploaded as an artifact.
4. **Push** to GHCR (main branch only).
5. **Sign** with cosign keyless OIDC (main branch only).
6. **Attest** the SBOM via `cosign attest --type cyclonedx` — binds the SBOM to the image digest.
7. **Verify** the signature just produced, on the same digest, in the same job (main branch only) — see §Signing below.

PRs run steps 1–3 only; push/sign/verify require `id-token: write` + GHCR auth, which we restrict to `main`.

A separate scheduled workflow, [`scheduled-vuln-rescan.yml`](../../.github/workflows/scheduled-vuln-rescan.yml), re-scans the last-published `:latest` image for each service weekly — same Trivy severities, but *without* `ignore-unfixed`, so a CVE that had no fix at build time still gets caught once one appears upstream. Report-only (`exit-code: "0"`), visible in the Security tab; it doesn't block anything since there's no PR to fail.

## Trivy policy

- **Severities**: `HIGH,CRITICAL`. We don't block on `MEDIUM` because it triples noise without proportional security value.
- **`ignore-unfixed: true`**: a HIGH CVE with no upstream patch would fail every CI run forever — nothing we can do about it beyond pinning to an alternative base image. Unfixed findings are still surfaced in the SARIF upload (GitHub Security tab) so they're visible, just not blocking.
- **Scan scope**: `os,library` — OS packages (apt/apk) + language dependencies (nuget, npm, pip). Trivy picks these up from the image layers.

SARIF output is uploaded to GitHub Advanced Security. Historical trend, dismissals, and PR-view annotations live in the "Security" tab of the repo.

## SBOM format

CycloneDX JSON ([cyclonedx.org/specification][cdx]). Attach-rather-than-embed — the SBOM is an OCI referrer on the image manifest, not inlined into the image. This keeps the image itself minimal and lets consumers download SBOMs without pulling the image:

```bash
cosign download sbom ghcr.io/OWNER/signal-forge/gateway-api@sha256:...
```

[cdx]: https://cyclonedx.org/specification/overview/

## Signing: keyless OIDC

Every image gets a cosign signature using the GitHub Actions OIDC identity — no long-lived keys. The signature's certificate embeds:

- The repo (`OWNER/signal-forge`)
- The workflow path (`.github/workflows/ci.yml`)
- The ref that produced the signature (`refs/heads/main`)
- The commit SHA

**Verified automatically**: the `build-images` job runs `cosign verify` against the exact digest it
just signed, in the same job, before the workflow completes — the sign→verify round-trip is
exercised on every push to `main`, not just documented as something you could do. That catches
config drift in the signing step itself (wrong identity, wrong issuer, etc.) immediately instead of
only ever being noticed the first time someone tries to verify manually.

Verify downstream yourself, any time:

```bash
cosign verify ghcr.io/OWNER/signal-forge/gateway-api@sha256:... \
  --certificate-identity-regexp "https://github.com/OWNER/signal-forge/.github/workflows/ci.yml@.*" \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

**Admission-time enforcement** (refuse to *schedule* an unsigned image at deploy time) is still
**not** wired up in this repo — that's a cluster-scoped decision, and a materially bigger lift than
verifying in CI (it needs an admission controller installed in the target cluster). See
§"Admission enforcement" below. What changed here is narrower but real: "we sign our images" used
to mean the signature was produced and never checked again by anything; now it's checked, by CI,
on every push.

## Digest pinning in base images

The Dockerfiles pin every `FROM` line by `@sha256:...`:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0@sha256:f88c77644f4c480a62d3b46dc74db8d5472a24e282df8b1e56195c689d35a6db
```

Why this matters even when CI is fine:

- The `:8.0` tag is mutable — Microsoft publishes new `:8.0` images every month for security updates. Without a digest pin, a rebuild tomorrow might pull a different image than CI verified today.
- Reproducibility for incident forensics: if production had CVE-X in its image, you can find the exact bytes that shipped.
- Zscaler / corporate TLS rewriting does not alter image content (only TLS certs), so pinning is safe behind corporate proxies.

### Refreshing pinned digests

Monthly-ish cadence. Fetch fresh digests:

```bash
for img in mcr.microsoft.com/dotnet/sdk:8.0 mcr.microsoft.com/dotnet/aspnet:8.0 \
           python:3.12-slim node:20-alpine nginxinc/nginx-unprivileged:alpine; do
  digest="$(docker buildx imagetools inspect "$img" --format '{{.Manifest.Digest}}')"
  echo "$img → $digest"
done
```

Paste the new `sha256:...` into the corresponding `FROM` line in each Dockerfile. Commit as `chore(ci): bump base image digests (YYYY-MM-DD)`.

If a new digest introduces a regression, revert the Dockerfile change — the image is cached in the previous digest for ~90 days on Docker Hub / MCR, so pinning back is safe.

## Admission enforcement (not implemented)

To reject unsigned images at deploy time, install one of:

- **[sigstore/policy-controller]** — Kubernetes admission controller that verifies cosign signatures. Configure with a `ClusterImagePolicy` that requires images from `ghcr.io/OWNER/signal-forge/*` to have a signature chaining back to `OWNER/signal-forge`'s workflow.
- **[connaisseur]** — similar, supports multiple signature backends (cosign, notary v1).
- **[kyverno]** with `verifyImages` rules — already a multi-purpose admission controller, can also verify SBOM attestations.

[sigstore/policy-controller]: https://docs.sigstore.dev/policy-controller/overview/
[connaisseur]: https://github.com/sse-secure-systems/connaisseur
[kyverno]: https://kyverno.io/docs/writing-policies/verify-images/

None of these are installed in the lab. The CI signing side ships signatures and now verifies them
itself (see §Signing above); no cluster gates on them at deploy time yet.

## What this doesn't cover

- **Dependency pinning in app code**: `dotnet restore`, `npm ci`, `pip install -r requirements.txt` respect lockfiles but we don't run `npm audit fix` / Dependabot proactively. PRs pass dependency-vulnerability scans (`dotnet list package --vulnerable`, `pip-audit`, `npm audit --audit-level=critical` in the `test-frontend` job); update cadence is ad-hoc.
- **Container image provenance (SLSA)**: our signing stops at "this image was built by this workflow"; it doesn't assert SLSA L3 hermeticity. For that, use `slsa-framework/slsa-github-generator` to produce a SLSA provenance attestation alongside the SBOM.
