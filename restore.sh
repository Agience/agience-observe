#!/usr/bin/env bash
# Agience — restore a sovereign node from a ./backup.sh tarball.
#
# Destructive: overwrites the lattice (and, unless RESTORE_STORE_ONLY, the trust keys +
# content + Origin state). Guarded by FORCE=1, and the node must be stopped — restoring a SQLite
# file underneath a process holding it open corrupts both.
#
# Usage:
#   FORCE=1 ./restore.sh <backup.tar.gz>
#   FORCE=1 RESTORE_STORE_ONLY=1 ./restore.sh <backup.tar.gz>   # lattice only
#
# Env:
#   DATA_DIR           ./.data-local            where state is restored
#   MANTLE_CONTAINER   agience-local-mantle-1   must NOT be running
#   RESTORE_STORE_ONLY unset                    set to restore ONLY the lattice (skip keys/content/origin)
#   FORCE              unset                    must be set — refuses to overwrite otherwise
set -euo pipefail

TARBALL="${1:-}"
DATA_DIR="${DATA_DIR:-./.data-local}"
MANTLE="${MANTLE_CONTAINER:-agience-local-mantle-1}"

say() { printf '\033[36m[restore]\033[0m %s\n' "$1"; }
die() { printf '\033[31m[restore] %s\033[0m\n' "$1" >&2; exit 1; }

[ -n "$TARBALL" ] || die "usage: FORCE=1 ./restore.sh <backup.tar.gz>"
[ -f "$TARBALL" ] || die "no such backup: $TARBALL"
[ -n "${FORCE:-}" ] || die "refusing to overwrite without FORCE=1 (restore is destructive — use a stopped stack)"
# A bound port is the honest test that something still holds the store open. Nothing here can
# stop it: a restore that killed a running node would be doing two destructive things at once.
if command -v curl >/dev/null 2>&1 \
   && curl -s -o /dev/null --max-time 2 "http://127.0.0.1:${MANTLE_PORT:-8082}/" 2>/dev/null; then
  die "something is serving on 127.0.0.1:${MANTLE_PORT:-8082} — stop the node first (restoring a live SQLite file corrupts it)"
fi

STAGE="$(dirname "$TARBALL")/.restore-$$"
rm -rf "$STAGE"; mkdir -p "$STAGE"
trap 'rm -rf "$STAGE"' EXIT

say "unpacking $TARBALL ..."
tar -xzf "$TARBALL" -C "$STAGE"
[ -d "$STAGE/mantle" ] || die "backup has no mantle/ (lattice) component — is this a pre-flip (ArangoDB-era) backup?"
[ -f "$STAGE/mantle/mantle.db" ] || die "backup mantle/ has no mantle.db"

# 1) The lattice.
say "restoring lattice into $DATA_DIR/mantle ..."
mkdir -p "$DATA_DIR"
rm -rf "$DATA_DIR/mantle"
cp -a "$STAGE/mantle" "$DATA_DIR/mantle"

# 2) Trust seed + Origin + content — unless store-only.
if [ -z "${RESTORE_STORE_ONLY:-}" ]; then
  say "restoring keys$([ -d "$STAGE/origin" ] && echo ' + origin')$([ -d "$STAGE/minio" ] && echo ' + content') ..."
  [ -d "$STAGE/keys" ]   && { rm -rf "$DATA_DIR/keys";   cp -a "$STAGE/keys"   "$DATA_DIR/keys"; }
  [ -d "$STAGE/origin" ] && { rm -rf "$DATA_DIR/origin"; cp -a "$STAGE/origin" "$DATA_DIR/origin"; }
  [ -d "$STAGE/minio" ]  && { rm -rf "$DATA_DIR/minio";  cp -a "$STAGE/minio"  "$DATA_DIR/minio"; }
else
  say "RESTORE_STORE_ONLY — skipping keys/content/origin"
fi

say "done — restart the stack to pick up restored state."
