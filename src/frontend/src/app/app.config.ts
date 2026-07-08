import { ApplicationConfig, APP_INITIALIZER, ErrorHandler } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { routes } from './app.routes';
import { initFaro } from './telemetry/faro';
import { FaroErrorHandler } from './telemetry/faro-error-handler';
import { resilienceInterceptor } from './services/resilience.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(routes),
    provideHttpClient(withInterceptors([resilienceInterceptor])),

    // Initialise Faro RUM before the first component renders.
    // APP_INITIALIZER runs synchronously before bootstrap, so all Angular
    // errors, navigation events, and HTTP calls are captured from the start.
    {
      provide: APP_INITIALIZER,
      useValue: initFaro,
      multi: true,
    },

    // Forward all unhandled Angular exceptions to Faro.
    // Replaces Angular's default ErrorHandler which only logs to console.
    {
      provide: ErrorHandler,
      useClass: FaroErrorHandler,
    },
  ],
};
