# shellcheck shell=bash disable=SC2029,SC2034,SC2004,SC2155  # vendored from LogicLand: ssh args expand client-side by design; BUILD_STOP_REASON is read by build.sh
# build-lock.sh — sourced by build.sh; atomic mkdir-based single-writer
# locks for the shared local staging area and the remote build context.
#
# Why not flock: macOS ships no flock(1) and the tower has no python3, so
# the guard uses only mkdir (atomic), ps, uname, date and ssh. Two locks,
# always acquired local-then-remote (fixed order, so two concurrent runners
# cannot deadlock):
#
#   local  $HERE/.build-lock       serializes staging inside one checkout
#   remote $REMOTE_DIR.build-lock  sibling of the rsync target, so
#                                  rsync --delete cannot eat the lock
#
# Takeover rule (fail closed): a lock is taken over ONLY when the holder's
# death is established — the owner file records "PID epoch hostname", and a
# takeover requires it to name THIS host with a PID that no longer exists
# (ps -p). Age is never evidence: a build that legitimately runs for days
# must keep its lock, and a foreign or unknown host's owner can never be
# stolen remotely. A lock with a missing or unreadable owner file is
# unknown, not stale: acquisition fails closed (remove by hand only after
# confirming no build is running). Live contention fails closed after
# BUILD_LOCK_WAIT_SECONDS with the holder's owner file.
#
# Owner-token-safe release: every acquire records the owner stamp it wrote;
# release (and takeover) deletes a lock only while the owner file still
# matches that exact stamp — one compare-and-delete on the remote side — so
# a late EXIT trap from a process whose lock was taken over can never
# remove the successor's lock. Release is idempotent; the sourcer wires it
# to EXIT/INT/TERM.
#
# UNKNOWN-attempt marker: BEFORE a build can be submitted, the orchestrator
# persists an `unknown` file INTO each held lock dir
# (build_lock_mark_all_unknown) and refuses to submit if any write fails —
# a SIGKILLed or OOM-killed holder runs no trap, and a lost transport cannot
# mark the remote lock after the fact, so the marker must already be in
# place once a daemon-side solve can outlive its client (with any rc). The
# marker is cleared only after a confirmed rc=0
# (build_lock_clear_all_unknown); on any unconfirmed stop the holder exits
# without releasing and the EXIT trap merely refreshes the marker's reason.
# Both acquisition paths (fresh mkdir and dead-owner takeover) refuse a
# marked lock regardless of PID death, and the marker also keeps the dir
# non-empty so the non-recursive remove cannot succeed. Only a human
# removes the marker and lock, after confirming no build is running.
#
# Assumptions: lock paths and owner stamps contain no single quotes (they
# are embedded in remote shell strings); an ssh failure fails the build
# rather than ever proceeding unlocked.

BUILD_LOCK_WAIT_SECONDS=${BUILD_LOCK_WAIT_SECONDS:-30}
BUILD_LOCK_POLL_SECONDS=${BUILD_LOCK_POLL_SECONDS:-5}
BUILD_LOCK_SSH_HOST=${BUILD_LOCK_SSH_HOST:-}
BUILD_LOCK_HELD_DIRS=()
BUILD_LOCK_HELD_REMOTE=()
BUILD_LOCK_HELD_STAMPS=()

_build_lock_create() { # dir remote
  if (( $2 )); then ssh "$BUILD_LOCK_SSH_HOST" "mkdir '$1'"; else mkdir "$1"; fi
}

