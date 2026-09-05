from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

import yaml


REPOSITORY = Path(__file__).resolve().parents[3]


def load_module(name: str, relative_path: str):
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
        with tempfile.TemporaryDirectory() as directory:
            validator = validate_observability.ObservabilityValidator(
                REPOSITORY, Path(directory)
            )
            validator.validate()
            self.assertTrue((Path(directory) / "config.alloy").is_file())

    def test_unbounded_metric_dimension_is_rejected(self) -> None:
        with self.assertRaises(validate_observability.ValidationError):
            validate_observability.validate_metric_dimensions(
                'dimension { name = "request_id" }'
            )


class DeploymentRendererTests(unittest.TestCase):
    def setUp(self) -> None:
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
        documents = [
            {"apiVersion": "v1", "kind": "Namespace", "metadata": {"name": "otel-lab"}},
            {
                "apiVersion": "v1",
                "kind": "Secret",
                "metadata": {"name": "db-secrets", "namespace": "otel-lab"},
                "data": {"PASSWORD": "unsafe-placeholder"},
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

    def _renderer(self):
        return render_deployment.DeploymentRenderer(
            release=json.loads(json.dumps(self.release)),
            namespace="otel-lab",
            environment="dev",
            otel_endpoint="http://alloy.monitoring.svc.cluster.local:4317",
            faro_url="https://faro.example.invalid/collect",
            api_base_url="/api",
        )

    def test_renders_all_images_by_digest_and_runtime_contracts(self) -> None:
        documents = self._renderer().render(
            self._source(tuple(render_deployment.SERVICE_IMAGES))
        )
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

    def test_missing_release_workload_is_rejected(self) -> None:
        with self.assertRaises(render_deployment.RenderError):
            self._renderer().render(self._source(("gateway-api",)))


if __name__ == "__main__":
    unittest.main()
