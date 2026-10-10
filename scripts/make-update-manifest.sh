#!/usr/bin/env bash
# Build update-manifest.json for a release and sign it (shared/spec/UPDATE.md).
#
#   scripts/make-update-manifest.sh <tag> <dist-dir> [key-path]
#
#   <tag>       release tag, e.g. v1.2.0 (must match the VERSION file)
#   <dist-dir>  directory containing the release artifacts, i.e. the names
#               the release pipeline produces:
#                 brainfuel-<ver>-macos-arm64.zip   brainfuel-<ver>-macos-x64.zip
#                 brainfuel-<ver>-windows-x64.zip   brainfuel-<ver>-linux-x64.tar.gz
#   <key-path>  Ed25519 private key: PEM (update-keys.sh output) or raw DER
#               (base64-decoded UPDATE_ED25519_PRIVATE_KEY_B64, the format
#               the release legs already consume). Env fallbacks:
#               UPDATE_KEY_PATH, then <repo>/.keys/update-signing-key.pem
#
# Emits <dist-dir>/update-manifest.json — a single self-contained signed
# wrapper (schema + base64 payload + signature block): the family format
# every rivet/distribution client verifies, hosts included. The four
# PORTABLE artifacts are the update feed; dmg/msi are human installers and
# stay out of it. Requires racket with rivet linked (the release publish
# job installs the pinned checkout).
set -euo pipefail

TAG="${1:?usage: make-update-manifest.sh <tag> <dist-dir> [key-path]}"
DIST="${2:?usage: make-update-manifest.sh <tag> <dist-dir> [key-path]}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
KEY="${3:-${UPDATE_KEY_PATH:-$ROOT/.keys/update-signing-key.pem}}"

VERSION="${TAG#v}"
[[ "$VERSION" == "$(tr -d '[:space:]' < "$ROOT/VERSION")" ]] || {
  echo "error: tag $TAG does not match VERSION '$(cat "$ROOT/VERSION")'" >&2; exit 1; }

for artifact in "$DIST/brainfuel-$VERSION-macos-arm64.zip" \
                "$DIST/brainfuel-$VERSION-macos-x64.zip" \
                "$DIST/brainfuel-$VERSION-windows-x64.zip" \
                "$DIST/brainfuel-$VERSION-linux-x64.tar.gz"; do
  [[ -f "$artifact" ]] || { echo "error: missing $artifact" >&2; exit 1; }
done
[[ -f "$KEY" ]] || { echo "error: missing signing key $KEY (see scripts/update-keys.sh)" >&2; exit 1; }

BASE_URL="${RELEASE_ASSET_BASE_URL:-https://github.com/turinglambdaai/brainfuel/releases/download/$TAG}"

# PEM input needs OpenSSL 3 (macOS ships LibreSSL, which cannot do Ed25519);
# DER input (the CI secret shape) needs nothing at all.
KEY_DER="$(mktemp /tmp/brainfuel-key-XXXXXX.der)"
SCRIPT="$(mktemp /tmp/brainfuel-manifest-XXXXXX.rkt)"
trap 'rm -f "$SCRIPT" "$KEY_DER"' EXIT

if head -c 30 "$KEY" | grep -q "BEGIN"; then
  OPENSSL_BIN="${OPENSSL_BIN:-}"
  if [[ -z "$OPENSSL_BIN" ]]; then
    for candidate in /opt/homebrew/opt/openssl@3/bin/openssl \
                     /usr/local/opt/openssl@3/bin/openssl openssl; do
      if command -v "$candidate" >/dev/null 2>&1 \
         && "$candidate" version 2>/dev/null | grep -q "OpenSSL 3"; then
        OPENSSL_BIN="$candidate"
        break
      fi
    done
  fi
  [[ -n "$OPENSSL_BIN" ]] || {
    echo "error: OpenSSL 3.x required for PEM keys (or pass DER base64-decoded)" >&2
    exit 1
  }
  # rivet's signer reads DER (OneAsymmetricKey); convert the PEM once here.
  "$OPENSSL_BIN" pkey -in "$KEY" -outform DER -out "$KEY_DER"
else
  cp "$KEY" "$KEY_DER"
fi

cat > "$SCRIPT" <<RKT
#lang racket/base
(require rivet/distribution
         racket/date
         racket/file
         racket/format)
(define version "$VERSION")
(define base-url "$BASE_URL")
(define dist (path->complete-path "$DIST"))
(define key-path (path->complete-path "$KEY_DER"))
(define key-id "brainfuel-2026-10")
(define build (hash-ref (file->value (build-path (path->complete-path "$ROOT") "rivet.rktd")) 'build))

(define (artifact platform architecture file installer)
  (define path (build-path dist file))
  (unless (file-exists? path)
    (error 'make-update-manifest "missing installer: ~a" path))
  (update-artifact platform architecture
                   (string-append base-url "/" file)
                   (sha256-file/hex path)
                   (file-size path)
                   installer
                   '()))

(define manifest
  (update-manifest "site.jrtx.brainfuel"
                   version
                   build
                   'stable
                   ;; published-at: RFC 3339, second precision, UTC
                   (let ([d (seconds->date (current-seconds) #f)])
                     (format "~a-~a-~aT~a:~a:~aZ"
                             (date-year d)
                             (~r (date-month d) #:min-width 2 #:pad-string "0")
                             (~r (date-day d) #:min-width 2 #:pad-string "0")
                             (~r (date-hour d) #:min-width 2 #:pad-string "0")
                             (~r (date-minute d) #:min-width 2 #:pad-string "0")
                             (~r (date-second d) #:min-width 2 #:pad-string "0")))
                   "0.0.0"
                   #f
                   #t
                   100
                   (list (artifact 'macos 'arm64
                                   (format "brainfuel-~a-macos-arm64.zip" version) 'zip)
                         (artifact 'macos 'x64
                                   (format "brainfuel-~a-macos-x64.zip" version) 'zip)
                         (artifact 'windows 'x64
                                   (format "brainfuel-~a-windows-x64.zip" version) 'zip)
                         (artifact 'linux 'x64
                                   (format "brainfuel-~a-linux-x64.tar.gz" version) 'targz))))

;; write-signed-manifest validates the struct against the manifest schema
;; before signing, so a malformed manifest fails the release instead of
;; shipping something every client would reject.
(call-with-output-file (build-path dist "update-manifest.json")
  #:exists 'truncate/replace
  (lambda (out)
    (write-signed-manifest manifest
                           (read-ed25519-private-key key-path)
                           key-id
                           out)
    (newline out)))
(printf "manifest: ~a (4 artifacts, key-id ~a)\\n"
        (build-path dist "update-manifest.json") key-id)
RKT

# rivet must be installed for the signer; the release publish job links a
# checkout at the release pin.
racket "$SCRIPT"
