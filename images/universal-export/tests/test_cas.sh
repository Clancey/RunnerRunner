#!/usr/bin/env bash
# Tests for cas.sh (content-addressed input cache). bash 3.2 compatible.
# Uses file:// URLs through curl and a stub docker via CAS_DOCKER.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
CAS="$HERE/../cas.sh"
TMP="$(mktemp -d)"
# shellcheck source=../safe_rm.sh
. "$HERE/../safe_rm.sh"
trap 'chmod -R u+w "$TMP" 2>/dev/null; safe_rm "$TMP"' EXIT
export CAS_RESERVE_BYTES=0
PASS=0

fail() { echo "FAIL: $1" >&2; echo "--- stderr:" >&2; cat "$TMP/err" >&2 2>/dev/null || true; exit 1; }
ok() { PASS=$((PASS + 1)); echo "ok: $1"; }
sha() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'; else shasum -a 256 "$1" | awk '{print $1}'; fi; }
size() { wc -c < "$1" | tr -d ' '; }
inode() { python3 -c 'import os,sys; print(os.stat(sys.argv[1]).st_ino)' "$1"; }

reset() {
  chmod -R u+w "$TMP/w" 2>/dev/null || true
  rm -rf "$TMP/w"
  mkdir -p "$TMP/w/src" "$TMP/w/ctx"
  CACHE="$TMP/w/cache"; CTX="$TMP/w/ctx"; PLAN="$TMP/w/plan.tsv"
  printf 'alpha payload\n' > "$TMP/w/src/alpha.bin"
  printf 'beta payload\n' > "$TMP/w/src/beta.bin"
  A_SHA=$(sha "$TMP/w/src/alpha.bin"); A_SIZE=$(size "$TMP/w/src/alpha.bin")
  B_SHA=$(sha "$TMP/w/src/beta.bin"); B_SIZE=$(size "$TMP/w/src/beta.bin")
  : > "$PLAN"
}

row() { # id sha size filename url rolling seeds
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$@" >> "$PLAN"
}

run_cas() { "$CAS" --plan "$PLAN" --cache "$CACHE" --ctx "$CTX" "$@" > "$TMP/out" 2> "$TMP/err"; }

expect_rc() { # rc label cmd...
  local want=$1 label=$2; shift 2
  local rc=0
  "$@" || rc=$?
  [ "$rc" = "$want" ] || fail "$label: exit $rc, want $want"
}

# 1. url fetch, CAS object + ctx hardlink, then a cache hit with the source gone.
reset
row alpha "$A_SHA" "$A_SIZE" alpha.bin "file://$TMP/w/src/alpha.bin" 0 -
expect_rc 0 "url fetch" run_cas
[ -f "$CACHE/sha256/$A_SHA" ] || fail "object not cached"
[ "$(inode "$CACHE/sha256/$A_SHA")" = "$(inode "$CTX/inputs/$A_SHA")" ] || fail "ctx input is not a hardlink of the CAS object"
[ ! -w "$CACHE/sha256/$A_SHA" ] || fail "CAS object should be read-only"
rm "$TMP/w/src/alpha.bin"
expect_rc 0 "cache hit" run_cas
grep -q "alpha.*cached" "$TMP/err" || fail "second run did not report a cache hit"
ok "url fetch is cached, linked, and reused without the source"

# 2. A corrupted cached object is quarantined (never deleted) and refetched.
reset
row alpha "$A_SHA" "$A_SIZE" alpha.bin "file://$TMP/w/src/alpha.bin" 0 -
expect_rc 0 "seed run" run_cas
chmod u+w "$CACHE/sha256/$A_SHA"; printf 'rot' >> "$CACHE/sha256/$A_SHA"
expect_rc 0 "refetch after corruption" run_cas
grep -q "quarantine" "$TMP/err" || fail "no quarantine message"
[ "$(find "$CACHE/quarantine" -type f | wc -l | tr -d ' ')" = 1 ] || fail "quarantine should hold the bad object"
[ "$(sha "$CACHE/sha256/$A_SHA")" = "$A_SHA" ] || fail "object not restored"
ok "corrupt cache object quarantined and refetched"

