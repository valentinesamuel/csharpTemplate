#!/usr/bin/env bash
# Lists vulnerable NuGet packages (direct and transitive) and fails on any High or Critical advisory (plan P22, WH-31).
# Low/Moderate advisories are reported but do not fail, matching the NuGetAudit policy in Directory.Packages.props
# (NU1901/NU1902 stay warnings; NU1903/NU1904 are errors).
#
# Usage: scripts/ci/check-vulnerable.sh [solution-or-project]   (default: Acme.Service.slnx at the repo root)
# Requires: dotnet SDK (packages restored), jq.
# Exit codes: 0 = no High/Critical, 1 = High/Critical found, 2 = the scan itself failed (fails closed).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
target="${1:-${repo_root}/Acme.Service.slnx}"

if ! command -v jq >/dev/null 2>&1; then
  echo "error: jq is not installed" >&2
  exit 2
fi

echo "==> dotnet list package --vulnerable --include-transitive (${target#"${repo_root}/"})"
if ! report="$(dotnet list "${target}" package --vulnerable --include-transitive --format json)"; then
  echo "error: 'dotnet list package --vulnerable' failed; is the solution restored?" >&2
  exit 2
fi

if ! jq -e '.projects' >/dev/null 2>&1 <<<"${report}"; then
  echo "error: could not parse the vulnerability report" >&2
  printf '%s\n' "${report}" >&2
  exit 2
fi

# Problems (for example an unreachable package source) mean the scan is incomplete: fail closed.
problems="$(jq -r '[.problems[]? | select((.level // "") | ascii_downcase == "error")] | .[] | "  \(.project // "-"): \(.text)"' <<<"${report}")"
if [[ -n "${problems}" ]]; then
  echo "error: the vulnerability scan reported problems:" >&2
  printf '%s\n' "${problems}" >&2
  exit 2
fi

# One line per (project, framework, package, advisory), de-duplicated.
findings="$(jq -r '
  .projects[]
  | (.path | split("/") | last) as $project
  | .frameworks[]?
  | .framework as $tfm
  | ((.topLevelPackages // []) | map(. + {kind: "direct"})) + ((.transitivePackages // []) | map(. + {kind: "transitive"}))
  | .[]
  | . as $pkg
  | .vulnerabilities[]?
  | [.severity, $pkg.id, $pkg.resolvedVersion, $pkg.kind, $project, $tfm, .advisoryurl]
  | @tsv' <<<"${report}" | sort -u)"

if [[ -z "${findings}" ]]; then
  echo "No vulnerable packages found."
  exit 0
fi

echo "Vulnerable packages (severity, package, version, kind, project, framework, advisory):"
printf '%s\n' "${findings}" | sed 's/^/  /'

blocking="$(awk -F'\t' 'tolower($1) == "high" || tolower($1) == "critical"' <<<"${findings}")"
if [[ -n "${blocking}" ]]; then
  echo "error: $(wc -l <<<"${blocking}" | tr -d ' ') High/Critical advisory finding(s). Upgrade or centrally pin the package in Directory.Packages.props." >&2
  exit 1
fi

echo "Only Low/Moderate advisories found; not blocking (WH-31)."
