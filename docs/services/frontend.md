# Service: frontend (Angular SPA)

**Role**: Browser single-page application. Provides the user interface and instruments browser
telemetry via Grafana Faro.

**Runtime**: Angular 17, built to static assets, served by nginx **Port**: 80 (nginx,
cluster-internal) → exposed via Traefik ingress at `/` **Replicas**: 1

---

## Pages

| Route            | Page           | API calls                                               |
| ---------------- | -------------- | ------------------------------------------------------- |
| `/`              | Dashboard      | `GET /api/projects`                                     |
| `/projects/:id`  | Project detail | `GET /api/projects/:id`, `GET /api/projects/:id/orders` |
| `/orders/new`    | Create order   | `POST /api/orders`                                      |
| `/notifications` | Notifications  | `GET /api/notifications`                                |
| `/error-test`    | Error trigger  | `GET /api/error`                                        |

---

## Faro initialisation (`src/app/telemetry/faro.ts`)

```typescript
import { initializeFaro, getWebInstrumentations } from '@grafana/faro-web-sdk';
import { TracingInstrumentation } from '@grafana/faro-web-tracing';

initializeFaro({
  url: environment.faroUrl,           // Alloy faro.receiver :12347 (or Grafana Cloud)
  app: {
    name: 'otel-frontend',
    version: '1.0.0',
    environment: environment.name,    // 'local' or 'production'
  },
  instrumentations: [
    ...getWebInstrumentations({
      captureConsole: true,           // console.error → Faro log entry
    }),
    new TracingInstrumentation({
      instrumentationOptions: {
        propagateTraceHeaderCorsUrls: [/http:\/\/localhost/, /\.otel-lab\./],
      },
    }),
  ],
});
```

`faroUrl` is injected at build time from `environment.ts`, which is overwritten at runtime via a
Kubernetes ConfigMap or Deployment env var substitution in nginx.

---

## Signals captured by Faro

| Signal                  | Source                     | Where it appears                                         |
| ----------------------- | -------------------------- | -------------------------------------------------------- |
| Page load timing        | `getWebInstrumentations()` | Web Vitals: LCP, FID, CLS                                |
| Route changes           | Angular router integration | Navigation spans in Faro traces                          |
| `fetch`/`XHR` spans     | `TracingInstrumentation`   | Browser spans linked to backend spans                    |
| `traceparent` injection | `TracingInstrumentation`   | Backend `gateway-api` span becomes child of browser span |
| JavaScript errors       | `getWebInstrumentations()` | Faro error events with stack traces                      |
| Console errors          | `captureConsole: true`     | Faro log entries                                         |

---

## Browser → backend trace linkage

When the Angular SPA calls any API endpoint, `TracingInstrumentation` injects a `traceparent`
header:

```
GET /api/projects HTTP/1.1
traceparent: 00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01
```

ASP.NET Core's `AddAspNetCoreInstrumentation()` reads this header and sets the HTTP server span's
parent to the browser span. The result: a single trace starting in the browser and ending in MySQL.

The `propagateTraceHeaderCorsUrls` regex must match the API origin. In local development this is
`http://localhost:8080`. In Kubernetes it's the ingress hostname.

---

## nginx configuration

nginx serves the Angular build and proxies `/api/*` to the gateway-api ClusterIP service. Key
directives:

```nginx
server {
    listen 80;
    root /usr/share/nginx/html;
    index index.html;

    # SPA routing: serve index.html for all non-file routes
    location / {
        try_files $uri $uri/ /index.html;
    }

    # API proxy — propagate all headers including traceparent
    location /api/ {
        proxy_pass http://gateway-api.otel-lab.svc.cluster.local:5000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_pass_request_headers on;
    }
}
```

**Important**: `proxy_pass_request_headers on` ensures the `traceparent` header from the browser
reaches gateway-api. If this is missing, trace propagation breaks at the nginx boundary.

---

## Environment variable injection

`FARO_URL` and `API_BASE_URL` change between local and cloud deployments. Rather than rewriting the
compiled bundle (`envsubst` on `main.js` would fight nginx's immutable JS caching — cache-busted by
filename hash, so a rewritten-in-place `main.js` either gets ignored in favor of a cached copy or
poisons the cache for everyone), `docker-entrypoint.sh` writes a small separate file,
`assets/env.js`, before nginx starts:

```sh
cat > /usr/share/nginx/html/assets/env.js << EOF
window.__ENV = {
  FARO_URL: "${FARO_URL}",
  API_BASE_URL: "${API_BASE_URL}"
};
EOF
```

`index.html` loads it unconditionally via `<script src="assets/env.js"></script>`, before the
Angular bundle. Consumers read `window.__ENV?.<KEY> || environment.<key>` — runtime value first,
falling back to the build-time `environment.ts` value for local `ng serve` (where `env.js` is the
checked-in placeholder in `src/assets/`, never overwritten). `faro.ts` does this for `FARO_URL`;
`api.service.ts` does the same for `API_BASE_URL`, which is what makes the Deployment's
`API_BASE_URL` env var actually take effect — reading only `environment.apiBaseUrl` there would
silently ignore it. The `window.__ENV` type lives in `src/window-env.d.ts`, not inline in either
consumer, so both (and any future one) share one declaration.

Both variables are set in the Deployment; `API_BASE_URL` comes from the Deployment env directly,
`FARO_URL` from the `grafana-cloud-secrets` Secret's `FARO_COLLECTOR_URL` key. A rollout restart of
the frontend Deployment picks up a changed value — no rebuild needed.

---

## Create Order — defensive input handling

`create-order.component.ts` parses the optional `?projectId=` query param defensively:

```typescript
ngOnInit(): void {
    const pid = this.route.snapshot.queryParamMap.get('projectId');
    if (pid !== null) {
        const parsed = parseInt(pid, 10);
        if (!isNaN(parsed) && parsed > 0) this.projectId = parsed;
    }
}
```

This guards against `?projectId=abc` producing `NaN`, which would be silently sent to the API and
rejected as an invalid input.

---

## Docker build

Multi-stage build:

1. `node:20-alpine` — `ng build --configuration production`
2. `nginx:alpine` — copy `/dist/` output, copy custom `nginx.conf`

In corporate proxy environments (e.g. Zscaler), `npm install` fails with TLS errors unless the CA
cert is trusted. The `make build` target handles this automatically: it copies
`/usr/local/share/ca-certificates/zcert.crt` from the host into the build context before
`docker build`, and each Dockerfile stage installs it with `RUN update-ca-certificates` before any
network step. The cert file is git-ignored and never committed.
