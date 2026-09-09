#!/usr/bin/env bash
# Build the distributable platform seed corpus tarball.
#
# Untarred into a node's seeds directory, which Mantle reads at AGIENCE_SEEDS_ROOT and applies
# via POST /api/system/seed. Mantle stays bare; this is how a domain delivers its founding
# catalog + grants.
#
# Publish the output wherever your nodes fetch it from. A different domain ships its own corpus
# the same way — same mechanism, different content.
#
# This tarball is NOT published at a public URL today. `get.agience.ai/full/seeds.tar.gz` was named
# here as the destination and has never served it: until 2026-09-09 that path answered 200 with the
# marketing homepage, so `curl -o seeds.tar.gz` would have written HTML to disk and reported
# success. It now returns 404, which is the truthful answer for a file nobody uploaded. Name a real
# URL here on the day one exists.
#
#   ./pack-seeds.sh [out.tar.gz]     # default: ./seeds.tar.gz
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
src="$here/seeds"
out="${1:-$here/seeds.tar.gz}"

[ -d "$src/platform" ] || { echo "error: no platform seed tree at $src/platform" >&2; exit 1; }

# Tar the CONTENTS of seeds/ (platform/ user/ admin/) so untar into package/seeds
# reproduces the tree AGIENCE_SEEDS_ROOT is expected to hold.
tar -czf "$out" -C "$src" .
echo "packed $(cd "$src" && find platform user admin -type f 2>/dev/null | wc -l) files -> $out"
