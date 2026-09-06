#!/usr/bin/env python3
"""Render one environment plan from Kustomize and an immutable CI release.

Kustomize owns the environment's resource topology; the CI release manifest
owns deployable image identity. This renderer joins those inputs by replacing
only the four ``:local`` application markers with GHCR digest references,
removing repository placeholder Secrets, and appending runtime ConfigMaps.

The output is deliberately secret-free and contains no image tag promotion.
All four service markers and the target Namespace must be present or rendering
fails, preventing a partial microservice release from reaching ``kubectl``.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

import yaml


SERVICE_IMAGES = {
    "otel-frontend": "otel-frontend",
    "gateway-api": "gateway-api",
    "order-api": "order-api",
    "notification-svc": "notification-svc",
}
# A local marker is an explicit handoff point, not a general-purpose image
# rewrite. Images for datastores and observability components remain owned by
# their manifests and cannot be redirected by release metadata.
LOCAL_IMAGE_PATTERN = re.compile(r"^[a-z0-9][a-z0-9._-]*:local$")
# Restrict releases to this product's GHCR namespace and full sha256 digests.
# Accepting arbitrary registries/tags here would let a well-formed but malicious
# manifest bypass the repository checks already performed in the workflows.
DIGEST_REFERENCE_PATTERN = re.compile(
    r"^ghcr\.io/[a-z0-9._-]+/signal-forge/[a-z0-9._-]+@sha256:[0-9a-f]{64}$"
)
ENVIRONMENT_PATTERN = re.compile(r"^[a-z][a-z0-9-]{1,31}$")


class RenderError(RuntimeError):
    """Raised when release metadata and deployment manifests do not agree."""


class DeploymentRenderer:
    """Apply immutable-release and environment contracts to Kustomize output."""

    def __init__(
        self,
        release: dict[str, Any],
        namespace: str,
        environment: str,
        otel_endpoint: str,
        faro_url: str,
        api_base_url: str,
        public_url: str,
    ) -> None:
        self.release = release
        self.namespace = namespace
        self.environment = environment
        self.otel_endpoint = otel_endpoint
        self.faro_url = faro_url
        self.api_base_url = api_base_url
        self.public_url = public_url
        self.git_sha = self._required_string(release, "git", "commit")
        self.run_id = str(self._required_value(release, "build", "run_id"))
        self.image_references = self._validate_release_images()

        # Inputs later become labels, URLs, Kubernetes names, or process
        # environment. Fail once at the boundary rather than emitting a plan
        # whose error appears only during admission or application startup.
        if not ENVIRONMENT_PATTERN.fullmatch(environment):
            raise RenderError(f"invalid environment name: {environment!r}")
        if not namespace or len(namespace) > 63:
            raise RenderError("namespace must be a non-empty Kubernetes name")
        if not otel_endpoint.startswith(("http://", "https://")):
            raise RenderError("OTLP endpoint must use http:// or https://")
        if not api_base_url.startswith(("/", "http://", "https://")):
            raise RenderError("API base URL must be an absolute URL or root-relative path")

        parsed_public_url = urlparse(public_url)
        if not parsed_public_url.hostname or parsed_public_url.scheme not in {"http", "https"}:
            raise RenderError("public URL must include an http:// or https:// hostname")
        if environment in {"qa", "prod"} and parsed_public_url.scheme != "https":
            raise RenderError(f"{environment} public URL must use HTTPS")
        self.public_host = parsed_public_url.hostname

    @staticmethod
    def _required_value(document: dict[str, Any], *path: str) -> Any:
        value: Any = document
        for key in path:
            if not isinstance(value, dict) or key not in value:
                raise RenderError(f"release manifest is missing {'.'.join(path)}")
            value = value[key]
        return value

    @classmethod
    def _required_string(cls, document: dict[str, Any], *path: str) -> str:
        value = cls._required_value(document, *path)
        if not isinstance(value, str) or not value:
            raise RenderError(f"release manifest field {'.'.join(path)} must be a string")
        return value

    def _validate_release_images(self) -> dict[str, str]:
        """Return the complete service-to-digest map or reject the release."""

        if not re.fullmatch(r"[0-9a-f]{40}", self.git_sha):
            raise RenderError("release git.commit must be a full 40-character SHA")

        images = self._required_value(self.release, "images")
        if not isinstance(images, dict):
            raise RenderError("release manifest images must be an object")

        references: dict[str, str] = {}
        for service in SERVICE_IMAGES:
            reference = self._required_string(images, service, "reference")
            if not DIGEST_REFERENCE_PATTERN.fullmatch(reference):
                raise RenderError(f"{service} is not an immutable GHCR digest reference")
            expected_suffix = f"/signal-forge/{service}@"
            if expected_suffix not in reference:
                raise RenderError(f"{service} release reference points at the wrong repository")
            references[service] = reference
        return references

    def render(self, source: str) -> list[dict[str, Any]]:
        """Transform a multi-document Kustomize stream into a deployable plan."""

        try:
            documents = [doc for doc in yaml.safe_load_all(source) if doc is not None]
        except yaml.YAMLError as exc:
            raise RenderError(f"Kustomize output is invalid YAML: {exc}") from exc

        rendered: list[dict[str, Any]] = []
        replaced: set[str] = set()
        namespace_seen = False

        for document in documents:
            if not isinstance(document, dict):
                raise RenderError("Kustomize emitted a non-object YAML document")

            self._normalize_environment_labels(document)
            kind = document.get("kind")
            metadata = document.get("metadata") or {}
            name = metadata.get("name")

            if kind == "Namespace" and name == self.namespace:
                namespace_seen = True

            # Real secrets are created from GitHub Environment secrets in CD.
            # Dropping both base placeholders here prevents an uploaded plan or
            # later `kubectl apply` from exposing/overwriting protected values.
            if kind == "Secret" and name in {"db-secrets", "grafana-cloud-secrets"}:
                continue

            if kind == "Deployment":
                self._render_deployment(document, replaced)
            elif kind == "Ingress" and name == "otel-lab-ingress":
                self._render_ingress(document)

            rendered.append(document)

        # Completeness is an atomic release invariant: promotion and rollback
        # operate on the same four-image set, never an accidental subset.
        missing = sorted(set(SERVICE_IMAGES) - replaced)
        if missing:
            raise RenderError(f"Kustomize output did not contain app images: {', '.join(missing)}")
        if not namespace_seen:
            raise RenderError(f"Kustomize output does not define namespace {self.namespace}")

        rendered.extend(self._runtime_configmaps())
        return rendered

    def _normalize_environment_labels(self, document: dict[str, Any]) -> None:
        """Replace inherited environment labels at every manifest depth.

        QA intentionally reuses the historical ``staging`` Kustomize overlay.
        Recursive normalization covers object metadata, pod templates, and any
        nested selectors so telemetry and inventory consistently report ``qa``.
        """

        stack: list[Any] = [document]
        while stack:
            value = stack.pop()
            if isinstance(value, dict):
                labels = value.get("labels")
                if (
                    isinstance(labels, dict)
                    and "signal-forge.environment" in labels
                ):
                    labels["signal-forge.environment"] = self.environment
                stack.extend(value.values())
            elif isinstance(value, list):
                stack.extend(value)

    def _render_ingress(self, document: dict[str, Any]) -> None:
        """Keep local HTTP behavior in dev and enforce host-scoped TLS elsewhere."""

        if self.environment == "dev":
            return

        try:
            spec = document["spec"]
            hosted_rules = [rule for rule in spec["rules"] if rule.get("host")]
            tls = spec["tls"]
        except (KeyError, TypeError) as exc:
            raise RenderError("application Ingress is missing rules or TLS configuration") from exc
        if len(hosted_rules) != 1 or not isinstance(tls, list) or not tls:
            raise RenderError("application Ingress must contain one host rule and TLS entry")

        # The base ingress includes a hostless convenience route for k3d. Remove
        # that catch-all outside dev so QA/PROD cannot accept unintended hosts.
        hosted_rules[0]["host"] = self.public_host
        spec["rules"] = hosted_rules
        tls[0]["hosts"] = [self.public_host]

        annotations = document.setdefault("metadata", {}).setdefault("annotations", {})
        annotations["traefik.ingress.kubernetes.io/router.entrypoints"] = "websecure"
        annotations["traefik.ingress.kubernetes.io/router.tls"] = "true"

    def _render_deployment(
        self, document: dict[str, Any], replaced: set[str]
    ) -> None:
        """Replace one local app image marker and stamp release provenance."""

        metadata = document.setdefault("metadata", {})
        annotations = metadata.setdefault("annotations", {})
        annotations["signal-forge.io/git-sha"] = self.git_sha
        annotations["signal-forge.io/ci-run-id"] = self.run_id

        try:
            pod_template = document["spec"]["template"]
            pod_labels = pod_template.setdefault("metadata", {}).setdefault("labels", {})
            containers = pod_template["spec"]["containers"]
        except (KeyError, TypeError) as exc:
            raise RenderError(f"Deployment {metadata.get('name')} has an invalid pod template") from exc

        if not isinstance(containers, list):
            raise RenderError(f"Deployment {metadata.get('name')} containers must be a list")

        for container in containers:
            if not isinstance(container, dict):
                continue
            image = container.get("image")
            if not isinstance(image, str) or not LOCAL_IMAGE_PATTERN.fullmatch(image):
                continue

            service = image.removesuffix(":local")
            if service not in self.image_references:
                raise RenderError(f"no release image is defined for local image {image}")
            if service in replaced:
                raise RenderError(f"local image {image} occurs more than once")

            container["image"] = self.image_references[service]
            # Digest references are immutable, so IfNotPresent can safely reuse
            # an already-cached byte-identical image while still allowing a new
            # digest to be pulled from GHCR. `Never` is only valid for local k3d.
            container["imagePullPolicy"] = "IfNotPresent"
            pod_labels["app.kubernetes.io/version"] = self.git_sha
            replaced.add(service)

    def _runtime_configmaps(self) -> list[dict[str, Any]]:
        """Build non-secret runtime configuration for the unchanged images.

        Backend services consume common OpenTelemetry/host settings from
        ``signal-forge-app-env``. Nginx mounts ``frontend-env-js`` over the
        image's browser-visible default. Together they allow endpoints,
        environment, and release identity to change without rebuilding layers.
        """

        deployment_environment = f"signal-forge-{self.environment}"
        allowed_hosts = ";".join(
            (
                "gateway-api",
                f"gateway-api.{self.namespace}.svc.cluster.local",
                "order-api",
                f"order-api.{self.namespace}.svc.cluster.local",
                self.public_host,
            )
        )
        # The full Git SHA is the cross-signal service.version used by the CD
        # telemetry gate to distinguish the candidate from prior releases.
        resource_attributes = ",".join(
            (
                f"service.namespace={self.namespace}",
                f"service.version={self.git_sha}",
                f"deployment.environment={deployment_environment}",
            )
        )
        env_js = (
            "window.__ENV = {\n"
            f"  FARO_URL: {json.dumps(self.faro_url)},\n"
            f"  API_BASE_URL: {json.dumps(self.api_base_url)}\n"
            "};\n"
        )

        return [
            {
                "apiVersion": "v1",
                "kind": "ConfigMap",
                "metadata": {
                    "name": "signal-forge-app-env",
                    "namespace": self.namespace,
                    "labels": {"signal-forge.environment": self.environment},
                },
                "data": {
                    "AllowedHosts": allowed_hosts,
                    "OTEL_EXPORTER_OTLP_ENDPOINT": self.otel_endpoint,
                    "OTEL_EXPORTER_OTLP_PROTOCOL": "grpc",
                    "OTEL_LOGS_EXPORTER": "none",
                    "OTEL_METRICS_EXEMPLAR_FILTER": "trace_based",
                    "OTEL_RESOURCE_ATTRIBUTES": resource_attributes,
                },
            },
            {
                "apiVersion": "v1",
                "kind": "ConfigMap",
                "metadata": {
                    "name": "frontend-env-js",
                    "namespace": self.namespace,
                    "labels": {"signal-forge.environment": self.environment},
                },
                "data": {"env.js": env_js},
            },
        ]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--release-manifest", type=Path, required=True)
    parser.add_argument("--namespace", required=True)
    parser.add_argument("--environment", required=True)
    parser.add_argument("--otel-endpoint", required=True)
    parser.add_argument("--faro-url", default="")
    parser.add_argument("--api-base-url", default="/api")
    parser.add_argument("--public-url", required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        release = json.loads(args.release_manifest.read_text(encoding="utf-8"))
        renderer = DeploymentRenderer(
            release=release,
            namespace=args.namespace,
            environment=args.environment,
            otel_endpoint=args.otel_endpoint,
            faro_url=args.faro_url,
            api_base_url=args.api_base_url,
            public_url=args.public_url,
        )
        documents = renderer.render(args.input.read_text(encoding="utf-8"))
        args.output.write_text(
            yaml.safe_dump_all(documents, explicit_start=True, sort_keys=False),
            encoding="utf-8",
        )
    except (OSError, json.JSONDecodeError, RenderError) as exc:
        print(f"render-deployment: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
