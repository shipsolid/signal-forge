# syntax=docker/dockerfile:1.7

FROM node:20-alpine@sha256:fb4cd12c85ee03686f6af5362a0b0d56d50c58a04632e6c0fb8363f609372293 AS build
WORKDIR /app

# The local build path may expose the corporate CA as a BuildKit secret. CI
# omits it. NODE_EXTRA_CA_CERTS is scoped to npm so the CA is not persisted.
COPY package*.json ./
RUN --mount=type=secret,id=corporate_ca,required=false \
    if [ -s /run/secrets/corporate_ca ]; then \
      NODE_EXTRA_CA_CERTS=/run/secrets/corporate_ca npm ci; \
    else \
      npm ci; \
    fi
COPY . .
# FARO_API_KEY is optional — pass via `docker build --build-arg FARO_API_KEY=...`
# (or `make import`) to enable source map upload at build time.
# If absent, the webpack FaroSourceMapUploaderPlugin is skipped silently.
ARG FARO_API_KEY
ENV FARO_API_KEY=${FARO_API_KEY}
RUN npm run build

FROM nginxinc/nginx-unprivileged:alpine@sha256:aa8c9087d36d93e9d650c5365f883b421e8214aedbad24ade52b844c583358f1 AS runtime
# nginx-unprivileged runs as UID 101 and binds 8080 by default.
# Our nginx.conf already listens on 8080.
USER root
RUN --mount=type=secret,id=corporate_ca,required=false \
    if [ -s /run/secrets/corporate_ca ]; then \
      ca_bundle="$(mktemp)"; \
      cat /etc/ssl/certs/ca-certificates.crt /run/secrets/corporate_ca > "$ca_bundle"; \
      SSL_CERT_FILE="$ca_bundle" apk upgrade --no-cache; \
      rm -f "$ca_bundle"; \
    else \
      apk upgrade --no-cache; \
    fi
COPY --from=build /app/dist/otel-frontend /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
# Baked-in default env.js — used as-is for a bare `docker run` (no runtime
# config injection outside K8s). In-cluster, deploy-local.sh's
# frontend-env-js ConfigMap is mounted read-only over this exact path via
# `subPath`, shadowing this default with the real FARO_URL/API_BASE_URL — no
# entrypoint script needed, and the mount is compatible with
# readOnlyRootFilesystem: true (see k8s/app/frontend/deployment.yaml).
RUN printf 'window.__ENV = {\n  FARO_URL: "",\n  API_BASE_URL: "/api"\n};\n' \
      > /usr/share/nginx/html/assets/env.js \
    && chown -R 101:101 /usr/share/nginx/html
USER 101
EXPOSE 8080
ENTRYPOINT ["nginx", "-g", "daemon off;"]
