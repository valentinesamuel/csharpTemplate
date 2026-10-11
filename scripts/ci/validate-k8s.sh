#!/usr/bin/env bash
# Renders every Kustomize overlay (app and migration Job) and validates it with kubeconform (plan P21).
# Fails if any overlay does not render, does not validate, or contains a Secret object (INV-11).
# Requires: kubeconform, and kustomize or kubectl (falls back to `kubectl kustomize`).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
k8s_dir="${repo_root}/deploy/k8s"
envs=(dev qa prod)

if command -v kustomize >/dev/null 2>&1; then
  render() { kustomize build "$1"; }
elif command -v kubectl >/dev/null 2>&1; then
  render() { kubectl kustomize "$1"; }
else
  echo "error: neither kustomize nor kubectl is installed" >&2
  exit 1
fi

if ! command -v kubeconform >/dev/null 2>&1; then
  echo "error: kubeconform is not installed (https://github.com/yannh/kubeconform)" >&2
  exit 1
fi

failed=0
for tree in overlays migration/overlays; do
  for env in "${envs[@]}"; do
    overlay="${k8s_dir}/${tree}/${env}"
    echo "==> ${overlay#"${repo_root}/"}"

    if ! rendered="$(render "${overlay}")"; then
      echo "error: render failed" >&2
      failed=1
      continue
    fi

    if grep -Eq '^kind:[[:space:]]*Secret[[:space:]]*$' <<<"${rendered}"; then
      echo "error: rendered output contains a Secret object (INV-11: secrets are created out of band)" >&2
      failed=1
    fi

    if ! kubeconform -strict -summary -ignore-missing-schemas <<<"${rendered}"; then
      failed=1
    fi
  done
done

if [[ "${failed}" -ne 0 ]]; then
  echo "Kubernetes manifest validation FAILED" >&2
  exit 1
fi
echo "All Kubernetes overlays rendered and validated."
