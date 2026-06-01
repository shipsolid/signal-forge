import { ApplicationConfig, APP_INITIALIZER, ErrorHandler } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { routes } from './app.routes';
import { initFaro } from './telemetry/faro';
import { FaroErrorHandler } from './telemetry/faro-error-handler';

export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(routes),
    provideHttpClient(),

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
