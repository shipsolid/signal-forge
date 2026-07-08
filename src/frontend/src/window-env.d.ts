// Ambient global type for window.__ENV. In K8s, assets/env.js is a ConfigMap
// mounted over the image's baked-in default (see deploy-local.sh's
// apply_frontend_env_configmap() for the source of truth).
// A standalone .d.ts with no top-level import/export, so tsconfig's
// "include": ["src/**/*.d.ts"] picks it up for every compilation unit — including
// each spec file's isolated ts-jest transpilation, where a `declare global` living
// inside a regular imported .ts module (as this used to, in faro.ts) is only visible
// to files that actually import that module.
export {};

declare global {
  interface Window {
    __ENV?: {
      FARO_URL?: string;
      API_BASE_URL?: string;
    };
  }
}
