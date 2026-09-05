#!/usr/bin/env python3
"""Validate Signal Forge observability assets and render Alloy test input."""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from string import Template
from typing import Any

import yaml


BACKEND_SERVICES = ("gateway-api", "order-api", "notification-svc")
REQUIRED_RESOURCE_ATTRIBUTES = {
    "service.namespace",
    "service.version",
    "deployment.environment",
}
UNBOUNDED_METRIC_DIMENSIONS = {
    "email",
    "enduser.id",
    "http.target",
    "request_id",
    "session_id",
    "timestamp",
    "trace_id",
    "url.full",
    "user_id",
}
DIMENSION_PATTERN = re.compile(r'dimension\s*\{\s*name\s*=\s*"([^"]+)"\s*\}')


class ValidationError(RuntimeError):
    """Raised when an observability release contract is invalid."""


def _alloy_fragment_code(fragment: str) -> list[str]:
    lines = fragment.rstrip("\n").splitlines()
    try:
        first_code_line = next(
            index for index, line in enumerate(lines) if line.lstrip().startswith("stage.")
        )
    except StopIteration as exc:
        raise ValidationError("shared Alloy fragment contains no stage block") from exc
    return lines[first_code_line:]


def validate_metric_dimensions(alloy_config: str) -> set[str]:
    dimensions = set(DIMENSION_PATTERN.findall(alloy_config))
    forbidden = sorted(dimensions & UNBOUNDED_METRIC_DIMENSIONS)
    if forbidden:
        raise ValidationError(
            "unbounded metric dimensions are forbidden: " + ", ".join(forbidden)
        )
    return dimensions


