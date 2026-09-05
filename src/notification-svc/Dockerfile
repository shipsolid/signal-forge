# syntax=docker/dockerfile:1.7

FROM python:3.12-slim@sha256:520153e2deb359602c9cffd84e491e3431d76e7bf95a3255c9ce9433b76ab99a

WORKDIR /app

COPY requirements.txt .
# The local build path may expose the corporate CA as a BuildKit secret. The
# temporary combined bundle exists only for this install command; CI omits it.
RUN --mount=type=secret,id=corporate_ca,required=false \
    if [ -s /run/secrets/corporate_ca ]; then \
      ca_bundle="$(mktemp)"; \
      cat /etc/ssl/certs/ca-certificates.crt /run/secrets/corporate_ca > "$ca_bundle"; \
      PIP_CERT="$ca_bundle" pip install --no-cache-dir -r requirements.txt; \
      rm -f "$ca_bundle"; \
    else \
      pip install --no-cache-dir -r requirements.txt; \
    fi

COPY app/ ./app/

# Non-root UID 1000, matches k8s securityContext.runAsUser.
RUN groupadd --system --gid 1000 app \
 && useradd  --system --uid 1000 --gid 1000 --home /app --shell /sbin/nologin app \
 && chown -R 1000:1000 /app
USER 1000:1000

ENV PYTHONUNBUFFERED=1
ENV OTEL_LOGS_EXPORTER=none

EXPOSE 8000
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
