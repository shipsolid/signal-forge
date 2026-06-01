import { ErrorHandler, Injectable } from '@angular/core';
import { faro } from '@grafana/faro-web-sdk';

/**
 * Angular ErrorHandler that forwards all unhandled errors to Grafana Faro RUM.
 *
 * Register this in app.config.ts:
 *   { provide: ErrorHandler, useClass: FaroErrorHandler }
 *
 * Without this handler, unhandled Angular exceptions (component errors,
 * uncaught Promise rejections, etc.) are swallowed by Angular's default
 * handler and never reach Faro's error tracking.
 *
 * ACA reference: "Error boundary integration — Angular ErrorHandler forwarding to Faro"
 */
@Injectable()
export class FaroErrorHandler implements ErrorHandler {
  handleError(error: unknown): void {
    // Normalise to Error so Faro captures a proper stack trace.
    const err = error instanceof Error ? error : new Error(String(error));

    // Push to Faro — appears in Grafana as an "exception" event linked to
    // the current session and active trace span.
    faro.api?.pushError(err);

    // Also write to console so developers see the error during local development.
    console.error('[FaroErrorHandler]', err);
  }
}
