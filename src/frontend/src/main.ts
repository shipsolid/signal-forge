import { bootstrapApplication } from '@angular/platform-browser';
import { AppComponent } from './app/app.component';
import { appConfig } from './app/app.config';

// Faro RUM is initialised via APP_INITIALIZER in app.config.ts — do not call
// initFaro() here.  Using APP_INITIALIZER ensures Faro is ready before any
// Angular component, service, or router guard executes.
bootstrapApplication(AppComponent, appConfig).catch((err) => console.error(err));
