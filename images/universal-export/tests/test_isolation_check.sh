#!/usr/bin/env bash
# Tests for bin/isolation-check's Apple SDK state report. bash 3.2 compatible.
# The image runs jobs as root, where a write probe always succeeds, so the
# state must come from mountinfo and the admission sidecar instead.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
IC="$HERE/../bin/isolation-check"
TMP="$(mktemp -d)"
trap 'chmod -R u+w "$TMP" 2>/dev/null; rm -rf "$TMP"' EXIT
PASS=0
fail() { echo "FAIL: $1" >&2; echo "--- output:" >&2; cat "$TMP/out" >&2 || true; exit 1; }
ok() { PASS=$((PASS + 1)); echo "ok: $1"; }

state() { # expect-rc label
  local rc=0
  IC_MOUNTINFO="$TMP/mountinfo" APPLE_SDK="$TMP/opt/apple-sdk" "$IC" sdk-state > "$TMP/out" 2>&1 || rc=$?
  [ "$rc" = "$1" ] || fail "$2: rc=$rc, want $1"
}

mkdir -p "$TMP/opt"
mi_line() { # mountpoint opts
  printf '1234 1200 0:99 / %s %s,nosuid,nodev - overlay overlay rw\n' "$1" "$2"
}

# absent: no mount, no dir, no sidecar
: > "$TMP/mountinfo"
state 0 absent
grep -q '^apple-sdk: absent$' "$TMP/out" || fail "absent report"
ok "absent"

# read-only bind mount (verify-mounted path)
mkdir -p "$TMP/opt/apple-sdk"
{ mi_line /etc/hosts rw; mi_line "$TMP/opt/apple-sdk" ro; } > "$TMP/mountinfo"
state 0 "ro mount"
grep -q '^apple-sdk: mounted read-only (ok)$' "$TMP/out" || fail "ro mount report"
ok "read-only mount"

# writable bind mount is a failure even though the dir exists
mi_line "$TMP/opt/apple-sdk" rw > "$TMP/mountinfo"
state 1 "rw mount"
grep -q '^apple-sdk: mounted WRITABLE (fail)$' "$TMP/out" || fail "rw mount report"
ok "writable mount fails"

# admitted copy: no mount, sidecar present, and apple-sdk-admit verify passes
mkdir -p "$TMP/shim"
cat > "$TMP/shim/apple-sdk-admit" <<'SH'
#!/bin/sh
echo "$*" > "$IC_TEST_ADMIT_ARGS"
exit "${IC_TEST_ADMIT_RC:-0}"
SH
chmod +x "$TMP/shim/apple-sdk-admit"
export IC_SDK_ADMIT="$TMP/shim/apple-sdk-admit" IC_TEST_ADMIT_ARGS="$TMP/admit-args"
: > "$TMP/mountinfo"
printf '{"build": "27A9275"}\n' > "$TMP/opt/.apple-sdk-admission.json"
state 0 admitted
grep -q '^apple-sdk: admitted copy of 27A9275 (verified)$' "$TMP/out" || fail "admitted report"
[ "$(cat "$TMP/admit-args")" = "verify --build 27A9275 --dest $TMP/opt/apple-sdk" ] || fail "verify args: $(cat "$TMP/admit-args")"
ok "admitted copy is verified"

# an admitted copy that fails apple-sdk-admit verify is a failure
IC_TEST_ADMIT_RC=1 state 1 "admitted, verify fails"
grep -q '^apple-sdk: admitted copy of 27A9275 FAILS verification (fail)$' "$TMP/out" || fail "verify-fail report"
ok "admitted copy failing verify fails"

# an unreadable admission record is a failure
printf '{}\n' > "$TMP/opt/.apple-sdk-admission.json"
state 1 "sidecar without build"
grep -q '^apple-sdk: unreadable admission record (fail)$' "$TMP/out" || fail "bad sidecar report"
ok "sidecar without build fails"

