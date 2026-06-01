#!/bin/sh
# Inject runtime environment variables into /assets/env.js before nginx starts.
#
# Variables consumed:
#   FARO_URL      — Grafana Alloy Faro receiver endpoint
#                   Default: /faro/collect  (proxied by nginx in-cluster)
#   API_BASE_URL  — Backend gateway API base URL
#                   Default: /api           (proxied by nginx in-cluster)
#
# In K8s the Deployment sets these from ConfigMap/Secret values so the same
# Docker image can target different environments without a rebuild.

# FARO_URL is injected via the grafana-cloud-secrets K8s Secret (FARO_COLLECTOR_URL key).
# No meaningful local fallback — leave empty so the Faro SDK skips transport
# when running without Grafana Cloud credentials.
FARO_URL="${FARO_URL:-}"
API_BASE_URL="${API_BASE_URL:-/api}"

cat > /usr/share/nginx/html/assets/env.js << EOF
window.__ENV = {
  FARO_URL: "${FARO_URL}",
  API_BASE_URL: "${API_BASE_URL}"
};
EOF

echo "[entrypoint] env.js written: FARO_URL=${FARO_URL}  API_BASE_URL=${API_BASE_URL}"

exec "$@"
