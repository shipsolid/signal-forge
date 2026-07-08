import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { retry, timeout, timer } from 'rxjs';
import { catchError, throwError } from 'rxjs';

const REQUEST_TIMEOUT_MS = 10_000;
const MAX_RETRIES = 2;
const RETRY_BASE_DELAY_MS = 500;

/**
 * Thrown by resilienceInterceptor instead of the raw HttpErrorResponse.
 * `.message` is always a safe, generic string — HttpErrorResponse.message
 * embeds the full request URL (including upstream service hostnames like
 * gateway-api's own address), which a component binding it straight into the
 * template would leak to the user.
 */
export class ApiError extends Error {
  constructor(public readonly status: number, message: string) {
    super(message);
  }
}

function toUserMessage(err: HttpErrorResponse): string {
  if (err.status === 0) return 'Unable to reach the server. Check your connection and try again.';
  if (err.status === 404) return 'The requested resource was not found.';
  if (err.status >= 500) return 'The server had a problem handling that request. Please try again shortly.';
  if (err.status >= 400) return 'That request could not be processed. Please check your input and try again.';
  return 'Something went wrong. Please try again.';
}

/**
 * Every ApiService call goes through this: a timeout so a hung downstream
 * (gateway-api → order-api/notification-svc) doesn't spin a button forever,
 * a bounded retry with backoff for GETs, and error normalization so
 * components never see a raw HttpErrorResponse.
 */
export const resilienceInterceptor: HttpInterceptorFn = (req, next) => {
  const response$ = next(req).pipe(timeout(REQUEST_TIMEOUT_MS));

  // Only GET is safe to retry blindly here — POST/DELETE aren't idempotent at
  // this layer (no client-generated idempotency key on the browser→gateway
  // hop, unlike gateway-api's own downstream call to order-api).
  const withRetry$ =
    req.method === 'GET'
      ? response$.pipe(retry({ count: MAX_RETRIES, delay: (_, retryIndex) => timer(retryIndex * RETRY_BASE_DELAY_MS) }))
      : response$;

  return withRetry$.pipe(
    catchError((err: unknown) => {
      const apiError =
        err instanceof HttpErrorResponse
          ? new ApiError(err.status, toUserMessage(err))
          : new ApiError(0, 'Something went wrong. Please try again.');
      return throwError(() => apiError);
    })
  );
};