_build_lock_remove_if_owner() { # dir remote stamp — delete only if owner still matches
  # Non-recursive by policy (tests/test_ci_tower_safety.py): a lock dir holds
  # only the owner file, so rm -f + rmdir suffices and rmdir refuses to
  # traverse anything unexpected. Remote side stays one atomic ssh command.
  if (( $2 )); then
    ssh "$BUILD_LOCK_SSH_HOST" "if [ \"\$(cat '$1/owner' 2>/dev/null)\" = '$3' ]; then rm -f '$1/owner' && { rmdir '$1' 2>/dev/null || echo \"build-lock: $1 not empty on remote — left in place\" >&2; }; fi"
  else
    if [[ "$(cat "$1/owner" 2>/dev/null)" = "$3" ]]; then
      rm -f "$1/owner"
      rmdir "$1" 2>/dev/null || echo "build-lock: $1 not empty — left in place" >&2
    fi
  fi
  return 0
}

_build_lock_read_owner() { # dir remote
  if (( $2 )); then
    ssh "$BUILD_LOCK_SSH_HOST" "cat '$1/owner' 2>/dev/null"
  else
    cat "$1/owner" 2>/dev/null
  fi
}

_build_lock_pid_alive() { # pid — ps, not kill -0: EPERM on a live foreign
                          # process must not read as death
  ps -p "$1" >/dev/null 2>&1
}

_build_lock_unknown_blocked() { # dir remote — true when the marker exists
  if (( $2 )); then
    ssh "$BUILD_LOCK_SSH_HOST" "test -f '$1/unknown'"
  else
    [[ -f "$1/unknown" ]]
  fi
}

