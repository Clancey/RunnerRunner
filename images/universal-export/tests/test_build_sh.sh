#!/usr/bin/env bash
# build.sh argument and dry-run contract (local only: no ssh, no docker).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
UE="$(cd "$HERE/.." && pwd)"
B="$UE/build.sh"
OUT="$(mktemp)"
trap 'rm -f "$OUT"' EXIT
n=0
fail() { echo "FAIL: $1"; sed 's/^/  | /' "$OUT"; exit 1; }
expect_rc() { # want desc args...
  local want=$1 desc=$2 rc=0; shift 2
  bash "$B" "$@" >"$OUT" 2>&1 || rc=$?
  [ "$rc" = "$want" ] || fail "$desc: rc=$rc, want $want"
  n=$((n + 1))
}

for t in current latest previous stable canary; do
  expect_rc 64 "protected tag $t" --dry-run --allow-no-apple-smoke --tag "$t"
done
expect_rc 64 "option-like tag" --dry-run --allow-no-apple-smoke --tag -rm
expect_rc 64 "bad tag charset" --dry-run --allow-no-apple-smoke --tag 'a/b'
expect_rc 64 "build without an Apple pack or the allow flag" --host nowhere.invalid --tag ue-x
grep -q -- "--apple-pack-remote PATH is required" "$OUT" || fail "missing the Apple pack explanation"
expect_rc 64 "pack and allow flag together" --dry-run --apple-pack-remote /p.tar.zst --allow-no-apple-smoke
expect_rc 64 "unknown Apple build" --dry-run --apple-pack-remote /p.tar.zst --apple-build 99Z999
expect_rc 64 "stage without ID=SRC" --dry-run --allow-no-apple-smoke --stage ovr-platform-util
expect_rc 64 "flag missing its value" --dry-run --tag
expect_rc 64 "unknown flag" --dry-run --frobnicate

expect_rc 0 "dry run with a pack" --dry-run --tag ue-t1 --apple-pack-remote /mnt/x/apple-sdk-27A9275.tar.zst \
  --stage 'ovr-platform-util=image:rr:old:/opt/ovr/ovr-platform-util'
while IFS= read -r kv; do
  [ -n "$kv" ] || continue
  grep -qF -- "--build-arg ${kv%%=*}=" "$OUT" || fail "dry run is missing build-arg ${kv%%=*}"
done <<EOF
$(python3 "$UE/lib/lockctl.py" --lock "$UE/toolchain.lock.json" build-args)
EOF
grep -Eq -- "-t rr-universal-export:ue-tmp-ue-t1-[0-9]+ \.$" "$OUT" || fail "dry run does not build to the temporary tag ue-tmp-ue-t1-<pid>"
grep -Eq -- "^! docker image inspect rr-universal-export:ue-t1 && docker tag rr-universal-export:ue-tmp-ue-t1-[0-9]+ rr-universal-export:ue-t1 && docker rmi rr-universal-export:ue-tmp-ue-t1-[0-9]+$" "$OUT" \
  || fail "dry run does not move the temporary tag to rr-universal-export:ue-t1"
if ! grep -F -- "/opt/universal-export/receipt.json" "$OUT" | grep -qF "rr-universal-export:ue-t1 "; then
  fail "the receipt must come from the final tag"
fi
grep -F -- "run_smoke.sh" "$OUT" | grep -qF "rr-universal-export:ue-t1 " || fail "the smoke must run the final tag"
grep -qF -- "--network none" "$OUT" || fail "smoke must run with --network none"
grep -qF -- "-v /mnt/x/apple-sdk-27A9275.tar.zst:/run/apple-sdk/pack.tar.zst:ro" "$OUT" || fail "Apple pack must be bind-mounted read-only"
grep -qF -- "--apple-build 27A9275" "$OUT" || fail "default --apple-build must be the lock's only known pack"
grep -qF -- "--stage ovr-platform-util=image:rr:old:/opt/ovr/ovr-platform-util" "$OUT" || fail "--stage not forwarded to cas.sh"
if grep -q -- "--allow-no-apple-smoke" "$OUT"; then fail "a pack-backed smoke must not allow skipping the Apple link"; fi
n=$((n + 1))