# a sidecar whose destination is gone (or is a symlink) is not an admitted SDK
printf '{"build": "27A9275"}\n' > "$TMP/opt/.apple-sdk-admission.json"
mv "$TMP/opt/apple-sdk" "$TMP/opt/real-sdk"
state 1 "sidecar, no dest"
grep -q '^apple-sdk: admission record without an admitted tree (fail)$' "$TMP/out" || fail "no-dest report"
ln -s "$TMP/opt/real-sdk" "$TMP/opt/apple-sdk"
state 1 "sidecar, symlink dest"
grep -q '^apple-sdk: admission record without an admitted tree (fail)$' "$TMP/out" || fail "symlink-dest report"
rm "$TMP/opt/apple-sdk"; mv "$TMP/opt/real-sdk" "$TMP/opt/apple-sdk"
ok "sidecar without an admitted tree fails"

# a directory with neither a mount nor an admission record is unexplained
rm -f "$TMP/opt/.apple-sdk-admission.json"
state 1 unadmitted
grep -q '^apple-sdk: present but neither mounted nor admitted (fail)$' "$TMP/out" || fail "unadmitted report"
ok "unadmitted dir fails"

# Full report. timeout is shimmed to decide the network probe; xcrun -f
# answers from a shim so the host's real xcrun never runs.
cat > "$TMP/shim/timeout" <<'SH'
#!/bin/sh
exit "${IC_TEST_NET_RC:-1}"
SH
cat > "$TMP/shim/xcrun" <<'SH'
#!/bin/sh
echo "${IC_TEST_XCRUN_DIR:-/usr/bin}/$2"
SH
chmod +x "$TMP/shim/timeout" "$TMP/shim/xcrun"
mi_line "$TMP/opt/apple-sdk" ro > "$TMP/mountinfo"
full() { # expect-rc label [extra PATH prefix]
  local rc=0
  PATH="${3:+$3:}$TMP/shim:$PATH" IC_MOUNTINFO="$TMP/mountinfo" APPLE_SDK="$TMP/opt/apple-sdk" \
    "$IC" > "$TMP/out" 2>&1 || rc=$?
  [ "$rc" = "$1" ] || fail "$2: rc=$rc, want $1"
}
full 0 "clean report"
grep -q '^unreachable$' "$TMP/out" || fail "clean report network"
ok "clean full report"

IC_TEST_NET_RC=0 full 1 "network reachable"
grep -q '^REACHABLE (fail)$' "$TMP/out" || fail "reachable report"
ok "reachable network fails"

mkdir -p "$TMP/opt/apple-sdk/usr/bin"
printf '\177ELF\002\001\001\000\000\000\000\000\000\000\000\000\002\000\076\000\001\000\000\000' \
  > "$TMP/opt/apple-sdk/usr/bin/tool"
chmod +x "$TMP/opt/apple-sdk/usr/bin/tool"
full 1 "ELF in SDK"
grep -q '^FORBIDDEN: ELF executables inside the SDK (fail)$' "$TMP/out" || fail "ELF report"
rm "$TMP/opt/apple-sdk/usr/bin/tool"
ok "ELF executable inside the SDK fails"

printf '#!/bin/sh\n' > "$TMP/opt/apple-sdk/usr/bin/clang"
chmod +x "$TMP/opt/apple-sdk/usr/bin/clang"
full 1 "tool from SDK" "$TMP/opt/apple-sdk/usr/bin"
grep -q '^FORBIDDEN: clang resolves into the SDK (fail)$' "$TMP/out" || fail "SDK tool report"
rm "$TMP/opt/apple-sdk/usr/bin/clang"
ok "tool resolving into the SDK fails"

IC_TEST_XCRUN_DIR="$TMP/opt/apple-sdk/usr/bin" full 1 "xcrun -f into SDK"
grep -q '^FORBIDDEN: xcrun -f ld resolves into the SDK (fail)$' "$TMP/out" || fail "xcrun SDK report"
ok "xcrun -f resolving into the SDK fails"

echo "isolation-check: $PASS passed"
