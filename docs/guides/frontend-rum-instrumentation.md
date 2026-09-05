---
title: "Guide: Frontend RUM Instrumentation"
description: "Step-by-step: instrument an Angular frontend with Grafana Faro for browser RUM — SDK setup, runtime config injection, source-map upload, and browser-to-backend trace linkage."
tags: ["ShipSolid", "Signal Forge", "Observability", "Guides", "Frontend"]
updated: 2026-07-30
zettelId: "202607301400-05"
relations:
  - slug: projects/app-signal-forge/guides/README
    kind: related
  - slug: projects/app-signal-forge/guides/collector-pipeline-setup
    kind: depends_on
  - slug: projects/app-signal-forge/observability/otel-contracts
    kind: depends_on
---

## Guide: Frontend RUM Instrumentation

Prerequisite: [[collector-pipeline-setup|Collector & Pipeline Setup]], specifically
[[collector-pipeline-setup#Step 9 — Frontend RUM ingestion is a separate concern from OTLP|Step 9 on Faro ingestion]]
— decide _before_ this guide whether your browser will talk directly to Grafana Cloud's managed Faro
endpoint or to a self-hosted receiver, since that decision is just a URL value everywhere below but
changes what network path has to exist.

This guide is written against Angular; the Faro Web SDK itself is framework-agnostic (React, Vue,
and vanilla JS all use the same `@grafana/faro-web-sdk` core), so the SDK configuration in Steps 2–4
applies regardless of framework — only the "where do I call this on startup" wiring in Step 2 is
Angular-specific.

### Step 1 — Install packages

```bash
npm install @grafana/faro-web-sdk @grafana/faro-web-tracing
npm install --save-dev @grafana/faro-webpack-plugin
```

`faro-web-sdk`/`faro-web-tracing` are runtime dependencies — they ship in your bundle.
`faro-webpack-plugin` is a **dev** dependency used only at build time, for uploading source maps so
stack traces in Faro's error reports resolve to your original source (Step 6) — it never ships to
the browser.

If you're on Angular and need webpack customization hooks that the stock Angular CLI builder doesn't
expose (needed for Step 6's plugin registration), also install a custom webpack builder:

```bash
npm install --save-dev @angular-builders/custom-webpack
```

and point `angular.json`'s `architect.build.builder` at `@angular-builders/custom-webpack:browser`
with `options.customWebpackConfig.path` set to your webpack config file.

### Step 2 — Initialize Faro at the right point in your app's bootstrap

Put the init call in its own module, and invoke it as early as possible in your framework's
bootstrap sequence — **before** the rest of the app renders, so instrumentation is live before any
user interaction happens.

In Angular, use `APP_INITIALIZER`, not a call from `main.ts` directly — `APP_INITIALIZER` factories
run and resolve before the root component is constructed, which is the guarantee you actually want:

```typescript
// app.config.ts
import { ApplicationConfig, APP_INITIALIZER, ErrorHandler } from '@angular/core';
import { initFaro } from './telemetry/faro';
import { FaroErrorHandler } from './telemetry/faro-error-handler';

export const appConfig: ApplicationConfig = {
  providers: [
    { provide: APP_INITIALIZER, useValue: initFaro, multi: true },
    { provide: ErrorHandler, useClass: FaroErrorHandler },
    // ... your other providers
  ],
};
```

```typescript
// main.ts
// Faro RUM is initialised via APP_INITIALIZER in app.config.ts — do not call initFaro() here.
bootstrapApplication(AppComponent, appConfig);
```

### Step 3 — Configure `initializeFaro()`

```typescript
// telemetry/faro.ts
import { initializeFaro, getWebInstrumentations } from '@grafana/faro-web-sdk';
import { TracingInstrumentation } from '@grafana/faro-web-tracing';
import { environment } from '../../environments/environment';

export function initFaro(): void {
  const faroUrl = window.__ENV?.FARO_URL || environment.faroUrl;   // runtime override wins — see Step 5

  if (!faroUrl) {
    console.info('[Faro] FARO_URL not configured — RUM disabled');
    return;   // the SDK throws on an empty url; guard explicitly instead of letting init blow up
  }

  initializeFaro({
    url: faroUrl,
    app: {
      name: 'my-frontend-app',
      version: '1.0.0',
      environment: environment.production ? 'production' : 'local',
    },
    sessionTracking: { samplingRate: 1, persistent: true },   // lower samplingRate for high-traffic prod
    beforeSend: scrubTelemetryItem,
    instrumentations: [
      ...getWebInstrumentations(),
      new TracingInstrumentation({
        instrumentationOptions: {
          // Must match your real API origin(s) — this is what triggers traceparent injection
          propagateTraceHeaderCorsUrls: [
            new RegExp(environment.apiBaseUrl.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')),
          ],
        },
      }),
    ],
  });
}
```

`propagateTraceHeaderCorsUrls` is not optional decoration — if your API's origin doesn't match one
of these patterns, `TracingInstrumentation` will still create browser-side spans for your XHR/fetch
calls, but it will **not** inject the `traceparent` header into the actual request, and your backend
span will never become a child of the browser span. This is the single most common way frontend RUM
"works" (spans show up in Faro) while browser-to-backend correlation silently doesn't.

`sessionTracking.samplingRate: 1` (100%) is fine for a lab or low-traffic app; drop it for real
production traffic volume.

### Step 4 — Scrub noise and PII, and wire up error capture

```typescript
// telemetry/faro.ts (continued)
export function scrubTelemetryItem(item: any): any | false {
  const json = JSON.stringify(item);
  if (json.includes('/healthz')) return false;   // drop health-check poll noise entirely
  const scrubbed = json.replace(/[\w.+-]+@[\w-]+\.[\w.-]+/g, '[redacted-email]');
  return JSON.parse(scrubbed);
}
```

Export this as a standalone function (not inline in the `initializeFaro()` call) so it's unit
testable on its own.

Replace Angular's default `ErrorHandler` — which just logs to the console — with one that also
forwards to Faro:

```typescript
// telemetry/faro-error-handler.ts
import { ErrorHandler, Injectable } from '@angular/core';
import { faro } from '@grafana/faro-web-sdk';

@Injectable()
export class FaroErrorHandler implements ErrorHandler {
  handleError(error: unknown): void {
    console.error(error);
    faro.api?.pushError(error as Error);
  }
}
```

Registered in `app.config.ts` in Step 2.

### Step 5 — Inject the collector URL at runtime, not at build time

Do **not** bake `FARO_URL` into your compiled JS bundle. The same built image needs to work across
environments (local, staging, prod, self-hosted-collector vs. Grafana-Cloud-direct) without a
rebuild, and rewriting a cache-busted, content-hashed bundle file in place either gets ignored (the
browser already has a cached copy under that filename) or poisons the cache for every other client.

Instead, load a tiny separate script **before** your app bundle that sets a global, and read from
that global at runtime with a build-time fallback:

```html
<!-- index.html -->
<script src="assets/env.js"></script>
<!-- app bundle scripts follow -->
```

```typescript
// window-env.d.ts
export {};
declare global {
  interface Window {
    __ENV?: { FARO_URL?: string; API_BASE_URL?: string };
  }
}
```

`assets/env.js` in your source tree is a **placeholder** for local dev
(`window.__ENV = { FARO_URL: '' }`) — it's never the thing that runs in a deployed environment. In
Kubernetes, mount a ConfigMap over that same path via `subPath`, so the built image (whose
filesystem may be read-only) never needs to be written to at runtime:

```yaml
volumeMounts:
  - name: frontend-env-js
    mountPath: /usr/share/nginx/html/assets/env.js
    subPath: env.js
    readOnly: true
volumes:
  - name: frontend-env-js
    configMap:
      name: frontend-env-js
```

with the ConfigMap rendered from your deploy tooling using the actual `FARO_URL` value for that
environment. A rollout restart of the frontend Deployment picks up a changed ConfigMap value — no
image rebuild required. (An earlier iteration of a comparable setup used a container-startup
`entrypoint.sh` script that wrote this file at container start instead of mounting it — that also
works, but is unnecessary complexity once you can mount the value directly; prefer the ConfigMap
mount.)

Read the same pattern for any other runtime-varying value (API base URL, feature flags) — one
`window.__ENV` global, one type declaration, every consumer reads
`window.__ENV?.<KEY> || environment.<key>` so local `ng serve`/equivalent dev servers still work off
the build-time fallback.

### Step 6 — Build-time secret: source-map upload (separate concern from Step 5)

`FARO_URL` (Step 5, runtime, no secret) and a Faro **API key** for source-map upload (build-time,
genuinely a secret) are unrelated values with different lifecycles — don't conflate them.

```dockerfile
# Dockerfile
ARG FARO_API_KEY
ENV FARO_API_KEY=${FARO_API_KEY}
RUN npm run build
```

Pass it in at build time via your build tooling's env-var-with-fallback pattern (shell env wins over
a config-file default, so local iteration doesn't require exporting anything):

```yaml
# your build config
build_args_from_env:
  - FARO_API_KEY
build_args_from_conf:
  FARO_API_KEY: monitoring.grafana_cloud.faro.api_key
```

In `webpack.config.ts`, register the source-map upload plugin **conditionally** — skip it silently
when the key isn't set, so local/CI builds without the secret still succeed:

```typescript
import { FaroSourceMapUploaderPlugin } from '@grafana/faro-webpack-plugin';

const plugins = [];
if (process.env['FARO_API_KEY']) {
  plugins.push(new FaroSourceMapUploaderPlugin({
    appName: 'my-frontend-app',
    endpoint: 'https://faro-api-<region>.grafana.net/faro/api/v1',   // your own Faro app registration
    appId: '<your appId>',       // from your Grafana Cloud Faro app registration — not portable across orgs
    stackId: '<your stackId>',   // ditto
    apiKey: process.env['FARO_API_KEY'],
  }));
}
```

`endpoint`/`appId`/`stackId` are specific to _your_ Grafana Cloud org's Faro app registration — get
your own values when registering a new Faro app rather than reusing another project's. Only the API
key is meant to be parameterized per-environment.

### Step 7 — Confirm the reverse proxy/ingress forwards the trace header

`TracingInstrumentation` injects `traceparent` into every XHR/fetch request your app makes. That
header has to survive everything between the browser and your instrumented backend:

- If nginx sits in front of your app (serving static assets, proxying `/api`), ensure header
  forwarding isn't explicitly disabled (nginx forwards proxied headers by default unless you've set
  something that strips them).
- If your backend service is reached only through your K8s Ingress (not an nginx sidecar), the same
  applies at the ingress controller — most (Traefik, ingress-nginx) forward arbitrary headers by
  default; verify yours doesn't have a header-stripping rule that would catch `traceparent`.

On the backend side, this only works if the receiving framework's HTTP server instrumentation reads
`traceparent` and makes the request span a child of it — this is automatic in the .NET
(`AddAspNetCoreInstrumentation()`) and Python (`FastAPIInstrumentor`) auto-instrumentation covered
in the other two guides; no frontend-side action needed beyond getting the header there intact.

### Step 8 — Remember: RUM traffic and OTLP traffic take different network paths

Revisit
[[collector-pipeline-setup#Step 9 — Frontend RUM ingestion is a separate concern from OTLP|Collector Setup Step 9]]
before deploying this. In short: if you're pointing `FARO_URL` at Grafana Cloud's managed Faro
endpoint, the browser talks to the internet directly and your cluster's OTLP receiver is irrelevant
to this traffic entirely. If you're self-hosting, `FARO_URL` needs to point at a Faro-receiver
component's own port — never the same port/component your backend services send OTLP to.

### Step 9 — Verify

1. Open the app in a browser, perform an action that calls your instrumented API.
2. In Grafana → your Faro app's Explore view, confirm the page-load event and the fetch/XHR span
   appear.
3. Find the `traceparent` value the browser sent (browser devtools → Network tab → request headers
   on the API call), and confirm a trace with that same `traceId` exists in your trace backend,
   containing both the Faro-originated client span and the backend's server span as parent/child.
4. Trigger an uncaught error in the app (or use a dedicated test route, as this project's
   `error-test` page does) and confirm it surfaces as a Faro error event with a resolved stack trace
   (requires Step 6's source-map upload to have run against the same build).

You now have all three application-side guides done. If you haven't already, close the loop with
[[collector-pipeline-setup|Collector & Pipeline Setup]]'s end-to-end verification, tracing one
request from browser through every backend hop.
