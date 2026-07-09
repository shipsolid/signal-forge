#!/usr/bin/env bash
# Validates that every Kustomize entrypoint under k8s/ still renders.
# Run by the `kustomize-build` pre-commit hook whenever a k8s/**/*.yaml
# file changes.
#
# k8s/overlays/prod deliberately has no committed prod.secrets.env (see
# root .gitignore and k8s/overlays/prod/kustomization.yaml's own comment) —
# `kubectl kustomize`/`apply -k` on it fails loudly by design rather than
# silently falling back to base's dev-placeholder DB credentials. To still
# validate prod's kustomization.yaml structure here, stage a throwaway copy
# of the committed prod.secrets.env.example for the duration of this check
# only, then remove it (trap-guarded) so the working tree — and the
# fail-closed behavior — is unchanged afterward.
set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

prod_secrets="k8s/overlays/prod/prod.secrets.env"
created_prod_secrets=false

cleanup() {
  if [ "$created_prod_secrets" = true ]; then
    rm -f "$prod_secrets"
  fi
}
trap cleanup EXIT

if [ ! -f "$prod_secrets" ]; then
  cp "${prod_secrets}.example" "$prod_secrets"
  created_prod_secrets=true
fi

status=0
for dir in k8s/base k8s/overlays/dev k8s/overlays/staging k8s/overlays/prod; do
  if ! kubectl kustomize "$dir" > /dev/null; then
    echo "::error::kubectl kustomize $dir failed to render" >&2
    status=1
  fi
done

exit "$status"
