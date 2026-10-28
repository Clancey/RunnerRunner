#!/usr/bin/env bash
# shellcheck disable=SC2030,SC2031  # PATH shims are subshell-local on purpose
# Mock tests for build-lock.sh: live contention, age-never-evidence,
# death-established takeover, foreign-host and ownerless refusal, trap
# cleanup on failure, owner-token-safe release, the UNKNOWN-attempt marker
# (persisted before submission, surviving SIGKILL with no trap, local and
# remote), the remote (ssh) path against a local ssh shim, and an
# end-to-end refusal through the real build.sh. Exits nonzero on the first
# failure.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
UE="$(cd "$HERE/.." && pwd)"
TMP="$(mktemp -d)"
. "$UE/safe_rm.sh"
trap 'safe_rm "$TMP"' EXIT
export BUILD_LOCK_WAIT_SECONDS=2
export BUILD_LOCK_POLL_SECONDS=1

try_acquire() { # dir label remote -> 0 acquired, 1 refused (message on stderr)
  . "$UE/build-lock.sh"
  build_lock_acquire "$1" "$2" "$3" 2>"$TMP/err.log" || return 1
  return 0
}

write_owner() { # dir pid host
  printf '%s %s %s\n' "$2" "$(date +%s)" "$3" > "$1/owner"
}

fail() { echo "FAIL: $1 (stderr: $(cat "$TMP/err.log" 2>/dev/null))" >&2; exit 1; }

# 1. Live contention: a live holder refuses the waiter with the owner message.
( . "$UE/build-lock.sh"; build_lock_acquire "$TMP/lk" holder 0; sleep 6 ) &
HOLDER=$!
sleep 1
if try_acquire "$TMP/lk" waiter 0; then
  fail "acquire succeeded while a live holder owned the lock"
fi
grep -q "held by another build" "$TMP/err.log" || fail "no contention message"
kill "$HOLDER" 2>/dev/null || true
wait "$HOLDER" 2>/dev/null || true
echo "ok: live contention refused with owner message"

# 2. Age is never evidence: an old lock with a LIVE owner is not stolen,
#    even when the directory mtime is ancient (the old 6h rule would have
#    admitted a second writer here).
touch -t 202001010000 "$TMP/lk"
write_owner "$TMP/lk" "$$" "$(uname -n)"
if try_acquire "$TMP/lk" waiter 0; then
  fail "old lock with a live owner was stolen by age"
fi
echo "ok: old lock with live owner not stolen (age is not evidence)"

# 3. Death-established takeover: a lock whose owner PID is gone, on this
#    host, is taken over. The dead PID is synthetic (a finished
#    `sh -c 'echo $$'`), so the test does not depend on subshell $$ semantics.
DEAD_PID=$(sh -c 'echo $$')
write_owner "$TMP/lk" "$DEAD_PID" "$(uname -n)"
if ! try_acquire "$TMP/lk" waiter 0; then
  fail "dead-owner lock not taken over"
fi
build_lock_release_all
[[ ! -d "$TMP/lk" ]] || fail "lock dir survived release"
echo "ok: dead-owner lock taken over and released"

# 4. A lock with no owner file is unknown, not stale: fail closed.
mkdir -p "$TMP/lk4"
if try_acquire "$TMP/lk4" waiter 0; then
  fail "ownerless lock was acquired"
fi
echo "ok: ownerless lock refused (fail closed)"

# 5. A foreign-host owner is never stolen, even with a dead PID.
mkdir -p "$TMP/lk5"
write_owner "$TMP/lk5" "$DEAD_PID" "some-other-runner"
if try_acquire "$TMP/lk5" waiter 0; then
  fail "foreign-host owner was stolen"
fi
echo "ok: foreign-host owner refused"

# 6. Failure cleanup: an EXIT trap releases the lock when the build dies.
rc=0
( . "$UE/build-lock.sh"; trap build_lock_release_all EXIT
  build_lock_acquire "$TMP/lk6" doomed 0; exit 7 ) || rc=$?
[[ "$rc" = 7 ]] || fail "subshell exit code $rc, expected 7"
[[ ! -d "$TMP/lk6" ]] || fail "EXIT trap did not release the lock"
echo "ok: EXIT trap releases the lock on failure"

# 7. Idempotency: releasing with nothing held must not error (set -u safe).
( set -u; . "$UE/build-lock.sh"; build_lock_release_all; build_lock_release_all )
echo "ok: release_all idempotent with no locks held"

