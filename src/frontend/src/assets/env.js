// Dev-time placeholder only — never loaded during `ng serve` (faro.ts falls back
// to the Angular environment.ts values there). In the built image this file is
// overwritten at Docker build time with the real baked-in default, and in K8s
// that's further shadowed by a mounted ConfigMap (see
// deploy-local.sh's apply_frontend_env_configmap()) — do NOT hardcode real
// values here.
window.__ENV = {
  FARO_URL: '',
  API_BASE_URL: '',
};
