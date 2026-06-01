// Runtime environment variables injected by docker-entrypoint.sh at container startup.
// This file is overwritten when the container starts — do NOT hardcode real values here.
// For local `ng serve` development the file is never loaded; faro.ts falls back to
// the Angular environment.ts values.
window.__ENV = {
  FARO_URL: '',       // replaced at startup: FARO_URL env var or default /faro/collect
  API_BASE_URL: '',   // replaced at startup: API_BASE_URL env var or default /api
};