# 8. Remote path via an ssh shim (executes the command locally).
mkdir -p "$TMP/bin"
printf '#!/bin/sh\nshift\nexec bash -c "$*"\n' > "$TMP/bin/ssh"
chmod +x "$TMP/bin/ssh"
export PATH="$TMP/bin:$PATH"
(
  . "$UE/build-lock.sh"
  BUILD_LOCK_SSH_HOST=shim
  build_lock_acquire "$TMP/ctx" remote 1
  [[ -d "$TMP/ctx" ]] || { echo "FAIL: remote lock dir not created" >&2; exit 1; }
  build_lock_release_all
  [[ ! -d "$TMP/ctx" ]] || { echo "FAIL: remote lock dir survived release" >&2; exit 1; }
)
echo "ok: remote acquire/release via ssh shim"

# 9. Remote takeover rules: a foreign-host owner is refused; a same-host
#    dead owner is taken over through the shim.
mkdir -p "$TMP/ctx2"
write_owner "$TMP/ctx2" "$DEAD_PID" "other-runner"
if try_acquire "$TMP/ctx2" remote 1; then
  fail "remote foreign-host owner was stolen"
fi
write_owner "$TMP/ctx2" "$DEAD_PID" "$(uname -n)"
if ! try_acquire "$TMP/ctx2" remote 1; then
  fail "remote same-host dead owner not taken over"
fi
build_lock_release_all
echo "ok: remote foreign owner refused, same-host dead owner taken over"

# 10. Owner-token-safe release: a late trap from a process whose lock was
#     taken over must not delete the successor's lock; deleting with the
#     owner's own stamp still removes it. (release_all is terminal — it
#     clears its held arrays — so the matching-stamp case exercises the
#     compare-and-delete primitive directly.)
(
  . "$UE/build-lock.sh"
  build_lock_acquire "$TMP/lk10" t 0
  OUR_STAMP=$(cat "$TMP/lk10/owner")
  printf '99999 1 successor-host\n' > "$TMP/lk10/owner"
  build_lock_release_all
  [[ -d "$TMP/lk10" ]] || { echo "FAIL: release deleted a successor's lock" >&2; exit 1; }
  printf '%s\n' "$OUR_STAMP" > "$TMP/lk10/owner"
  _build_lock_remove_if_owner "$TMP/lk10" 0 "$OUR_STAMP"
  [[ ! -d "$TMP/lk10" ]] || { echo "FAIL: compare-and-delete did not remove the matching lock" >&2; exit 1; }
)
echo "ok: owner-token-safe release"

# 11. Owner-write failure fails closed and removes the just-created dir: an
#     ownerless lock must never be left behind to wedge future builds. The
#     shim refuses the owner-write ssh (a blip) but executes the cleanup
#     removal, proving the dir is gone when acquire returns nonzero.
SHIM11="$TMP/bin11"; mkdir -p "$SHIM11"
cat > "$SHIM11/ssh" <<'EOF'
#!/bin/sh
shift
# Blip only the owner write (printf into owner); the non-recursive cleanup
# (rm -f owner + rmdir) must still run.
case "$*" in *printf*) exit 255 ;; esac
exec bash -c "$*"
EOF
chmod +x "$SHIM11/ssh"
mkdir -p "$TMP/remote11"
if ( export PATH="$SHIM11:$PATH"; try_acquire "$TMP/remote11/lk" blip 1 ); then
  fail "acquire succeeded despite owner-write failure"
fi
[[ ! -e "$TMP/remote11/lk" ]] || fail "ownerless lock left behind after owner-write failure"
echo "ok: owner-write failure cleans up the lock dir"

# 12. UNKNOWN-attempt marker: a dead-owner lock is normally taken over
#     (test 3), but once a holder marks an unconfirmed stop — here an
#     unclassified failed-client exit (rc=1), which is not authoritative
#     about the daemon-side solve — the marker must block acquisition after
#     the holder exits, and the non-recursive remove must fail while the
#     dir is non-empty.
mkdir -p "$TMP/lk12"
write_owner "$TMP/lk12" "$DEAD_PID" "$(uname -n)"
if ! try_acquire "$TMP/lk12" waiter 0; then
  fail "dead-owner lock without marker not taken over (baseline changed?)"
