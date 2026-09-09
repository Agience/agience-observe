#!/usr/bin/env bash
# Agience — back up a sovereign node's full state to one portable tarball.
#
# A node's state is three things: the store (Mantle's lattice — one SQLite file + the
# content CAS tree on the mantle volume), the trust seed (KEYS_DIR — RSA identities,
# encryption.key, authority manifest), and Origin's SQLite (if this node runs one).
# MinIO (the local S3 content/SSE endpoint) is included unless SKIP_CONTENT=1.
#
# The SQLite file is snapshotted with the sqlite3 online-backup API rather than copied — a plain
# `cp` of a live SQLite db is torn by design. The API is safe whether or not Mantle is running, so
# there is one path and no "is it up?" branch to get wrong.
#
# Usage:
#   ./backup.sh [label]                 # default label = UTC timestamp
#   DATA_DIR=~/.agience/.data ./backup.sh nightly
#
# Env:
#   DATA_DIR          ~/.agience/.data         the node's data dir (keys/mantle/minio/origin)
#   PYTHON            python3                  interpreter used for the snapshot
#   OUT_DIR           ./backups                where the tarball lands
#   SKIP_CONTENT=1                             omit the local object store (large)
set -euo pipefail

DATA_DIR="${DATA_DIR:-$HOME/.agience/.data}"
PYTHON="${PYTHON:-python3}"
OUT_DIR="${OUT_DIR:-./backups}"
LABEL="${1:-$(date -u +%Y%m%d-%H%M%SZ)}"

say() { printf '\033[36m[backup]\033[0m %s\n' "$1"; }
die() { printf '\033[31m[backup] %s\033[0m\n' "$1" >&2; exit 1; }

[ -d "$DATA_DIR/keys" ]   || die "no trust seed at $DATA_DIR/keys — is DATA_DIR right?"
[ -d "$DATA_DIR/mantle" ] || die "no lattice at $DATA_DIR/mantle — is DATA_DIR right?"

mkdir -p "$OUT_DIR"
# Stage under OUT_DIR (a real host dir) — not mktemp /tmp, which Git-Bash maps to a
# nonexistent C:\tmp.
STAGE="$OUT_DIR/.stage-$LABEL"
rm -rf "$STAGE"; mkdir -p "$STAGE/mantle"
SNAP_HOST="$DATA_DIR/mantle/.backup-snapshot-$LABEL.db"
trap 'rm -rf "$STAGE"; rm -f "$SNAP_HOST"' EXIT

# 1) The lattice — consistent SQLite snapshot, taken in place.
say "lattice: online snapshot ..."
command -v "$PYTHON" >/dev/null 2>&1 || die "$PYTHON not found — set PYTHON to an interpreter"
"$PYTHON" -c "
import sqlite3, sys
src = sqlite3.connect(sys.argv[1])
dst = sqlite3.connect(sys.argv[2])
src.backup(dst)
dst.close(); src.close()
" "$DATA_DIR/mantle/mantle.db" "$SNAP_HOST" || die "sqlite online backup failed"
mv "$SNAP_HOST" "$STAGE/mantle/mantle.db"

# 2) The CAS tree (content-addressed, immutable blobs — safe to copy live) + any other
#    mantle-volume state, EXCLUDING the live db files already snapshotted above.
say "copying lattice CAS tree ..."
(cd "$DATA_DIR/mantle" && tar cf - --exclude='mantle.db' --exclude='mantle.db-wal' \
    --exclude='mantle.db-shm' --exclude='.backup-snapshot-*' .) | (cd "$STAGE/mantle" && tar xf -)

# 3) Trust seed + Origin state (+ content unless skipped).
say "copying keys + origin${SKIP_CONTENT:+ (content skipped)} ..."
cp -a "$DATA_DIR/keys" "$STAGE/keys"
[ -d "$DATA_DIR/origin" ] && cp -a "$DATA_DIR/origin" "$STAGE/origin" || say "  (no origin dir — Mantle-only node)"
if [ -z "${SKIP_CONTENT:-}" ]; then
  if [ -d "$DATA_DIR/minio" ]; then
    say "copying content store (MinIO) — may be large ..."
    cp -a "$DATA_DIR/minio" "$STAGE/minio"
  else
    say "  (no minio dir — skipping content)"
  fi
fi

# 4) Manifest — what/when/where, for restore-time sanity.
{
  echo "label=$LABEL"
  echo "created_utc=$(date -u +%FT%TZ)"
  echo "store=lattice"
  echo "mantle_container=$MANTLE"
  echo "snapshot_mode=$([ -n "$MANTLE_RUNNING" ] && echo online || echo closed-copy)"
  echo "data_dir=$DATA_DIR"
  echo "components=mantle,keys$([ -d "$STAGE/origin" ] && echo ,origin)$([ -d "$STAGE/minio" ] && echo ,minio)"
} > "$STAGE/MANIFEST"

OUT="$OUT_DIR/agience-backup-$LABEL.tar.gz"
say "packing -> $OUT"
tar -czf "$OUT" -C "$STAGE" .
say "done: $(du -h "$OUT" | cut -f1)  ($OUT)"
echo "$OUT"
