#!/usr/bin/env bash
# Streamed over ssh by build.sh, BEFORE trusting/writing the remote context.
# Requires remote bash, GNU realpath (-m), timeout and df; no Python or jq.
set -euo pipefail

fail() { printf 'build-safety: %s\n' "$*" >&2; exit 1; }
uint() { [[ $1 =~ ^(0|[1-9][0-9]{0,15})$ ]]; }
probe() { timeout -k 2 10 "$@"; }

path_check() { # literal absolute path, including every existing ancestor
  local path=$1 component resolved
  [[ $path = /* && $path != *$'\n'* ]] || fail "invalid path: $path"
  component=$path
  while :; do
    [ ! -L "$component" ] || fail "symlink in remote path: $component"
    [ "$component" = / ] && break
    component=${component%/*}
    [ -n "$component" ] || component=/
    [ ! -e "$component" ] || [ -d "$component" ] || fail "not a directory: $component"
  done
  resolved=$(probe realpath -m -- "$path") || fail "realpath probe failed: $path"
  [ "$resolved" = "$path" ] || fail "noncanonical remote path: $path resolves to $resolved"
}

paths() { # root smoke-directory receipt-file (each may be absent)
  [ "$#" = 3 ] || fail "paths needs root, smoke directory and receipt file"
  local root=$1 smoke=$2 receipt=$3 path
  for path in "$root" "$root/ctx" "$root/ctx/inputs" "$root/cache" \
    "$root/cache/sha256" "$root/cache/tmp" "$root/cache/quarantine" \
    "$root/smoke" "$root/receipts" "$root/.build-lock" "$smoke"; do
    path_check "$path"
    [ ! -e "$path" ] || [ -d "$path" ] || fail "not a directory: $path"
  done
  path_check "$receipt"
  [ ! -e "$receipt" ] || [ -f "$receipt" ] || fail "not a regular file: $receipt"
}

exclusive() { # explicitly approved compose project/service pairs only
  local ids id row allowed ok project service oneoff job running found extra
  ids=$(probe docker ps --no-trunc --quiet) || fail "running-container probe failed"
  [ -n "$ids" ] || return 0
  while IFS= read -r id; do
    [[ $id =~ ^[a-f0-9]{64}$ ]] || fail "malformed running-container ID"
    # shellcheck disable=SC2016  # $key/$value are Go-template variables, not shell
    row=$(probe docker inspect --format \
      '{{.Id}}|{{.State.Running}}|{{with index .Config.Labels "com.docker.compose.project"}}{{.}}{{else}}-{{end}}|{{with index .Config.Labels "com.docker.compose.service"}}{{.}}{{else}}-{{end}}|{{with index .Config.Labels "com.docker.compose.oneoff"}}{{.}}{{else}}-{{end}}|{{range $key, $value := .Config.Labels}}{{if or (eq $key "runnerrunner.managed") (eq $key "runnerrunner.instance-id") (eq $key "runnerrunner.runner-name") (eq $key "runnerrunner.profile-id")}}job{{end}}{{end}}-' "$id") \
      || fail "container label probe failed: $id"
    [[ $row != *$'\n'* ]] || fail "malformed container labels: $id"
    IFS='|' read -r found running project service oneoff job extra <<< "$row"
    [ "$found" = "$id" ] && [ "$running" = true ] && [ "$oneoff" = False ] \
      && [ "$job" = - ] && [ -z "$extra" ] || fail "unapproved/job container: $id"
    case $service in server|postgres|host-worker) ;; *) fail "not an RR service: $id" ;; esac
    ok=0
    for allowed in "$@"; do
      [ "$project/$service" != "$allowed" ] || ok=1
    done
    [ "$ok" = 1 ] || fail "running container not explicitly allowed: $id ($project/$service)"
  done <<< "$ids"
}

docker_root() {
  local root
  root=$(probe docker info --format '{{.DockerRootDir}}') || fail "Docker-root probe failed"
  [[ $root = /* && $root != *$'\n'* ]] || fail "invalid Docker root"
  printf '%s\n' "$root"
}

disk_sample() { # validated-root startup|tail projected startup-min tail-min tail-percent
  local root=$1 mode=$2 projected=$3 minimum=$4 tail_min=$5 tail_pct=$6
  local data avail total extra tail pct required
  for data in "$projected" "$minimum" "$tail_min" "$tail_pct"; do
    uint "$data" || fail "invalid disk threshold"
  done
  [ "$minimum" -ge 107374182400 ] && [ "$tail_min" -ge 21474836480 ] \
    && [ "$tail_pct" -ge 10 ] && [ "$tail_pct" -le 100 ] || fail "unsafe disk threshold"
  data=$(probe df -B1 --output=avail,size -- "$root") || fail "Docker-root df probe failed"
  [[ $data = *$'\n'* ]] || fail "malformed df output"
  data=${data#*$'\n'}
  [[ $data != *$'\n'* ]] || fail "malformed df output"
  read -r avail total extra <<< "$data"
  uint "$avail" && uint "$total" && [ -z "$extra" ] && [ "$total" -gt 0 ] \
    && [ "$avail" -le "$total" ] || fail "malformed df counters"
  pct=$(( (tail_pct * total + 99) / 100 ))
  tail=$(( tail_min > pct ? tail_min : pct ))
  required=$tail
  if [ "$mode" = startup ]; then
    required=$(( projected + tail ))
    [ "$required" -ge "$minimum" ] || required=$minimum
  fi
  echo "docker root $root: $avail bytes free (required $required; $mode)"
  [ "$avail" -ge "$required" ] || fail "Docker-root free space below $mode floor"
}

disk() { # one-shot admission: resolve once, then sample that root
  local root
  root=$(docker_root) || return 1
  disk_sample "$root" "$@"
}

# Remote supervisor: one directly exec'd Docker CLI child, NOT a detached
# monitor. Polling lives on the remote host, not in the local SSH client.
# HUP/INT/TERM and any monitor failure cancel ONLY that child PID. Killing a CLI is
# NOT proof of daemon-side BuildKit/container shutdown; caller keeps UNKNOWN.
# The coordinator must prevent all competing admission. These are snapshots,
# not a host-wide scheduler lock or a filesystem symlink-race defense.
watch() { # phase seconds interval projected startup-min tail-min pct command
  local phase=$1 seconds=$2 interval=$3 projected=$4 minimum=$5 tail_min=$6 tail_pct=$7 command=$8
  local child="" rc=0 next=0 started=$SECONDS root
  uint "$seconds" && uint "$interval" && [ "$interval" -ge 1 ] \
    && [ "$interval" -le 30 ] || fail "invalid watchdog timing"
  # Resolve once per phase. Repeating/final samples need only bounded df,
  # not a responsive daemon. No daemon restart or root-path change is allowed
  # during the coordinator's exclusive window.
  root=$(docker_root) || return 75
  # In particular, recheck after all pre-submission work and before fork.
  (disk_sample "$root" tail "$projected" "$minimum" "$tail_min" "$tail_pct") || return 75
  # shellcheck disable=SC2329  # called via traps and on watchdog failure
  cancel_child() {
    if [ -n "${child:-}" ]; then
      # Bash may already have reaped a fast-exiting background process.
      # Check parentage before either signal, especially after the grace
      # interval, rather than signalling a PID that could have been reused.
      local parent
      parent=$(probe ps -o ppid= -p "$child" 2>/dev/null) || parent=""
      parent=${parent//[[:space:]]/}
      if [ "$parent" = "$$" ]; then kill -TERM "$child" 2>/dev/null || true; fi
      # Fixed grace, then KILL only our unreaped child PID. Never docker rm,
      # container-name killing, buildx prune, or claiming the daemon stopped.
      sleep 1
      parent=$(probe ps -o ppid= -p "$child" 2>/dev/null) || parent=""
      parent=${parent//[[:space:]]/}
      if [ "$parent" = "$$" ]; then kill -KILL "$child" 2>/dev/null || true; fi
      # Never wait forever for an uninterruptible client or a failed parent
      # probe. UNKNOWN already means the owner must establish shutdown.
      if kill -0 "$child" 2>/dev/null; then sleep 1; fi
      if kill -0 "$child" 2>/dev/null; then
        echo "STOP UNKNOWN: owned Docker CLI shutdown could not be confirmed (PID $child)" >&2
      else
        wait "$child" 2>/dev/null || true
      fi
      child=""
    fi
  }
  # shellcheck disable=SC2329
  interrupted() {
    trap '' HUP INT TERM
    echo "STOP UNKNOWN: $phase supervisor interrupted" >&2
    cancel_child
    exit 75
  }
  trap interrupted HUP INT TERM
  trap cancel_child EXIT
  # command is assembled only by build.sh; its final command MUST use exec.
  bash -c "$command" </dev/null &
  child=$!
  while kill -0 "$child" 2>/dev/null; do
    if [ "$seconds" != 0 ] && [ "$((SECONDS - started))" -ge "$seconds" ]; then
      echo "STOP UNKNOWN: $phase timeout; cancelling owned Docker CLI PID $child" >&2
      cancel_child
      return 75
    fi
    if [ "$SECONDS" -ge "$next" ]; then
      next=$((SECONDS + interval))
      # disk_sample uses fail/exit; isolate it so failure reaches this supervisor.
      if ! (disk_sample "$root" tail "$projected" "$minimum" "$tail_min" "$tail_pct"); then
        echo "STOP UNKNOWN: $phase watchdog; cancelling owned Docker CLI PID $child (daemon stop unconfirmed)" >&2
        cancel_child
        return 75
      fi
    fi
    sleep 1
  done
  wait "$child" || rc=$?
  child=""
  trap - HUP INT TERM EXIT
  # A fast command still gets a final probe. No successful completion may
  # clear UNKNOWN until the supervisor and this last probe have finished.
  (disk_sample "$root" tail "$projected" "$minimum" "$tail_min" "$tail_pct") || return 75
  return "$rc"
}

case ${1:-} in
  paths) shift; paths "$@" ;;
  exclusive) shift; exclusive "$@" ;;
  disk) shift; disk "$@" ;;
  watch) shift; watch "$@" ;;
  *) fail "unknown safety operation" ;;
esac