fi
build_lock_release_all
(
  . "$UE/build-lock.sh"
  build_lock_acquire "$TMP/lk12" holder 0
  build_lock_mark_all_unknown "unclassified failed-client exit rc=1"
  # holder exits WITHOUT releasing, as the STOP UNKNOWN path does
)
[[ -f "$TMP/lk12/unknown" ]] || fail "unknown marker not written"
if try_acquire "$TMP/lk12" waiter 0; then
  fail "acquire succeeded despite the UNKNOWN-attempt marker"
fi
grep -q "UNKNOWN-attempt marker" "$TMP/err.log" || fail "no marker refusal message"
HOLDER_STAMP=$(cat "$TMP/lk12/owner")
_build_lock_remove_if_owner "$TMP/lk12" 0 "$HOLDER_STAMP"
[[ -d "$TMP/lk12" ]] || fail "marked lock dir was removed despite the marker"
echo "ok: unknown marker blocks takeover and the non-recursive remove"

# 13. Remote UNKNOWN-attempt marker: a holder that marks the remote lock
#     (through the ssh shim) blocks the next remote acquisition after it
#     exits — no Tower access needed; the shim executes locally. The holder
#     creates the lock dir itself, exactly as a real run would.
(
  . "$UE/build-lock.sh"
  BUILD_LOCK_SSH_HOST=shim
  build_lock_acquire "$TMP/ctx13" remote 1
  build_lock_mark_all_unknown "build timed out rc=124"
)
[[ -f "$TMP/ctx13/unknown" ]] || fail "remote unknown marker not written"
if try_acquire "$TMP/ctx13" remote 1; then
  fail "remote acquire succeeded despite the UNKNOWN-attempt marker"
fi
grep -q "UNKNOWN-attempt marker" "$TMP/err.log" || fail "no remote marker refusal message"
echo "ok: remote unknown marker blocks acquisition"

# 14. The failure classifier shared by both orchestrators maps timeout,
#     transport-loss and unclassified client exits to named reasons, so the
#     retained marker records what happened; every nonzero rc classifies
#     because no client-side rc is authoritative about the daemon solve.
. "$UE/build-lock.sh"
BUILD_STOP_REASON=""
build_lock_classify_stop 124
[[ "$BUILD_STOP_REASON" == "build timed out rc=124" ]] || fail "rc=124 not classified as a timeout"
build_lock_classify_stop 137
[[ "$BUILD_STOP_REASON" == "build timed out rc=137" ]] || fail "rc=137 not classified as a timeout"
build_lock_classify_stop 255
[[ "$BUILD_STOP_REASON" == "ssh transport failure rc=255" ]] || fail "rc=255 not classified as a transport failure"
build_lock_classify_stop 1
[[ "$BUILD_STOP_REASON" == "unclassified failed-client exit rc=1" ]] || fail "rc=1 not classified as unclassified"
build_lock_classify_stop 42
[[ "$BUILD_STOP_REASON" == "unclassified failed-client exit rc=42" ]] || fail "rc=42 not classified as unclassified"
echo "ok: failure classifier maps every nonzero rc to a named reason"

# 15. SIGKILL after submission: the in-flight marker is persisted BEFORE the
#     build ssh, so a holder killed with -9 — no trap runs, no EXIT — still
#     leaves a marked lock that blocks takeover while the daemon-side solve
#     may be live.
HOLDER15_LOG="$TMP/holder15.ready"; rm -f "$HOLDER15_LOG"
(
  . "$UE/build-lock.sh"
  build_lock_acquire "$TMP/lk15" holder 0 || exit 3
  build_lock_mark_all_unknown "build submitted, completion unconfirmed" || exit 3
  echo ready >> "$HOLDER15_LOG"
  sleep 30
) &
HOLDER15=$!
sleep 1
[[ -f "$HOLDER15_LOG" ]] || fail "holder 15 never reached the marked state"
kill -9 "$HOLDER15" 2>/dev/null || true
wait "$HOLDER15" 2>/dev/null || true
[[ -f "$TMP/lk15/unknown" ]] || fail "in-flight marker missing after kill -9"
if try_acquire "$TMP/lk15" waiter 0; then
  fail "acquire succeeded after the holder was SIGKILLed post-submission"
fi
grep -q "UNKNOWN-attempt marker" "$TMP/err.log" || fail "no marker refusal message after kill -9"
echo "ok: kill -9 after submission leaves a marked, unacquirable lock"

