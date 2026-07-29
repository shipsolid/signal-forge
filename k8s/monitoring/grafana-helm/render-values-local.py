"""
render-values-local.py — Render values-local.yaml.tmpl's ${DEPLOYMENT_ENVIRONMENT}
placeholder for the legacy Makefile secrets-fetch-akv / secrets-apply targets.

deploy-local.sh's render_helm_values() covers this same substitution for its own
callers; this is the Makefile-only counterpart, since that pipeline doesn't source
deploy-local.sh's bash functions. Reads monitoring.deployment_environment directly
from conf.yml so both pipelines stay driven by the same single value.

Usage: python3 render-values-local.py > /tmp/values-local-rendered.yaml
"""

from pathlib import Path
from string import Template

import yaml

REPO_ROOT = Path(__file__).resolve().parents[3]
conf = yaml.safe_load((REPO_ROOT / "conf.yml").read_text()) or {}
deployment_environment = (conf.get("monitoring") or {}).get("deployment_environment") or "signal-forge-dev"

tmpl = (REPO_ROOT / "k8s/monitoring/grafana-helm/values-local.yaml.tmpl").read_text()
print(Template(tmpl).substitute(DEPLOYMENT_ENVIRONMENT=deployment_environment), end="")
