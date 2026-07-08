import {
  getWebInstrumentations,
  initializeFaro,
  TransportItem,
  TransportItemType,
} from '@grafana/faro-web-sdk';
import { TracingInstrumentation } from '@grafana/faro-web-tracing';
import { environment } from '../../environments/environment';

// Illustrative, not exhaustive — a real PII scrubber for user-entered fields
// (order description, project name/owner) would need one pattern per data
// class this lab doesn't collect (SSNs, phone numbers, etc.). This redacts
// the one pattern realistically likely to show up in a console-captured
// error message or log line.
const EMAIL_PATTERN = /[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}/g;

// Scrub PII / noise before any event leaves the browser.
// Return null to drop the event; return the item to forward it.
// Exported standalone (rather than inlined in initFaro's config object) so
// it's unit-testable without mocking the whole Faro SDK initialization.
export function scrubTelemetryItem(item: TransportItem): TransportItem | null {
  // Drop health-check noise captured by console instrumentation. Scoped to
  // LOG items' actual message field — the previous version JSON.stringify()'d
  // the *entire* payload of every item type and substring-matched it, so any
  // event with "/healthz" anywhere in any nested field (a user-typed value, a
  // stack trace's file path) was a false-positive drop, not just console noise.
  if (item.type === TransportItemType.LOG) {
    const message = (item.payload as { message?: string }).message ?? '';
    if (message.includes('/healthz')) {
      return null;
    }
    return { ...item, payload: { ...item.payload, message: message.replace(EMAIL_PATTERN, '[redacted-email]') } };
  }
  return item;
}

// Runtime env vars mounted into /assets/env.js via a K8s ConfigMap (see
// deploy-local.sh's apply_frontend_env_configmap()).  Allows the collector URL
// to be changed per environment without rebuilding the image.  Falls back to
// the Grafana Cloud collector URL for production and the Angular environment
// file for local `ng serve` development.
// window.__ENV's type lives in src/window-env.d.ts, not here — see that file for why.

export function initFaro(): void {
  // Production / k3d: FARO_URL is mounted from the frontend-env-js ConfigMap,
  // rendered from the same Grafana Cloud credentials as grafana-cloud-secrets.
  // Local ng serve: falls back to the environment.ts value (port-forwarded Alloy).
  //
  // Use || (not ??) — the ConfigMap renders FARO_URL="" (empty string, not
  // null/undefined) when no credentials are configured. ?? would not trigger on "".
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

    beforeSend: scrubTelemetryItem,

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
