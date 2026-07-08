import {
  getWebInstrumentations,
  initializeFaro,
  TransportItem,
} from '@grafana/faro-web-sdk';
import { TracingInstrumentation } from '@grafana/faro-web-tracing';
import { environment } from '../../environments/environment';

// Runtime env vars injected by docker-entrypoint.sh into /assets/env.js at container
// startup.  Allows the collector URL to be changed per environment without
// rebuilding the image.  Falls back to the Grafana Cloud collector URL for production
// and the Angular environment file for local `ng serve` development.
// window.__ENV's type lives in src/window-env.d.ts, not here — see that file for why.

export function initFaro(): void {
  // Production / k3d: FARO_URL is injected at container startup from the
  // grafana-cloud-secrets K8s Secret (key: FARO_COLLECTOR_URL) via
  // docker-entrypoint.sh → assets/env.js → window.__ENV.
  // Local ng serve: falls back to the environment.ts value (port-forwarded Alloy).
  //
  // Use || (not ??) — docker-entrypoint.sh writes FARO_URL="" (empty string, not
  // null/undefined) when the secret is absent. ?? would not trigger on "".
  const faroUrl = window.__ENV?.FARO_URL || environment.faroUrl;

  // No-op when both sources are empty (no cloud credentials, no local Alloy endpoint).
  // Faro SDK throws on an empty URL; guarding here keeps the app functional.
  if (!faroUrl) {
    console.info('[Faro] FARO_URL not configured — RUM disabled');
    return;
  }

  initializeFaro({
    url: faroUrl,
    app: {
      name: 'signal-forge',
      version: '1.0.0',
      environment: environment.production ? 'production' : 'local',
    },

    // Persist session across page reloads; sample 100% of sessions in the lab
    // (reduce samplingRate to 0.1 in production to limit cardinality).
    sessionTracking: {
      samplingRate: 1,
      persistent: true,
    },

    // Scrub PII / noise before any event leaves the browser.
    // Return null to drop the event; return the item to forward it.
    beforeSend: (item: TransportItem) => {
      // Drop health-check noise captured by console instrumentation.
      if (JSON.stringify(item.payload).includes('/healthz')) {
        return null;
      }
      return item;
    },

    instrumentations: [
      // All default web instrumentations (page loads, navigation, console, errors, etc.)
      ...getWebInstrumentations(),

      // Tracing: creates OTel spans for XHR/fetch calls and propagates W3C traceparent
      // to the gateway API so browser-initiated spans link into the backend trace waterfall.
      new TracingInstrumentation({
        instrumentationOptions: {
          propagateTraceHeaderCorsUrls: [
            new RegExp(environment.apiBaseUrl.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')),
            /http:\/\/localhost/,
          ],
        },
      }),
    ],
  });
}
