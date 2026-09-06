"""Regression tests for immutable deployment and observability policy contracts.

These tests exercise failure boundaries that are easy to weaken with an
otherwise harmless workflow/template change: complete digest replacement,
secret removal, runtime telemetry identity, environment normalization, non-dev
ingress hardening, and rejection of incomplete releases.
"""

from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

import yaml


REPOSITORY = Path(__file__).resolve().parents[3]


def load_module(name: str, relative_path: str):
    """Load a CI script by path because ``scripts/ci`` is not a Python package."""

    spec = importlib.util.spec_from_file_location(name, REPOSITORY / relative_path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"cannot load {relative_path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


render_deployment = load_module("render_deployment", "scripts/ci/render_deployment.py")
validate_observability = load_module(
    "validate_observability", "scripts/ci/validate_observability.py"
)


class ObservabilityPolicyTests(unittest.TestCase):
    def test_repository_observability_assets_are_valid(self) -> None:
        # Use a disposable output path because validation emits the exact Alloy
        # file that the workflow later passes to the Alloy binary.
        with tempfile.TemporaryDirectory() as directory:
            validator = validate_observability.ObservabilityValidator(
                REPOSITORY, Path(directory)
            )
            validator.validate()
            self.assertTrue((Path(directory) / "config.alloy").is_file())

    def test_unbounded_metric_dimension_is_rejected(self) -> None:
        # This is the policy's fail-closed regression: parsing dimensions without
        # rejecting a request identifier would make the CI job falsely green.
        with self.assertRaises(validate_observability.ValidationError):
            validate_observability.validate_metric_dimensions(
                'dimension { name = "request_id" }'
            )


class DeploymentRendererTests(unittest.TestCase):
    def setUp(self) -> None:
        # Every service uses a recognizable but valid immutable reference. The
        # renderer must consume all four as one release, never infer a tag.
        images = {}
        for service in render_deployment.SERVICE_IMAGES:
            digest = "a" * 64
            repository = f"ghcr.io/shipsolid/signal-forge/{service}"
            images[service] = {
                "repository": repository,
                "digest": f"sha256:{digest}",
                "reference": f"{repository}@sha256:{digest}",
            }
        self.release = {
            "schema_version": 1,
            "git": {"commit": "b" * 40},
            "build": {"run_id": 12345},
            "images": images,
        }

    def _source(self, services: tuple[str, ...]) -> str:
        # Model the risky base conditions the production renderer is expected to
        # repair: an inherited staging label, placeholder Secret, local image,
        # Never pull policy, and a hostless ingress fallback.
        documents = [
            {
                "apiVersion": "v1",
                "kind": "Namespace",
                "metadata": {
                    "name": "otel-lab",
                    "labels": {"signal-forge.environment": "staging"},
                },
            },
            {
                "apiVersion": "v1",
                "kind": "Secret",
                "metadata": {"name": "db-secrets", "namespace": "otel-lab"},
                "data": {"PASSWORD": "unsafe-placeholder"},
            },
            {
                "apiVersion": "networking.k8s.io/v1",
                "kind": "Ingress",
                "metadata": {"name": "otel-lab-ingress", "namespace": "otel-lab"},
                "spec": {
                    "tls": [{"hosts": ["signal-forge.local"], "secretName": "tls"}],
                    "rules": [
                        {"host": "signal-forge.local", "http": {"paths": []}},
                        {"http": {"paths": []}},
                    ],
                },
            },
        ]
        for service in services:
            documents.append(
                {
                    "apiVersion": "apps/v1",
                    "kind": "Deployment",
                    "metadata": {"name": service, "namespace": "otel-lab"},
                    "spec": {
                        "template": {
                            "metadata": {"labels": {"app": service}},
                            "spec": {
                                "containers": [
                                    {
                                        "name": service,
                                        "image": f"{service}:local",
                                        "imagePullPolicy": "Never",
                                    }
                                ]
                            },
                        }
                    },
                }
            )
        return yaml.safe_dump_all(documents)

    def _renderer(
        self,
        environment: str = "dev",
        public_url: str = "http://localhost:8080",
    ):
        return render_deployment.DeploymentRenderer(
            release=json.loads(json.dumps(self.release)),
            namespace="otel-lab",
            environment=environment,
            otel_endpoint="http://alloy.monitoring.svc.cluster.local:4317",
            faro_url="https://faro.example.invalid/collect",
            api_base_url="/api",
            public_url=public_url,
        )

    def test_renders_all_images_by_digest_and_runtime_contracts(self) -> None:
        documents = self._renderer().render(
            self._source(tuple(render_deployment.SERVICE_IMAGES))
        )
        # Uploaded/applied plans must remain secret-free; environment-scoped CD
        # steps create the stable Secret separately from protected values.
        self.assertFalse(any(doc.get("kind") == "Secret" for doc in documents))

        deployments = [doc for doc in documents if doc.get("kind") == "Deployment"]
        for deployment in deployments:
            container = deployment["spec"]["template"]["spec"]["containers"][0]
            self.assertIn("@sha256:", container["image"])
            self.assertEqual("IfNotPresent", container["imagePullPolicy"])

        configmaps = {
            doc["metadata"]["name"]: doc
            for doc in documents
            if doc.get("kind") == "ConfigMap"
        }
        resource_attributes = configmaps["signal-forge-app-env"]["data"][
            "OTEL_RESOURCE_ATTRIBUTES"
        ]
        self.assertIn("service.version=" + "b" * 40, resource_attributes)
        self.assertIn("deployment.environment=signal-forge-dev", resource_attributes)
        self.assertIn("frontend-env-js", configmaps)
        self.assertIn(
            "localhost", configmaps["signal-forge-app-env"]["data"]["AllowedHosts"]
        )
        for document in documents:
            labels = document.get("metadata", {}).get("labels", {})
            if "signal-forge.environment" in labels:
                self.assertEqual("dev", labels["signal-forge.environment"])

    def test_qa_ingress_is_host_scoped_and_tls_only(self) -> None:
        # QA reuses the historical staging overlay, so this test proves the
        # renderer removes local catch-all routing and applies the actual target.
        documents = self._renderer(
            environment="qa", public_url="https://qa.signal-forge.example"
        ).render(self._source(tuple(render_deployment.SERVICE_IMAGES)))
        ingress = next(doc for doc in documents if doc.get("kind") == "Ingress")
        self.assertEqual(
            ["qa.signal-forge.example"], ingress["spec"]["tls"][0]["hosts"]
        )
        self.assertEqual(1, len(ingress["spec"]["rules"]))
        self.assertEqual(
            "qa.signal-forge.example", ingress["spec"]["rules"][0]["host"]
        )
        self.assertEqual(
            "websecure",
            ingress["metadata"]["annotations"][
                "traefik.ingress.kubernetes.io/router.entrypoints"
            ],
        )

    def test_missing_release_workload_is_rejected(self) -> None:
        # A syntactically valid partial render is still unsafe: deployment and
        # rollback are defined over the complete four-service release set.
        with self.assertRaises(render_deployment.RenderError):
            self._renderer().render(self._source(("gateway-api",)))


if __name__ == "__main__":
    unittest.main()