class ObservabilityValidator:
    def __init__(self, repository: Path, output_dir: Path) -> None:
        self.repository = repository
        self.output_dir = output_dir

    def validate(self) -> None:
        self.output_dir.mkdir(parents=True, exist_ok=True)
        self._validate_telemetry_contract()
        local_alloy = self._render_local_alloy()
        validate_metric_dimensions(local_alloy)
        self._render_helm_values()
        self._validate_dashboards()
        self._validate_required_assets()

    def _read(self, relative_path: str) -> str:
        path = self.repository / relative_path
        try:
            return path.read_text(encoding="utf-8")
        except OSError as exc:
            raise ValidationError(f"cannot read {relative_path}: {exc}") from exc

    def _validate_telemetry_contract(self) -> None:
        rendered = Template(self._read("k8s/infra/app-env.yaml.tmpl")).substitute(
            APP_NAMESPACE="otel-lab",
            HELM_NAMESPACE="monitoring",
            HELM_RELEASE="grafana-k8s",
            DEPLOYMENT_ENVIRONMENT="signal-forge-ci",
        )
        try:
            app_env = yaml.safe_load(rendered)
        except yaml.YAMLError as exc:
            raise ValidationError(f"app telemetry ConfigMap is invalid YAML: {exc}") from exc

        data = (app_env or {}).get("data") or {}
        attributes = data.get("OTEL_RESOURCE_ATTRIBUTES", "")
        attribute_names = {
            item.split("=", 1)[0] for item in attributes.split(",") if "=" in item
        }
        missing = sorted(REQUIRED_RESOURCE_ATTRIBUTES - attribute_names)
        if missing:
            raise ValidationError(
                "OTEL_RESOURCE_ATTRIBUTES is missing: " + ", ".join(missing)
            )

        for service in BACKEND_SERVICES:
            deployment_path = f"k8s/app/{service.removesuffix('-api')}/deployment.yaml"
            if service == "notification-svc":
                deployment_path = "k8s/app/notification/deployment.yaml"
            try:
                deployment = yaml.safe_load(self._read(deployment_path))
                container = deployment["spec"]["template"]["spec"]["containers"][0]
            except (KeyError, IndexError, TypeError, yaml.YAMLError) as exc:
                raise ValidationError(f"invalid Deployment telemetry contract: {service}") from exc

            env = {item.get("name"): item.get("value") for item in container.get("env", [])}
            if env.get("OTEL_SERVICE_NAME") != service:
                raise ValidationError(f"{service} OTEL_SERVICE_NAME does not match its identity")
            config_refs = {
                item.get("configMapRef", {}).get("name")
                for item in container.get("envFrom", [])
            }
            if "signal-forge-app-env" not in config_refs:
                raise ValidationError(f"{service} does not consume signal-forge-app-env")

    def _render_local_alloy(self) -> str:
        fragment = self._read(
            "k8s/monitoring/grafana/shared/trace-correlation-stages.alloy"
        )
        code = _alloy_fragment_code(fragment)
        indented = "\n".join(("      " + line if line else line) for line in code)
        rendered = Template(
            self._read("k8s/monitoring/grafana/local/configmap.yaml.tmpl")
        ).substitute(
            TRACE_CORRELATION_STAGES=indented,
            DEPLOYMENT_ENVIRONMENT="signal-forge-ci",
        )
        try:
            configmap = yaml.safe_load(rendered)
            alloy_config = configmap["data"]["config.river"]
        except (KeyError, TypeError, yaml.YAMLError) as exc:
            raise ValidationError(f"rendered local Alloy ConfigMap is invalid: {exc}") from exc

        (self.output_dir / "config.alloy").write_text(alloy_config, encoding="utf-8")
        return alloy_config

    def _render_helm_values(self) -> None:
        local_rendered = Template(
            self._read("k8s/monitoring/grafana-helm/values-local.yaml.tmpl")
        ).substitute(DEPLOYMENT_ENVIRONMENT="signal-forge-ci")
        self._write_yaml("values-local.yaml", local_rendered)

        fragment = self._read(
            "k8s/monitoring/grafana/shared/trace-correlation-stages.alloy"
        )
        escaped_lines = [
            line.replace("{{", "\x00")
            .replace("}}", '{{"}}"}}')
            .replace("\x00", '{{"{{"}}')
            for line in _alloy_fragment_code(fragment)
        ]
        escaped_fragment = "\n".join(
            ("    " + line if line else line) for line in escaped_lines
        )
        cloud_rendered = Template(
            self._read("k8s/monitoring/grafana-helm/values-cloud.yaml.tmpl")
        ).substitute(
            CLUSTER_NAME="signal-forge-ci",
            DEPLOYMENT_ENVIRONMENT="signal-forge-ci",
            SECRET_NAME="grafana-cloud-secrets",
            SECRET_NAMESPACE="monitoring",
            MIMIR_URL="https://prometheus.example.invalid/api/prom/push",
            LOKI_URL="https://logs.example.invalid/loki/api/v1/push",
            TEMPO_ENDPOINT="tempo.example.invalid:443",
            TRACE_CORRELATION_STAGES_ESCAPED=escaped_fragment,
            ALLOY_LOGS_CA_CONTROLLER_BLOCK="",
            ALLOY_LOGS_CA_MOUNTS_BLOCK="",
        )
        self._write_yaml("values-cloud.yaml", cloud_rendered)

    def _write_yaml(self, filename: str, content: str) -> None:
        try:
            document: Any = yaml.safe_load(content)
        except yaml.YAMLError as exc:
            raise ValidationError(f"rendered {filename} is invalid YAML: {exc}") from exc
        if not isinstance(document, dict):
            raise ValidationError(f"rendered {filename} must be a YAML object")
        (self.output_dir / filename).write_text(content, encoding="utf-8")

    def _validate_dashboards(self) -> None:
        dashboard_dir = (
            self.repository / "k8s/monitoring/local/grafana/provisioning/dashboards"
        )
        dashboards = sorted(dashboard_dir.glob("*.json"))
        if not dashboards:
            raise ValidationError("no Grafana dashboards were found")
        for dashboard in dashboards:
            try:
                document = json.loads(dashboard.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError) as exc:
                raise ValidationError(f"invalid Grafana dashboard {dashboard.name}: {exc}") from exc
            if not document.get("title") or not isinstance(document.get("panels"), list):
                raise ValidationError(f"dashboard {dashboard.name} lacks title or panels")

    def _validate_required_assets(self) -> None:
        required = (
            "k8s/monitoring/slo-rules.yaml",
            "k8s/monitoring/grafana-helm/values-cloud.yaml.tmpl",
            "k8s/monitoring/grafana-helm/values-local.yaml.tmpl",
            "docs/operations/runbooks.md",
        )
        missing = [path for path in required if not (self.repository / path).is_file()]
        if missing:
            raise ValidationError("required observability assets are missing: " + ", ".join(missing))


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", type=Path, default=Path.cwd())
    parser.add_argument("--output-dir", type=Path, required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        ObservabilityValidator(args.repository.resolve(), args.output_dir.resolve()).validate()
    except (OSError, ValidationError) as exc:
        print(f"observability-policy: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