build_lock_mark_all_unknown() { # reason — persistently block takeover after
                                # this holder exits; never removed by code.
                                # Returns 1 if any write failed so the
                                # caller can refuse to submit a build.
  local reason=${1//\'/} i dir remote failed=0
  local stamp="unknown $(date -u +%FT%TZ) pid=${BASHPID:-$$} host=$(uname -n) reason=$reason"
  for (( i=${#BUILD_LOCK_HELD_DIRS[@]}-1; i>=0; i-- )); do
    dir="${BUILD_LOCK_HELD_DIRS[$i]}"
    remote="${BUILD_LOCK_HELD_REMOTE[$i]}"
    if (( remote )); then
      ssh "$BUILD_LOCK_SSH_HOST" "printf '%s\n' '$stamp' > '$dir/unknown'" \
        || { echo "build-lock: warning: could not write the unknown marker to $dir/unknown on $BUILD_LOCK_SSH_HOST" >&2; failed=1; }
    else
      printf '%s\n' "$stamp" > "$dir/unknown" \
        || { echo "build-lock: warning: could not write the unknown marker to $dir/unknown" >&2; failed=1; }
    fi
  done
  return $failed
}

build_lock_clear_all_unknown() { # — remove the in-flight marker from every
                                 # held lock; returns 1 if any removal
                                 # failed (the caller then retains)
  local i dir remote failed=0
  for (( i=${#BUILD_LOCK_HELD_DIRS[@]}-1; i>=0; i-- )); do
    dir="${BUILD_LOCK_HELD_DIRS[$i]}"
    remote="${BUILD_LOCK_HELD_REMOTE[$i]}"
    if (( remote )); then
      ssh "$BUILD_LOCK_SSH_HOST" "rm -f '$dir/unknown'" \
        || { echo "build-lock: warning: could not clear the unknown marker from $dir/unknown on $BUILD_LOCK_SSH_HOST" >&2; failed=1; }
    else
      rm -f "$dir/unknown" \
        || { echo "build-lock: warning: could not clear the unknown marker from $dir/unknown" >&2; failed=1; }
    fi
  done
  return $failed
}

build_lock_classify_stop() { # <rc> — name a failed build's client exit in
                             # BUILD_STOP_REASON; no rc is authoritative
                             # (see STOP UNKNOWN), this only labels it
  case $1 in
    124|137) BUILD_STOP_REASON="build timed out rc=$1" ;;
    255)     BUILD_STOP_REASON="ssh transport failure rc=255" ;;
    *)       BUILD_STOP_REASON="unclassified failed-client exit rc=$1" ;;
  esac
}

_build_lock_stealable() { # dir remote — prints the owner stamp and returns
                          # 0 only when the holder's death is proven
  local dir=$1 remote=$2 owner pid epoch host
  owner=$(_build_lock_read_owner "$dir" "$remote") || owner=""
  [[ -n "$owner" ]] || return 1
  read -r pid epoch host _ <<<"$owner" || return 1
  [[ "$pid" =~ ^[0-9]+$ && -n "$host" ]] || return 1
  [[ "$host" == "$(uname -n)" ]] || return 1
  ! _build_lock_pid_alive "$pid" || return 1
  printf '%s\n' "$owner"
}

build_lock_acquire() { # dir label remote
  local dir=$1 label=$2 remote=$3
  local waited=0 stamp steal_owner
  stamp="${BASHPID:-$$} $(date +%s) $(uname -n)"
  while :; do
    # An UNKNOWN-attempt marker blocks every acquisition path — fresh and
    # dead-owner takeover alike — regardless of PID death.
    if _build_lock_unknown_blocked "$dir" "$remote"; then
      echo "build-lock: $label is blocked by an UNKNOWN-attempt marker ($dir/unknown) — a previous attempt's stop was never confirmed; inspect the host, then remove the marker and lock by hand only after confirming no build is running" >&2
      return 1
    fi
    if _build_lock_create "$dir" "$remote" 2>/dev/null; then
      if (( $remote )); then
        if ! ssh "$BUILD_LOCK_SSH_HOST" "printf '%s\n' '$stamp' > '$dir/owner'"; then
          # Owner write failed: the just-created dir is ownerless and, by the
          # fail-closed rule, unacquirable — no successor can have claimed it
          # in between. Remove it (non-recursive: owner file + rmdir) instead
          # of leaking a lock that would wedge every future build. (If the
          # host is unreachable for this cleanup too, the leak remains;
          # nothing more can be done remotely.)
          ssh "$BUILD_LOCK_SSH_HOST" "rm -f '$dir/owner' 2>/dev/null; rmdir '$dir' 2>/dev/null || true"
          return 1
        fi
      else
        if ! printf '%s\n' "$stamp" > "$dir/owner"; then
          rm -f "$dir/owner" 2>/dev/null
          rmdir "$dir" 2>/dev/null || true
          return 1
        fi
      fi
      BUILD_LOCK_HELD_DIRS+=("$dir")
      BUILD_LOCK_HELD_REMOTE+=("$remote")
      BUILD_LOCK_HELD_STAMPS+=("$stamp")
      return 0
    fi
    if steal_owner=$(_build_lock_stealable "$dir" "$remote"); then
      echo "build-lock: taking over $label from a dead owner ($steal_owner)" >&2
      # Compare-and-delete: if the owner changed between the check and now
      # (another waiter won the race), this deletes nothing and the next
      # mkdir attempt re-evaluates against the real holder.
      _build_lock_remove_if_owner "$dir" "$remote" "$steal_owner"
      continue
    fi
    if (( waited >= BUILD_LOCK_WAIT_SECONDS )); then
      echo "build-lock: $label is held by another build (waited ${BUILD_LOCK_WAIT_SECONDS}s); owner:" >&2
      _build_lock_read_owner "$dir" "$remote" >&2 || true
      return 1
    fi
    sleep "$BUILD_LOCK_POLL_SECONDS"
    waited=$((waited + BUILD_LOCK_POLL_SECONDS))
  done
}

build_lock_release_all() {
  local i
  for (( i=${#BUILD_LOCK_HELD_DIRS[@]}-1; i>=0; i-- )); do
    _build_lock_remove_if_owner "${BUILD_LOCK_HELD_DIRS[$i]}" "${BUILD_LOCK_HELD_REMOTE[$i]}" "${BUILD_LOCK_HELD_STAMPS[$i]}" 2>/dev/null || true
  done
  BUILD_LOCK_HELD_DIRS=()
  BUILD_LOCK_HELD_REMOTE=()
  BUILD_LOCK_HELD_STAMPS=()
}