# 3. --stage takes precedence; a wrong staged file is a hard failure.
reset
row alpha "$A_SHA" "$A_SIZE" alpha.bin "file://$TMP/w/nonexistent" 0 -
cp "$TMP/w/src/alpha.bin" "$TMP/w/staged"
expect_rc 0 "stage path" run_cas --stage "alpha=$TMP/w/staged"
grep -q "alpha.*staged" "$TMP/err" || fail "stage source not reported"
reset
row alpha "$A_SHA" "$A_SIZE" alpha.bin "file://$TMP/w/src/alpha.bin" 0 -
expect_rc 1 "wrong stage" run_cas --stage "alpha=$TMP/w/src/beta.bin"
grep -q "mismatch" "$TMP/err" || fail "wrong stage not reported as mismatch"
[ ! -e "$CACHE/sha256/$A_SHA" ] || fail "url must not be tried after a wrong explicit stage"
ok "--stage used first; a wrong staged file fails hard"

# 4. Seeds: a wrong seed is skipped with a warning, the next seed is used, url untouched.
reset
row alpha "$A_SHA" "$A_SIZE" alpha.bin "file://$TMP/w/nonexistent" 0 \
  "file:$TMP/w/src/beta.bin file:$TMP/w/missing file:$TMP/w/src/alpha.bin"
expect_rc 0 "seed fallthrough" run_cas
grep -q "beta.bin.*mismatch" "$TMP/err" || fail "wrong seed not warned"
grep -q "alpha.*file:$TMP/w/src/alpha.bin" "$TMP/err" || fail "good seed not reported as the source"
ok "seeds tried in order; wrong/missing seeds skipped"

# 5. image: seeds go through docker with no network, splitting REF at the last ':/'.
reset
mkdir -p "$TMP/w/img"
cat > "$TMP/w/docker" <<EOF
#!/usr/bin/env bash
printf '%s\n' "\$*" >> "$TMP/w/docker.log"
ref=\${@: -2:1}; path=\${@: -1}
[ "\$ref" = "repo/linux-apple-runner:1.0" ] && [ "\$path" = "/opt/godot-fork/godot" ] && exec cat "$TMP/w/src/alpha.bin"
exit 125
EOF
chmod +x "$TMP/w/docker"
row alpha "$A_SHA" - alpha.bin - 0 "image:repo/linux-apple-runner:1.0:/opt/godot-fork/godot"
CAS_DOCKER="$TMP/w/docker" expect_rc 0 "image seed" run_cas
grep -q -- "run --rm --network none --entrypoint cat repo/linux-apple-runner:1.0 /opt/godot-fork/godot" "$TMP/w/docker.log" \
  || fail "unexpected docker invocation: $(cat "$TMP/w/docker.log")"
ok "image: seed extracted via docker run --network none (null size accepted)"

# 6. Rolling url that now serves other bytes: 'upstream drifted'. Non-rolling: mismatch.
reset
row alpha "$A_SHA" "$A_SIZE" alpha.bin "file://$TMP/w/src/beta.bin" 1 -
expect_rc 1 "rolling drift" run_cas
grep -q "upstream drifted" "$TMP/err" || fail "no drift message"
grep -q "$B_SHA" "$TMP/err" || fail "drift message lacks the served digest"
reset
row alpha "$A_SHA" "$A_SIZE" alpha.bin "file://$TMP/w/src/beta.bin" 0 -
expect_rc 1 "url mismatch" run_cas
grep -q "mismatch" "$TMP/err" || fail "no mismatch message"
ok "rolling drift and url mismatch fail with distinct messages"

# 7. The lock size is enforced too.
reset
row alpha "$A_SHA" "$((A_SIZE + 1))" alpha.bin "file://$TMP/w/src/alpha.bin" 0 -
expect_rc 1 "size mismatch" run_cas
grep -q "mismatch" "$TMP/err" || fail "size mismatch not reported"
ok "size pin enforced"

