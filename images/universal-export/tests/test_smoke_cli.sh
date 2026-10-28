#!/usr/bin/env bash
# CLI contract of smoke/run_smoke.sh and smoke/android_prewarm.sh: syntax,
# --help, usage errors exit 64 before any side effect, shellcheck when present.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
SMOKE="$HERE/../smoke"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
fails=0

check() { # description expected_rc cmd...
  local desc=$1 want=$2 rc=0
  shift 2
  "$@" >"$TMP/out" 2>&1 || rc=$?
  if [[ $rc -eq $want ]]; then
    echo "ok: $desc"
  else
    echo "FAIL: $desc (exit $rc, want $want)"
    sed 's/^/  | /' "$TMP/out"
    fails=$((fails + 1))
  fi
}

for f in "$SMOKE"/*.sh; do
  check "bash -n $(basename "$f")" 0 bash -n "$f"
done
for f in run_smoke.sh android_prewarm.sh; do
  [[ -f "$SMOKE/$f" ]] || { echo "FAIL: missing smoke/$f"; fails=$((fails + 1)); }
done

RUN="$SMOKE/run_smoke.sh"
W="$TMP/work"
check "--help exits 0" 0 bash "$RUN" --help
grep -q -- "--allow-no-apple-smoke" "$TMP/out" || { echo "FAIL: --help does not document the flags"; fails=$((fails + 1)); }
check "no apple flag" 64 bash "$RUN" --mode offline --work "$W"
check "unknown flag" 64 bash "$RUN" --mode offline --allow-no-apple-smoke --frobnicate --work "$W"
check "missing --mode" 64 bash "$RUN" --allow-no-apple-smoke --work "$W"
check "online mode" 64 bash "$RUN" --mode online --allow-no-apple-smoke --work "$W"
check "pack without build" 64 bash "$RUN" --mode offline --apple-pack "$TMP/p.tar.zst" --work "$W"
check "build without pack" 64 bash "$RUN" --mode offline --apple-build 27A9275 --work "$W"
check "pack and allow together" 64 bash "$RUN" --mode offline --apple-pack "$TMP/p" --apple-build B \
  --allow-no-apple-smoke --work "$W"
check "flag without value" 64 bash "$RUN" --mode offline --allow-no-apple-smoke --work
check "empty --sets" 64 bash "$RUN" --mode offline --allow-no-apple-smoke --sets "" --work "$W"
check "positional argument" 64 bash "$RUN" --mode offline --allow-no-apple-smoke extra --work "$W"
if [[ -e "$W" ]]; then
  echo "FAIL: a usage error created the work dir"
  fails=$((fails + 1))
fi

check "android_prewarm.sh rejects arguments" 64 bash "$SMOKE/android_prewarm.sh" extra
check "android_prewarm.sh --help exits 0" 0 bash "$SMOKE/android_prewarm.sh" --help

if command -v shellcheck >/dev/null; then
  check "shellcheck -x" 0 shellcheck -x "$SMOKE"/*.sh "$HERE/test_smoke_cli.sh"
else
  echo "skip: shellcheck not installed"
fi

if [[ $fails -ne 0 ]]; then
  echo "$fails failure(s)"
  exit 1
fi
echo "all smoke CLI checks passed"
