// Build-time defaults for `ng serve` and a bare local frontend. Kubernetes
// mounts window.__ENV from a ConfigMap at runtime, so CI/CD can change endpoint
// destinations without rebuilding this image. Never place a secret here or in
// env.js: both values are downloaded by every browser client.
export const environment = {
  production: false,
  apiBaseUrl: 'http://localhost/api',
  faroUrl: 'http://localhost:12347/collect',
};
