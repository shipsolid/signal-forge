# Security Policy

SignalForge is a personal lab / portfolio project (single owner, no production deployment, no
SLA — see [README.md § Ownership Boundary](README.md#ownership-boundary)). This page covers how to
report a vulnerability. For the threat model, secrets lifecycle, and hardening controls, see
[docs/operations/security.md](docs/operations/security.md).

## Scope

In scope:

- The application code in `src/` (gateway-api, order-api, notification-svc, frontend)
- The Kubernetes manifests and Helm values in `k8s/`
- `deploy-local.sh`, `scripts/`, and CI workflows in `.github/workflows/`

Out of scope:

- The `.env` / `conf.yml` placeholder values tracked in this repo — these are documented
  learning-lab scaffolding (see [CLAUDE.md § Environmental gotchas](CLAUDE.md#environmental-gotchas)),
  not live credentials. Reporting that a placeholder value "looks like a secret" is not actionable;
  reporting that a *real* secret was committed (check timestamps/rotation history) is.
- The vendored `grafana/k8s-monitoring` Helm chart and other third-party dependencies — report those
  upstream.

## Supported versions

There are no tagged releases or version branches. Only the `main` branch is maintained; fixes land
there directly.

## Reporting a vulnerability

Preferred: open a [GitHub Security Advisory](https://github.com/shipsolid/app-signal-forge/security/advisories/new)
(repo → Security tab → "Report a vulnerability"). This is private until a fix is available.

If advisories are unavailable to you, open a regular GitHub issue with the non-sensitive summary
only (affected file/endpoint, impact class) and note that exploit details will follow privately —
do not post exploit payloads, working PoCs, or credential material in a public issue.

There is no dedicated security email or bug-bounty program; this is a single-maintainer project.
Response time is best-effort, not SLA-backed.

## Disclosure

No embargo period is enforced. Once a fix is merged to `main`, the finding can be disclosed. Credit
is given in the fix commit message unless you ask otherwise.