expect_rc 0 "dry run without a pack" --dry-run --tag ue-t2 --allow-no-apple-smoke
grep -qF -- "--allow-no-apple-smoke" "$OUT" || fail "allow flag not forwarded to the smoke"
grep -q "not promotable" "$OUT" || fail "dry run must say the candidate is not promotable"
expect_rc 0 "default tag" --dry-run --allow-no-apple-smoke
grep -Eq 'rr-universal-export:ue-[0-9]{8}-[0-9a-f]{12} ' "$OUT" || fail "default tag is not ue-<date>-<recipe12>"

# ---- end-to-end result mapping through ssh/rsync/docker shims ---------------
# Modelled on test_build_lock.sh test 17: a copy of the recipe tree runs
# against a shim PATH. ssh drops its options and host and executes locally
# with the Tower root rewritten into the sandbox (SHIM_FAIL_CLEAR=1 refuses
# clearing the remote in-flight marker). docker keeps tags as files in
# $E2E_IMAGES: build -t creates one (SHIM_RACE=1 also creates the final tag,
# as if it appeared mid-build), tag/rmi/image inspect act on them, and every
# docker run must name an existing, non-temporary tag. The smoke run exits
# SHIM_SMOKE_RC and writes SHIM_REPORT (literal JSON, or "missing") as
# smoke-report.json, plus the log archive unless SHIM_LOGS is missing|corrupt.
E2E="$(mktemp -d)"
trap 'rm -f "$OUT"; chmod -R u+w "$E2E" 2>/dev/null; rm -rf "$E2E"' EXIT
mkdir -p "$E2E/shim"
cp -R "$UE" "$E2E/ue"
rm -rf "$E2E/ue/tests" "$E2E/ue/logs" "$E2E/ue/.build-lock" "$E2E/ue/.plan.tsv"
find "$E2E/ue" -name __pycache__ -prune -exec rm -rf {} +
cat > "$E2E/shim/ssh" <<'EOF2'
#!/bin/sh
while [ $# -gt 0 ]; do
  case $1 in -o|-p|-i|-l) shift 2 ;; -*) shift ;; *) break ;; esac
done
shift
case "$*" in *cas.sh*) exit 0 ;; esac
if [ "${SHIM_FAIL_CLEAR:-0}" = 1 ]; then
  case "$*" in "rm -f '"*"/unknown'") exit 1 ;; esac
fi
cmd=$(printf '%s\n' "$*" | sed "s|/mnt/user/appdata/rr-builds/universal-export|$E2E_REMOTE|g")
exec bash -c "$cmd"
EOF2
cat > "$E2E/shim/docker" <<'EOF2'
#!/bin/sh
echo "docker $*" >> "$E2E_DOCKER_LOG"
img() { printf '%s/%s' "$E2E_IMAGES" "$1"; }
case $1 in
  build)
    prev=""
    for a in "$@"; do
      [ "$prev" = -t ] && : > "$(img "$a")" && built=$a
      prev=$a
    done
    if [ "${SHIM_RACE:-0}" = 1 ]; then : > "$(img "${built%%:ue-tmp-*}:$SHIM_FINAL_TAG")"; fi
    exit 0 ;;
  image)
    ref=""; for a in "$@"; do ref=$a; done
    [ -e "$(img "$ref")" ] || exit 1
    case "$*" in *--format*) echo sha256:shim ;; esac
    exit 0 ;;
  tag) [ -e "$(img "$2")" ] || exit 1; : > "$(img "$3")"; exit 0 ;;
  rmi) [ -e "$(img "$2")" ] || exit 1; rm -f "$(img "$2")"; exit 0 ;;
  run)
    ref=""
    for a in "$@"; do case $a in rr-universal-export:*) ref=$a ;; esac; done
    [ -n "$ref" ] && [ -e "$(img "$ref")" ] || { echo "shim: no such image $ref" >&2; exit 125; }
    case $ref in *:ue-tmp-*) echo "shim: ran the temporary tag $ref" >&2; exit 125 ;; esac
    case "$*" in
      *run_smoke.sh*)
        touch "$E2E_SMOKED"
        out="" prev=""
        for a in "$@"; do
          if [ "$prev" = -v ]; then case $a in *:/smoke-out) out=${a%:/smoke-out} ;; esac; fi
          prev=$a
        done
        [ "$SHIM_REPORT" = missing ] || printf '%s\n' "$SHIM_REPORT" > "$out/smoke-report.json"
        case ${SHIM_LOGS:-ok} in
          ok) mkdir -p "$out/x/logs" && echo log > "$out/x/logs/a.log" && tar -czf "$out/smoke-report-logs.tar.gz" -C "$out/x" logs && rm -rf "$out/x" ;;
          corrupt) echo 'not a tarball' > "$out/smoke-report-logs.tar.gz" ;;
        esac
        exit "$SHIM_SMOKE_RC" ;;
      *) echo '{"receipt": "shim"}' ;;
    esac ;;
