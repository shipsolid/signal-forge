// Build-time fallback for a static production bundle. The frontend-env-js
// ConfigMap overrides endpoint values through window.__ENV at runtime, allowing
// the same immutable bundle in DEV, QA, and PROD. Browser-visible config is not
// a secret store; credentials must remain server-side/Environment-scoped.
export const environment = {
  production: true,
  apiBaseUrl: '/api',
  faroUrl: '/faro/collect',
};
