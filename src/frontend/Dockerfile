FROM node:20-alpine@sha256:fb4cd12c85ee03686f6af5362a0b0d56d50c58a04632e6c0fb8363f609372293 AS build
WORKDIR /app

# Corporate CA (Zscaler) trust. Build contexts always stage a zcert.crt
# (real cert on corporate machines, empty placeholder otherwise) so this COPY
# never breaks. Avoid apk here: if TLS is already intercepted, apk itself may
# fail before the extra CA is trusted. Node can use an appended PEM bundle.
COPY zcert.crt /usr/local/share/ca-certificates/zcert.crt
RUN if [ -s /usr/local/share/ca-certificates/zcert.crt ]; then \
      cat /etc/ssl/cert.pem /usr/local/share/ca-certificates/zcert.crt > /etc/ssl/certs/node-extra-ca-certificates.pem; \
    else \
      cp /etc/ssl/cert.pem /etc/ssl/certs/node-extra-ca-certificates.pem; \
    fi
RUN rm -f /usr/local/share/ca-certificates/zcert.crt
ENV NODE_EXTRA_CA_CERTS=/etc/ssl/certs/node-extra-ca-certificates.pem

COPY package*.json ./
RUN npm ci
COPY . .
# FARO_API_KEY is optional — pass via `docker build --build-arg FARO_API_KEY=...`
# (or `make import`) to enable source map upload at build time.
# If absent, the webpack FaroSourceMapUploaderPlugin is skipped silently.
ARG FARO_API_KEY
ENV FARO_API_KEY=${FARO_API_KEY}
RUN npm run build

FROM nginxinc/nginx-unprivileged:alpine@sha256:ede1d5af209ef9d29c902e0775ae850ce231d810a6980b1762f81110e757aa96 AS runtime
# nginx-unprivileged runs as UID 101 and binds 8080 by default.
# Our nginx.conf already listens on 8080.
USER root
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