# 8. --offline never touches the url; cached inputs still pass.
reset
row alpha "$A_SHA" "$A_SIZE" alpha.bin "file://$TMP/w/src/alpha.bin" 0 -
expect_rc 1 "offline missing" run_cas --offline
grep -q "offline" "$TMP/err" || fail "offline failure not explained"
[ ! -e "$CACHE/sha256/$A_SHA" ] || fail "offline run fetched the url"
expect_rc 0 "online" run_cas
expect_rc 0 "offline cached" run_cas --offline
ok "--offline skips urls and passes on cache hits"

# 9. --check is read-only: reports missing bytes, exits 1, creates nothing.
reset
row alpha "$A_SHA" "$A_SIZE" alpha.bin "file://$TMP/w/src/alpha.bin" 0 -
row beta "$B_SHA" "$B_SIZE" beta.bin "file://$TMP/w/src/beta.bin" 0 -
expect_rc 1 "check missing" run_cas --check
[ ! -e "$CACHE" ] || fail "--check created the cache"
grep -q "^missing_bytes=$((A_SIZE + B_SIZE))$" "$TMP/out" || fail "missing_bytes wrong: $(cat "$TMP/out")"
expect_rc 0 "fill" run_cas
expect_rc 0 "check ok" run_cas --check
grep -q "^missing_bytes=0$" "$TMP/out" || fail "missing_bytes should be 0"
ok "--check is read-only and reports missing bytes"

# 10. ctx/inputs holds exactly the plan: stale digests pruned, foreign files refused.
reset
row alpha "$A_SHA" "$A_SIZE" alpha.bin "file://$TMP/w/src/alpha.bin" 0 -
mkdir -p "$CTX/inputs"
printf 'old' > "$CTX/inputs/$(printf 'e%.0s' $(seq 64))"
expect_rc 0 "prune" run_cas
[ "$(ls "$CTX/inputs")" = "$A_SHA" ] || fail "ctx/inputs not pruned to the plan: $(ls "$CTX/inputs")"
printf 'x' > "$CTX/inputs/notes.txt"
expect_rc 1 "foreign file" run_cas
grep -q "notes.txt" "$TMP/err" || fail "foreign file not named"
[ -e "$CTX/inputs/notes.txt" ] || fail "foreign file must not be deleted"
ok "ctx/inputs pruned to the plan; foreign files refused, not deleted"

# 11. Disk admission: missing bytes + reserve must fit before any fetch.
reset
row alpha "$A_SHA" "$A_SIZE" alpha.bin "file://$TMP/w/src/alpha.bin" 0 -
CAS_RESERVE_BYTES=999999999999999 expect_rc 1 "disk" run_cas
grep -q "disk" "$TMP/err" || fail "no disk message"
[ ! -e "$CACHE/sha256/$A_SHA" ] || fail "fetched despite failing disk admission"
ok "disk admission refuses before fetching"

# 12. Usage errors exit 64; placeholder seeds are ignored defensively.
reset
row alpha "$A_SHA" "$A_SIZE" alpha.bin "file://$TMP/w/src/alpha.bin" 0 -
expect_rc 64 "no plan" "$CAS" --cache "$CACHE" --ctx "$CTX"
expect_rc 64 "unknown stage id" run_cas --stage "nope=/x"
expect_rc 64 "bad stage" run_cas --stage "alpha"
reset
printf 'bad id\t%s\t-\tf\t-\t0\t-\n' "$A_SHA" > "$PLAN"
expect_rc 64 "bad plan row" run_cas
reset
row alpha "$A_SHA" "$A_SIZE" alpha.bin "file://$TMP/w/src/alpha.bin" 0 "image:<tag>:/x"
CAS_DOCKER=/nonexistent expect_rc 0 "placeholder seed" run_cas
grep -q "placeholder" "$TMP/err" || fail "placeholder seed not reported"
ok "usage errors exit 64; placeholder seeds skipped"

echo "test_cas: $PASS groups passed"
