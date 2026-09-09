#!/usr/bin/env bash
# Build the distributable platform seed corpus tarball.
#
# Untarred into a node's seeds directory, which Mantle reads at AGIENCE_SEEDS_ROOT and applies
# via POST /api/system/seed. Mantle stays bare; this is how a domain delivers its founding
# catalog + grants.
#
# Publish the output to your get-base (get.agience.ai/full/seeds.tar.gz). A different
# domain ships its own corpus here — same mechanism, different content.
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
