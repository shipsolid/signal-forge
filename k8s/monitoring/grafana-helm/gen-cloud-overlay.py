"""
gen-cloud-overlay.py — Generate Helm values overlay with Grafana Cloud destinations.

Outputs a YAML overlay to stdout that adds four Grafana Cloud destinations to the
grafana/k8s-monitoring chart.  Pass it to `helm upgrade -f values-local.yaml -f <this>`.

Two calling modes, selected by the MODE env var:

  MODE=akv   (called from secrets-fetch-akv — raw AKV values, paths not yet appended)
    TEMPO_HOST   — tempo host without scheme, e.g. tempo-prod-29-....grafana.net
    TEMPO_USER   — Tempo instance ID
    MIMIR_BASE   — raw Mimir URL,  e.g. https://prometheus-us-central2.grafana.net
    MIMIR_USER   — Mimir instance ID
    LOKI_BASE    — raw Loki URL,   e.g. https://logs-prod-037.grafana.net
    LOKI_USER    — Loki instance ID
    API_KEY      — shared glsa_... API key

  MODE=env   (called from secrets-apply — pre-formatted values from .env)
    GRAFANA_CLOUD_TEMPO_ENDPOINT  — already "host:443"
    GRAFANA_CLOUD_TEMPO_USER
    GRAFANA_CLOUD_MIMIR_ENDPOINT  — already "https://.../api/v1/otlp"
    GRAFANA_CLOUD_MIMIR_USER
    GRAFANA_CLOUD_LOKI_ENDPOINT   — already "https://.../loki/api/v1/push"
    GRAFANA_CLOUD_LOKI_USER
    GRAFANA_CLOUD_API_KEY

Destinations produced:
  grafana-cloud-traces        otlp/grpc  → Tempo             (traces only)
  grafana-cloud-metrics       prometheus → Mimir remote_write (app OTLP metrics converted by Alloy)
  grafana-cloud-infra-metrics prometheus → Mimir remote_write (scraped infra metrics from alloy-metrics)
  grafana-cloud-logs          loki       → Loki push          (logs from alloy-logs)

NOTE: prometheus-us-central2.grafana.net is a Prometheus remote_write endpoint, not an OTLP gateway.
Using type:otlp would cause a 404 on /api/v1/otlp/v1/metrics.  Both metrics destinations use
type:prometheus so Alloy converts OTLP app metrics → Prometheus format before remote_write.
"""

import os
import re
import sys

import yaml

mode = os.environ.get("MODE", "akv")

if mode == "akv":
    api_key = os.environ["API_KEY"]
    tempo_endpoint = os.environ["TEMPO_HOST"] + ":443"
    tempo_user = os.environ["TEMPO_USER"]
    mimir_otlp_url = os.environ["MIMIR_BASE"] + "/api/v1/otlp"
    mimir_prom_url = os.environ["MIMIR_BASE"] + "/api/prom/push"
    mimir_user = os.environ["MIMIR_USER"]
    loki_url = os.environ["LOKI_BASE"] + "/loki/api/v1/push"
    loki_user = os.environ["LOKI_USER"]
elif mode == "env":
    api_key = os.environ["GRAFANA_CLOUD_API_KEY"]
    tempo_endpoint = os.environ["GRAFANA_CLOUD_TEMPO_ENDPOINT"]
    tempo_user = os.environ["GRAFANA_CLOUD_TEMPO_USER"]
    mimir_otlp_url = os.environ["GRAFANA_CLOUD_MIMIR_ENDPOINT"]
    mimir_prom_url = re.sub(r"/api/v1/otlp$", "/api/prom/push", mimir_otlp_url)
    mimir_user = os.environ["GRAFANA_CLOUD_MIMIR_USER"]
    loki_url = os.environ["GRAFANA_CLOUD_LOKI_ENDPOINT"]
    loki_user = os.environ["GRAFANA_CLOUD_LOKI_USER"]
else:
    sys.exit(f"Unknown MODE={mode!r}. Set MODE=akv or MODE=env.")

destinations = [
    # Traces → Grafana Cloud Tempo (gRPC OTLP, traces only)
    {
        "name": "grafana-cloud-traces",
        "type": "otlp",
        "protocol": "grpc",
        "url": tempo_endpoint,
        "auth": {"type": "basic", "username": tempo_user, "password": api_key},
        "traces": {"enabled": True},
        "metrics": {"enabled": False},
        "logs": {"enabled": False},
        # extraLabels is a no-op on otlp-type destinations in this chart version —
        # only prometheus/loki/pyroscope destination templates implement it.
        "processors": {
            "transform": {
                "traces": {
                    "resource": [
                        'set(attributes["deployment_environment"], "signal-forge-dev")'
                    ]
                }
            }
        },
    },
    # App metrics → Grafana Cloud Mimir (Prometheus remote_write, converted from OTLP by Alloy)
    # NOTE: prometheus-us-central2.grafana.net is a Prometheus-compatible endpoint (remote_write),
    # not an OTLP gateway.  Using type:otlp here causes the chart to POST to /api/v1/otlp/v1/metrics
    # which returns 404.  Use type:prometheus (remote_write) instead — Alloy converts OTLP → Prometheus.
    #
    # openTelemetryConversion.resourceToTelemetryConversion: true — promotes all OTel resource
    # attributes (service.name, k8s.*) as Prometheus labels in Mimir so they are queryable
    # as service_name, k8s_deployment_name, etc. rather than only appearing in the job label.
    {
        "name": "grafana-cloud-metrics",
        "type": "prometheus",
        "url": mimir_prom_url,
        "auth": {"type": "basic", "username": mimir_user, "password": api_key},
        "openTelemetryConversion": {"resourceToTelemetryConversion": True},
        "extraLabels": {"deployment_environment": "signal-forge-dev"},
    },
    # Infra metrics → Grafana Cloud Mimir (Prometheus remote_write, scraped by alloy-metrics)
    {
        "name": "grafana-cloud-infra-metrics",
        "type": "prometheus",
        "url": mimir_prom_url,
        "auth": {"type": "basic", "username": mimir_user, "password": api_key},
        "openTelemetryConversion": {"resourceToTelemetryConversion": True},
        "extraLabels": {"deployment_environment": "signal-forge-dev"},
    },
    # Logs → Grafana Cloud Loki (pod/node logs tailed by alloy-logs)
    {
        "name": "grafana-cloud-logs",
        "type": "loki",
        "url": loki_url,
        "auth": {"type": "basic", "username": loki_user, "password": api_key},
        "extraLabels": {"deployment_environment": "signal-forge-dev"},
    },
]

print(yaml.dump({"destinations": destinations}, default_flow_style=False))
