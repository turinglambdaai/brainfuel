#!/usr/bin/env bash
# Release version preflight: VERSION == rivet.rktd == (optional) tag.
# Runs in CI on every push and in the release pipeline on every tag.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="$(tr -d '[:space:]' < "$ROOT/VERSION")"
TAG="${1:-}"

fail() {
  echo "release preflight: $*" >&2
  exit 1
}

[[ -n "$VERSION" ]] || fail "VERSION is empty"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$ ]] || \
  fail "VERSION '$VERSION' is not a supported semantic version"

if [[ -n "$TAG" ]]; then
  TAG_VERSION="${TAG#v}"
  [[ "$TAG_VERSION" == "$VERSION" ]] || \
    fail "tag '$TAG' does not match VERSION '$VERSION'"
fi

# The Rivet app manifest carries the product version on this tree. Match
# the explicit version field — the manifest also holds multi-component
# min-version strings that a "first semver" heuristic could trip over.
RIVET_RKTD_VERSION="$(grep -o '(version \.[[:space:]]*"[^"]*"' "$ROOT/rivet.rktd" | head -n1 | sed 's/.*"\(.*\)"/\1/')"
[[ -n "$RIVET_RKTD_VERSION" ]] || fail "rivet.rktd has no version field"
[[ "$RIVET_RKTD_VERSION" == "$VERSION" ]] || \
  fail "rivet.rktd version '$RIVET_RKTD_VERSION' does not match VERSION '$VERSION'"

# The backend updater embeds the release identity for the update feed
# (the taskly family preflight: VERSION == rivet.rktd == updater).
grep -qF "(define app-version \"$VERSION\")" "$ROOT/racket/brainfuel/updater.rkt" || \
  fail "racket/brainfuel/updater.rkt app-version does not match VERSION '$VERSION'"

echo "release preflight: version $VERSION is aligned (VERSION == rivet.rktd == updater$( [[ -n "$TAG" ]] && echo ' == tag' ))"
