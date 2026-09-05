---
title: "Signal Forge ADR-006: Fail-fast on missing secrets"
description: "Services throw at startup when required connection strings are absent, instead of silently falling back to defaults that mask misconfiguration."
tags: ["ShipSolid", "Signal Forge", "Architecture"]
updated: 2026-07-10
zettelId: "202607091847-4"
relations:
  - slug: projects/app-signal-forge/architecture/adrs/adr-secretkeyref-for-credentials
    kind: related
---

## Signal Forge ADR-006: Fail-fast on missing secrets

**Status**: Accepted

**Decision**: Services throw at startup if required connection strings are absent or empty. No
fallback to defaults.

**Code pattern (.NET)**:

```csharp
var connStr = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connStr))
    throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection is required. Set the environment variable.");
```

**Rationale**:

- A service that starts without a database connection appears healthy to liveness probes but fails
  all requests. This is worse than failing loudly at startup — it makes root cause harder to find.
- Fail-fast produces a clear error in pod logs immediately, the pod enters `CrashLoopBackOff`, and
  the operator can read the exact missing variable from `kubectl describe pod`.
- Silent defaults (e.g. connecting to `localhost:3306`) work in developer machines but break in
  Kubernetes where there is no local database — this class of environment-specific bugs is
  eliminated.

**Alternative considered**: Fallback defaults — rejected because they hide misconfiguration.

This complements
[[projects/app-signal-forge/architecture/adrs/adr-secretkeyref-for-credentials|secretKeyRef for all credentials]]:
that ADR governs how credentials are stored and referenced; this one governs what happens when a
required one is absent.