esac
exit 0
EOF2
printf '#!/bin/sh\nexit 0\n' > "$E2E/shim/rsync"
chmod +x "$E2E/shim/ssh" "$E2E/shim/docker" "$E2E/shim/rsync"
export E2E_REMOTE="$E2E/remote" E2E_SMOKED="$E2E/smoked" E2E_IMAGES="$E2E/images" E2E_DOCKER_LOG="$E2E/docker.log"
e2e() { # want desc pack|nopack smoke_rc report [fail_clear [logs [race]]]
  local want=$1 desc=$2 rc=0 mode=--allow-no-apple-smoke pack=""
  [ "$3" = pack ] && { mode=--apple-pack-remote; pack=/mnt/x/apple-sdk.tar.zst; }
  rm -rf "$E2E/ue/logs" "$E2E_SMOKED" "$E2E_IMAGES" "$E2E_DOCKER_LOG"
  mkdir -p "$E2E_IMAGES"
  (
    export PATH="$E2E/shim:$PATH" BUILD_PROJECTED_BYTES=0 BUILD_TIMEOUT_SECONDS=0 SMOKE_TIMEOUT_SECONDS=0
    export SHIM_SMOKE_RC="$4" SHIM_REPORT="$5" SHIM_FAIL_CLEAR="${6:-0}" SHIM_LOGS="${7:-ok}" SHIM_RACE="${8:-0}"
    export SHIM_FINAL_TAG="ue-e2e-$n"
    unset UE_REMOTE_ROOT
    # shellcheck disable=SC2086  # $pack is empty or one word
    bash "$E2E/ue/build.sh" --host shim --tag "ue-e2e-$n" $mode $pack
  ) >"$OUT" 2>&1 || rc=$?
  [ -d "$E2E_REMOTE/ctx" ] || fail "$desc: build.sh never reached the shim remote"
  [ "$rc" = "$want" ] || fail "$desc: rc=$rc, want $want"
  n=$((n + 1))
}
# report VARIANT: a full report built from the lock, then broken per VARIANT.
report() {
  python3 - "$UE/toolchain.lock.json" "$1" <<'PY'
import json, sys
lock, variant = json.load(open(sys.argv[1])), sys.argv[2]
build = next(iter(lock["apple_sdk"]["known_packs"]))
pack = not variant.startswith("nopack")
row = {"status": "pass", "detail": "", "artifact_sha256": None}
r = {"schema": 1, "network_isolated": True,
     "sets": {sid: {p: dict(row) for p in s["smoke_presets"]} for sid, s in lock["godot_sets"].items()},
     "apple": {"status": "pass", "build": build, "detail": ""} if pack else
              {"status": "skip", "build": None, "detail": "no pack"},
     "hygiene": {h: {"status": "pass", "detail": ""}
                 for h in ("secret-env", "verify-installed", "isolation-check", "ephemeral-keys")},
     "promotable": pack}
first = next(iter(r["sets"]))
if variant.endswith("-missing-preset"):
    del r["sets"][first]["ios"]
elif variant.endswith("-missing-set"):
    del r["sets"][first]
elif variant.endswith("-failed-preset"):
    r["sets"][first]["linux-x86_64"]["status"] = "fail"
elif variant.endswith("-hygiene"):
    r["hygiene"]["isolation-check"]["status"] = "fail"
elif variant.endswith("-no-hygiene"):
    del r["hygiene"]["verify-installed"]
elif variant.endswith("-not-isolated"):
    r["network_isolated"] = False
elif variant.endswith("-schema"):
    r["schema"] = 2
elif variant.endswith("-apple-build"):
    r["apple"]["build"] = "00A0000"
elif variant.endswith("-apple-fail"):
    r["apple"]["status"] = "fail"
elif variant.endswith("-not-promotable"):
    r["promotable"] = not pack
print(json.dumps(r))
PY
}
PROM=$(report pack)
NOTPROM=$(report nopack)
e2e 0 "pack, smoke rc 0, valid promotable report" pack 0 "$PROM"
grep -q "PROMOTABLE" "$OUT" || fail "no PROMOTABLE result line"
[ ! -e "$E2E/ue/.build-lock" ] && [ ! -e "$E2E_REMOTE/.build-lock" ] || fail "locks not released after success"
[ -s "$E2E/ue/logs/ue-e2e-$((n - 1)).smoke-logs.tar.gz" ] || fail "the log archive was not fetched"
[ -e "$E2E_IMAGES/rr-universal-export:ue-e2e-$((n - 1))" ] || fail "final tag missing after success"
for f in "$E2E_IMAGES"/*ue-tmp-*; do [ ! -e "$f" ] || fail "temporary tag not removed: $f"; done
grep -Eq "^docker build .* -t rr-universal-export:ue-tmp-ue-e2e-$((n - 1))-[0-9]+ \.$" "$E2E_DOCKER_LOG" || fail "build did not use the temporary tag"
e2e 1 "pack, smoke rc 0, report missing" pack 0 missing
e2e 1 "pack, smoke rc 0, report malformed" pack 0 'not json {'
e2e 1 "pack, smoke rc 0, report not an object" pack 0 '[true]'
e2e 1 "pack, smoke rc 0, minimal promotable report" pack 0 '{"schema": 1, "promotable": true}'
e2e 1 "pack, smoke rc 0, promotable is a string" pack 0 '{"promotable": "true"}'
for v in not-promotable missing-preset missing-set failed-preset hygiene no-hygiene not-isolated schema apple-build apple-fail; do
  e2e 1 "pack, smoke rc 0, report $v" pack 0 "$(report "pack-$v")"
done
e2e 1 "pack, smoke rc 0, no-pack report" pack 0 "$NOTPROM"
e2e 1 "pack, smoke rc 2" pack 2 "$NOTPROM"
e2e 1 "pack, smoke rc 1" pack 1 "$PROM"
e2e 1 "pack, valid report, log archive missing" pack 0 "$PROM" 0 missing
grep -q "smoke-report-logs.tar.gz" "$OUT" || fail "no explanation for the missing log archive"
e2e 1 "pack, valid report, log archive corrupt" pack 0 "$PROM" 0 corrupt
e2e 2 "no pack, smoke rc 2, valid no-pack report" nopack 2 "$NOTPROM"
grep -q "NOT promotable" "$OUT" || fail "no NOT promotable result line"
e2e 1 "no pack, smoke rc 0, promotable report" nopack 0 "$PROM"
e2e 1 "no pack, smoke rc 2, report missing" nopack 2 missing
e2e 1 "no pack, smoke rc 2, report malformed" nopack 2 '{"promotable":'
e2e 1 "no pack, smoke rc 2, promotable report" nopack 2 "$PROM"
e2e 1 "no pack, smoke rc 2, minimal report" nopack 2 '{"schema": 1, "promotable": false}'
for v in missing-preset failed-preset hygiene not-isolated not-promotable; do
  e2e 1 "no pack, smoke rc 2, report $v" nopack 2 "$(report "nopack-$v")"
done
e2e 1 "no pack, valid report, log archive missing" nopack 2 "$NOTPROM" 0 missing

# The final tag appearing while the build ran: refuse to retag, keep the
# build under its temporary tag, run neither receipt nor smoke.
e2e 1 "final tag appears during the build" pack 0 "$PROM" 0 ok 1
grep -q "appeared on shim during the build" "$OUT" || fail "no explanation for the tag race"
[ ! -e "$E2E_SMOKED" ] || fail "smoke ran after the tag race"
kept=
for f in "$E2E_IMAGES"/*"ue-tmp-ue-e2e-$((n - 1))-"*; do [ ! -e "$f" ] || kept=1; done
[ -n "$kept" ] || fail "temporary tag not kept after the tag race"
if grep -q "^docker tag" "$E2E_DOCKER_LOG"; then fail "retagged despite the race"; fi

# A failed marker clear after a confirmed rc=0 must stop the run with exit 1,
# retaining both locks (still marked), before the receipt and smoke.
e2e 1 "marker clear fails after build rc 0" pack 0 "$PROM" 1
[ ! -e "$E2E_SMOKED" ] || fail "smoke ran after the marker clear failed"
[ -f "$E2E_REMOTE/.build-lock/unknown" ] || fail "remote lock/marker not retained after the marker clear failed"
[ -d "$E2E/ue/.build-lock" ] || fail "local lock not retained after the marker clear failed"

echo "ok: $n build.sh contract checks"
