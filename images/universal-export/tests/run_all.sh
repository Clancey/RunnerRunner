#!/usr/bin/env bash
# Static test suite for images/universal-export (no Docker, no network):
# every tests/test_*.sh, every tests/test_*.py, then lockctl validate and
# check-dockerfile. Exits nonzero if anything failed.
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
UE="$(cd "$HERE/.." && pwd)"
export PYTHONDONTWRITEBYTECODE=1
FAILED=""
run() { # name cmd...
  local name=$1; shift
  echo "== $name"
  if "$@"; then echo "-- ok: $name"; else echo "-- FAIL: $name"; FAILED="$FAILED $name"; fi
}
for t in "$HERE"/test_*.sh; do
  run "$(basename "$t")" bash "$t"
done
run unittest python3 -m unittest discover -s "$HERE" -t "$HERE" -p 'test_*.py'
run lockctl-validate python3 "$UE/lib/lockctl.py" --lock "$UE/toolchain.lock.json" validate
run check-dockerfile python3 "$UE/lib/lockctl.py" --lock "$UE/toolchain.lock.json" check-dockerfile "$UE/Dockerfile"
if [ -n "$FAILED" ]; then echo "FAILED:$FAILED"; exit 1; fi
echo "ALL UNIVERSAL-EXPORT TESTS PASSED"
