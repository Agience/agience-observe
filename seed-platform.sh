#!/usr/bin/env bash
# Agience — apply the platform seed corpus to a full/home install (via the API).
#
# Mantle ships bare: it bundles no seed content and applies none at boot. The
# platform seed corpus lives here, in this repo's package/seeds, and is pointed at by
# AGIENCE_SEEDS_ROOT. This script is
# the install step that tells Mantle to apply the founding platform artifacts —
# after the operator is claimed and before users sign in, so their grant seeds
# resolve. Idempotent (safe to re-run).
#
# A bare seed node intentionally does not run this.
#
# Usage:
#   OPERATOR_TOKEN=<jwt>  ./seed-platform.sh          # token from bootstrap claim / login
#   ./seed-platform.sh <operator-jwt>
#   MANTLE_URL=https://my.node ./seed-platform.sh <jwt>
set -euo pipefail

MANTLE_URL="${MANTLE_URL:-http://localhost:8081}"
TOKEN="${1:-${OPERATOR_TOKEN:-}}"

if [ -z "$TOKEN" ]; then
  echo "error: operator token required (first arg or OPERATOR_TOKEN env)." >&2
  echo "  obtain it from POST /auth/bootstrap/claim (first operator) or" >&2
  echo "  POST /auth/password/login on Origin. See SEED.md." >&2
  exit 1
fi

# Mantle answers 202 at once with the run; the seed runs on and `follow` reports it.
field() { python3 -c 'import json,sys; print(json.load(sys.stdin).get(sys.argv[1]) or "")' "$1"; }

echo "[seed] applying platform corpus -> ${MANTLE_URL}/system/seed ..."
run=$(curl -fsS -X POST "${MANTLE_URL}/system/seed" \
  -H "Authorization: Bearer ${TOKEN}" \
  -H "Content-Type: application/json")
follow=$(printf '%s' "$run" | field follow)
[ -n "$follow" ] || { echo "[seed] unexpected answer: $run" >&2; exit 1; }
echo "[seed] run $(printf '%s' "$run" | field run_id) started; following ${follow}"

state=running
for _ in $(seq 1 600); do
  run=$(curl -fsS "${MANTLE_URL}${follow}" -H "Authorization: Bearer ${TOKEN}")
  state=$(printf '%s' "$run" | field state)
  [ "$state" = running ] || break
  sleep 2
done
printf '%s\n' "$run" | sed 's/^/[seed] /'
case "$state" in
  done) echo "[seed] done — users signing in now receive the platform collection grants." ;;
  running) echo "[seed] still running after 20 min; follow ${MANTLE_URL}${follow}" >&2; exit 1 ;;
  *) echo "[seed] the seed run failed" >&2; exit 1 ;;
esac