# 16. Same, through the remote (ssh shim) path: the marker write happens
#     before submission, so a SIGKILLed holder leaves the remote lock
#     marked and unacquirable — no Tower access needed.
HOLDER16_LOG="$TMP/holder16.ready"; rm -f "$HOLDER16_LOG"
(
  . "$UE/build-lock.sh"
  BUILD_LOCK_SSH_HOST=shim
  build_lock_acquire "$TMP/ctx16" remote 1 || exit 3
  build_lock_mark_all_unknown "build submitted, completion unconfirmed" || exit 3
  echo ready >> "$HOLDER16_LOG"
  sleep 30
) &
HOLDER16=$!
sleep 1
[[ -f "$HOLDER16_LOG" ]] || fail "remote holder 16 never reached the marked state"
kill -9 "$HOLDER16" 2>/dev/null || true
wait "$HOLDER16" 2>/dev/null || true
[[ -f "$TMP/ctx16/unknown" ]] || fail "remote in-flight marker missing after kill -9"
if try_acquire "$TMP/ctx16" remote 1; then
  fail "remote acquire succeeded after the holder was SIGKILLed post-submission"
fi
grep -q "UNKNOWN-attempt marker" "$TMP/err.log" || fail "no remote marker refusal message after kill -9"
echo "ok: kill -9 after remote submission leaves a marked, unacquirable lock"

# 17. End-to-end refusal: the real build.sh must refuse to submit the docker
#     build when the pre-submission marker write fails, and must release
#     both locks (nothing was submitted). Runs a copy of the recipe tree
#     against a shim PATH: ssh drops its options and host, refuses every
#     marker write, accepts cas.sh, and executes everything else locally
#     with the Tower root rewritten into the sandbox; rsync is a stub; disk
#     admission and timeout(1) are disabled by env.
E2E="$TMP/e2e"; mkdir -p "$E2E/shim"
cp -R "$UE" "$E2E/ue"
safe_rm "$E2E/ue/tests"
rm -rf "$E2E/ue/logs" "$E2E/ue/.build-lock" "$E2E/ue/.plan.tsv"
cat > "$E2E/shim/ssh" <<'EOF'
#!/bin/sh
while [ $# -gt 0 ]; do
  case $1 in -o|-p|-i|-l) shift 2 ;; -*) shift ;; *) break ;; esac
done
shift
# Refuse every marker write (this also makes a marked lock read as
# blocked); execute everything else with the Tower root rewritten.
case "$*" in *unknown*) exit 1 ;; esac
case "$*" in *cas.sh*) exit 0 ;; esac
# Real sentinel: if a docker build ever passes through this shim, the
# submission happened and the assertion below must fail.
case "$*" in *"docker build"*) touch "$E2E_SUBMITTED" ;; esac
cmd=$(printf '%s\n' "$*" | sed "s|/mnt/user/appdata/rr-builds/universal-export|$E2E_REMOTE|g")
exec bash -c "$cmd"
EOF
printf '#!/bin/sh\nexit 0\n' > "$E2E/shim/rsync"
chmod +x "$E2E/shim/ssh" "$E2E/shim/rsync"
export E2E_REMOTE="$E2E/remote"
export E2E_SUBMITTED="$TMP/e2e-submitted"
rm -f "$E2E_SUBMITTED"
rc=0
(
  export PATH="$E2E/shim:$PATH"
  unset UE_REMOTE_ROOT
  export BUILD_PROJECTED_BYTES=0
  export BUILD_TIMEOUT_SECONDS=0
  bash "$E2E/ue/build.sh" --host shim --tag ue-e2e-test --allow-no-apple-smoke
) >"$TMP/e2e.out" 2>&1 || rc=$?
[[ "$rc" != 0 ]] || fail "build.sh exited 0 despite refusing to submit"
[[ ! -e "$E2E_SUBMITTED" ]] || fail "docker build was submitted despite a failed marker write"
if ! grep -q "refusing to submit" "$TMP/e2e.out" 2>/dev/null; then
  grep -q "refusing to submit" "$E2E/ue/logs/"*.log 2>/dev/null || fail "no refusal message in build.sh output: $(tail -5 "$TMP/e2e.out")"
fi
[[ -d "$E2E_REMOTE/ctx" ]] || fail "build.sh never reached the remote (shim path rewrite broken): $(tail -5 "$TMP/e2e.out")"
[[ ! -e "$E2E/ue/.build-lock" ]] || fail "local lock not released after the refused submission"
[[ ! -e "$E2E_REMOTE/.build-lock" ]] || fail "remote lock not released after the refused submission"
echo "ok: build.sh refuses to submit when the in-flight marker write fails"

echo "ALL BUILD-LOCK TESTS PASSED"
