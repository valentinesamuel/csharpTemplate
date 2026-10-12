#!/usr/bin/env bash
# Documentation drift check (plan P24). Fails when a Markdown document points at something that does not exist:
#   1. relative links and images, inline `[text](target)` and reference definitions `[id]: target`;
#   2. backticked repository paths, for example `src/Host/Acme.Service.Host/Program.cs` or `deploy/compose/`.
# External links (scheme://, mailto:) and pure in-page anchors (#...) are not checked. Fenced code blocks are skipped.
# A backticked path is checked when it has no whitespace and starts with a top-level repository directory (src/,
# tests/, docs/, deploy/, scripts/, .github/, .config/) or is one of the root files listed below. Spans containing a
# placeholder (<...>, {...}, ...) are treated as patterns and skipped; spans containing * are expanded as globs and must
# match at least one path.
#
# Usage: bash scripts/ci/check-doc-links.sh [file-or-directory ...]
#   Default targets: README.md and docs/. Exit 0 when nothing is broken, 1 otherwise, 2 on bad usage.
set -euo pipefail

repo_root="${DOC_LINKS_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
cd "${repo_root}"

if [[ $# -eq 0 ]]; then
  set -- README.md docs
fi

files=()
for target in "$@"; do
  if [[ -d "${target}" ]]; then
    while IFS= read -r file; do
      files+=("${file}")
    done < <(find "${target}" -type f -name '*.md' | LC_ALL=C sort)
  elif [[ -f "${target}" ]]; then
    files+=("${target}")
  else
    echo "error: ${target} does not exist" >&2
    exit 2
  fi
done

if [[ ${#files[@]} -eq 0 ]]; then
  echo "error: no Markdown files found" >&2
  exit 2
fi

# Emits "<line>\t<kind>\t<target>" for every link target (kind=link) and code span (kind=code) outside fenced blocks.
# Portable awk (BSD and GNU): only match(), substr() and index().
extract() {
  awk '
    BEGIN { fence = "" }
    {
      line = $0
      if (fence == "" && match(line, /^[ ]{0,3}(```+|~~~+)/)) {
        fence = substr(line, RSTART, RLENGTH); sub(/^[ ]+/, "", fence); fence = substr(fence, 1, 3)
        next
      }
      if (fence != "") {
        if (match(line, /^[ ]{0,3}(```+|~~~+)[ ]*$/) && index(line, fence) > 0) { fence = "" }
        next
      }

      # Reference definitions: [id]: target
      if (match(line, /^[ ]{0,3}\[[^]]+\]:[ ]*[^ ]+/)) {
        def = substr(line, RSTART, RLENGTH); sub(/^[ ]{0,3}\[[^]]+\]:[ ]*/, "", def)
        printf "%d\tlink\t%s\n", NR, def
      }

      # Code spans first, then remove them so link syntax inside code is not treated as a link.
      rest = line; stripped = ""
      while (match(rest, /`[^`]+`/)) {
        printf "%d\tcode\t%s\n", NR, substr(rest, RSTART + 1, RLENGTH - 2)
        stripped = stripped substr(rest, 1, RSTART - 1) " "
        rest = substr(rest, RSTART + RLENGTH)
      }
      stripped = stripped rest

      # Inline links and images: ](target) or ](target "title")
      while (match(stripped, /\]\([^)]*\)/)) {
        target = substr(stripped, RSTART + 2, RLENGTH - 3)
        sub(/^[ ]+/, "", target); sub(/[ ]+"[^"]*"[ ]*$/, "", target); sub(/[ ]+$/, "", target)
        if (target ~ /^<.*>$/) { target = substr(target, 2, length(target) - 2) }
        printf "%d\tlink\t%s\n", NR, target
        stripped = substr(stripped, RSTART + RLENGTH)
      }
    }
  ' "$1"
}

root_files=" README.md Dockerfile global.json nuget.config Directory.Build.props Directory.Packages.props Acme.Service.slnx .editorconfig .gitignore .gitattributes .dockerignore .gitleaks.toml .markdownlint.jsonc Taskfile.yml "

checked=0
broken=0

report() {
  echo "BROKEN ${1}:${2}: ${3} '${4}'"
  broken=$((broken + 1))
}

for file in "${files[@]}"; do
  file_dir="$(dirname "${file}")"
  while IFS=$'\t' read -r line kind target; do
    [[ -z "${target}" ]] && continue
    if [[ "${kind}" == "link" ]]; then
      case "${target}" in
        *://* | mailto:* | tel:* | '#'*) continue ;;
      esac
      path="${target%%#*}"
      path="${path%%\?*}"
      [[ -z "${path}" ]] && continue
      if [[ "${path}" == /* ]]; then
        resolved="${repo_root}${path}"
      else
        resolved="${file_dir}/${path}"
      fi
      checked=$((checked + 1))
      [[ -e "${resolved}" ]] || report "${file}" "${line}" "link target not found" "${target}"
      continue
    fi

    # kind=code: only repository-looking paths.
    span="${target}"
    [[ "${span}" =~ [[:space:]] ]] && continue
    span="${span#./}"
    is_path=false
    case "${span}" in
      src/* | tests/* | docs/* | deploy/* | scripts/* | .github/* | .config/*) is_path=true ;;
      *) [[ "${root_files}" == *" ${span} "* ]] && is_path=true ;;
    esac
    [[ "${is_path}" == true ]] || continue
    case "${span}" in
      *'<'* | *'>'* | *'{'* | *'}'* | *'...'* | *'…'*) continue ;;
    esac
    span="${span%/}"
    checked=$((checked + 1))
    if [[ "${span}" == *'*'* ]]; then
      # shellcheck disable=SC2206 # intentional glob expansion
      matches=( ${span} )
      [[ -e "${matches[0]}" ]] || report "${file}" "${line}" "path pattern matches nothing" "${target}"
    else
      # Gitignored files that exist only after a local step (for example deploy/compose/.env) are valid references.
      [[ -e "${span}" ]] || git check-ignore -q -- "${span}" 2>/dev/null || report "${file}" "${line}" "repository path not found" "${target}"
    fi
  done < <(extract "${file}")
done

echo "check-doc-links: ${#files[@]} file(s), ${checked} reference(s) checked, ${broken} broken"
[[ ${broken} -eq 0 ]]
