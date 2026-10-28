#!/usr/bin/env bash
# cas.sh — content-addressed input cache for the universal export image build.
#
# Runs on the Docker host (bash, coreutils, curl, docker; no python needed).
# Reads the plan written by `lockctl.py plan` (TSV: id sha256 size|- filename
# url|- rolling(0/1) seeds|-), makes sure every pinned input is in the CAS at
# $CACHE/sha256/<sha256> (re-hashed on every run; corrupt objects are moved to
# $CACHE/quarantine, never deleted), and hardlinks exactly the planned inputs
# into $CTX/inputs/<sha256> for the Dockerfile's read-only bind mount.
#
# Source order for a missing input: --stage ID=SRC (a wrong file fails hard),
# then the lock's seeds (a wrong seed is skipped), then the url (unless
# --offline; a rolling url that now serves other bytes is reported as
# "upstream drifted"). SRC/seed forms: /abs/path, file:/abs/path,
# image:REF:/path/in/image (docker run --rm --network none --entrypoint cat),
# or an http(s)/file:// url.
#
# --check: read-only; prints "<id>\t<cached|missing|corrupt>\t<size>" lines and
# missing_bytes=N, exits 0 only when every input is cached and intact.
# Exit codes: 0 ok, 1 an input could not be satisfied, 64 usage.
# Env: CAS_DOCKER (default docker), CAS_RESERVE_BYTES (default 1 GiB headroom).
set -euo pipefail

usage() {
  sed -n '2,23p' "$0" | sed 's/^# \{0,1\}//' >&2
  echo "usage: cas.sh --plan FILE --cache DIR --ctx DIR [--stage ID=SRC]... [--offline] [--check]" >&2
  exit 64
}
die() { printf 'cas: %s\n' "$*" >&2; exit 1; }
usage_die() { printf 'cas: %s\n' "$*" >&2; exit 64; }
log() { printf 'cas: %s\n' "$*" >&2; }

PLAN="" CACHE="" CTX="" OFFLINE=0 CHECK=0 STAGES=""
DOCKER=${CAS_DOCKER:-docker}
RESERVE=${CAS_RESERVE_BYTES:-1073741824}
while [ $# -gt 0 ]; do
  case "$1" in
    --plan|--cache|--ctx|--stage)
      [ $# -ge 2 ] && [ -n "$2" ] || usage_die "$1 needs a value"
      case "$1" in
        --plan) PLAN=$2 ;;
        --cache) CACHE=$2 ;;
        --ctx) CTX=$2 ;;
        --stage)
          case "$2" in ?*=?*) STAGES="$STAGES$2"$'\n' ;; *) usage_die "--stage needs ID=SRC, got '$2'" ;; esac ;;
      esac
      shift 2 ;;
    --offline) OFFLINE=1; shift ;;
    --check) CHECK=1; shift ;;
    -h|--help) usage ;;
    *) usage_die "unknown argument '$1'" ;;
  esac
done
[ -n "$PLAN" ] && [ -n "$CACHE" ] && [ -n "$CTX" ] || usage_die "--plan, --cache and --ctx are required"
[ -f "$PLAN" ] || usage_die "plan $PLAN not found"

sha_of() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'
  else shasum -a 256 "$1" | awk '{print $1}'; fi
}
size_of() { wc -c < "$1" | tr -d ' '; }
is_hex64() { case "$1" in *[!0-9a-f]*) return 1 ;; esac; [ ${#1} -eq 64 ]; }

# --- load and validate the plan (bash 3.2: parallel indexed arrays) ---------
IDS=() SHAS=() SIZES=() URLS=() ROLL=() SEEDS=()
while IFS=$'\t' read -r id sha size _filename url rolling seeds || [ -n "${id:-}" ]; do
  [ -n "$id" ] || continue
  case "$id" in *[!a-z0-9.-]*|"") usage_die "plan: bad input id '$id'" ;; esac
  is_hex64 "$sha" || usage_die "plan: $id: bad sha256 '$sha'"
  case "$size" in -) ;; ''|*[!0-9]*) usage_die "plan: $id: bad size '$size'" ;; esac
  case "$rolling" in 0|1) ;; *) usage_die "plan: $id: rolling must be 0 or 1" ;; esac
  IDS+=("$id"); SHAS+=("$sha"); SIZES+=("$size"); URLS+=("$url"); ROLL+=("$rolling"); SEEDS+=("$seeds")
done < "$PLAN"
[ ${#IDS[@]} -gt 0 ] || usage_die "plan $PLAN is empty"

stage_for() { # id -> prints SRC if staged
  local line
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    [ "${line%%=*}" = "$1" ] && { printf '%s' "${line#*=}"; return 0; }
  done <<EOF
$STAGES
EOF
  return 1
}
while IFS= read -r line; do
  [ -n "$line" ] || continue
  found=0
  for id in "${IDS[@]}"; do [ "$id" = "${line%%=*}" ] && found=1; done
  [ $found = 1 ] || usage_die "--stage for unknown input '${line%%=*}'"
done <<EOF
$STAGES
EOF

intact() { # file sha size -> 0 if matching; sets GOT_SHA GOT_SIZE
  GOT_SIZE=$(size_of "$1")
  if [ "$3" != "-" ] && [ "$GOT_SIZE" != "$3" ]; then GOT_SHA="(size differs, not hashed)"; return 1; fi
  GOT_SHA=$(sha_of "$1")
  [ "$GOT_SHA" = "$2" ]
}

# --- pass 1: classify --------------------------------------------------------
STATUS=() MISSING_BYTES=0 UNKNOWN_SIZE=0
for i in "${!IDS[@]}"; do
  obj="$CACHE/sha256/${SHAS[$i]}"
  st=missing
  if [ -f "$obj" ]; then
    if intact "$obj" "${SHAS[$i]}" "${SIZES[$i]}"; then
      st=cached
    elif [ $CHECK = 1 ]; then
      st=corrupt
    else
      mkdir -p "$CACHE/quarantine"
      q="$CACHE/quarantine/${SHAS[$i]}.$(date +%s)"
      mv -f "$obj" "$q"
      log "${IDS[$i]}: cached object is corrupt (sha $GOT_SHA, size $GOT_SIZE); moved to quarantine $q"
    fi
  fi
  if [ $st != cached ]; then
    if [ "${SIZES[$i]}" = "-" ]; then UNKNOWN_SIZE=$((UNKNOWN_SIZE + 1)); else MISSING_BYTES=$((MISSING_BYTES + SIZES[i])); fi
  fi
  STATUS+=("$st")
  [ $CHECK = 1 ] && printf '%s\t%s\t%s\n' "${IDS[$i]}" "$st" "${SIZES[$i]}"
done

if [ $CHECK = 1 ]; then
  printf 'missing_bytes=%s\n' "$MISSING_BYTES"
  [ $UNKNOWN_SIZE = 0 ] || printf 'missing_unknown_size=%s\n' "$UNKNOWN_SIZE"
  for st in "${STATUS[@]}"; do [ "$st" = cached ] || exit 1; done
  exit 0
fi

# --- disk admission before any fetch ---------------------------------------
mkdir -p "$CACHE/sha256" "$CACHE/tmp" "$CTX/inputs"
if [ "$MISSING_BYTES" -gt 0 ] || [ "$UNKNOWN_SIZE" -gt 0 ]; then
  free_kb=$(df -Pk "$CACHE" | awk 'NR==2 {print $4}')
  need=$((MISSING_BYTES + RESERVE))
  if [ $((free_kb * 1024)) -lt "$need" ]; then
    die "disk admission: $CACHE has $((free_kb / 1024)) MiB free, needs $((need / 1048576)) MiB (missing inputs + reserve)"
  fi
fi

# --- pass 2: fetch -----------------------------------------------------------
TMPF=""
cleanup() { [ -z "$TMPF" ] || rm -f "$TMPF"; }
trap cleanup EXIT

fetch() { # src dest -> 0 if bytes were produced
  local src=$1 dest=$2 spec ref path
  case "$src" in
    *'<'*|*'>'*) log "skipping placeholder source $src"; return 1 ;;
    image:*)
      spec=${src#image:}
      case "$spec" in *:/*) ;; *) log "bad image source $src (want image:REF:/path)"; return 1 ;; esac
      ref=${spec%:/*}; path="/${spec##*:/}"
      "$DOCKER" run --rm --network none --entrypoint cat "$ref" "$path" > "$dest" 2>/dev/null ;;
    http://*|https://*|file://*)
      curl -fsSL --retry 3 --retry-delay 5 --connect-timeout 30 -o "$dest" "$src" ;;
    file:*) [ -f "${src#file:}" ] && cp "${src#file:}" "$dest" ;;
    /*) [ -f "$src" ] && cp "$src" "$dest" ;;
    *) log "unknown source form $src"; return 1 ;;
  esac
}

FAILED=0 FETCHED=0 HITS=0
for i in "${!IDS[@]}"; do
  id=${IDS[$i]} sha=${SHAS[$i]} size=${SIZES[$i]} obj="$CACHE/sha256/${SHAS[$i]}"
  if [ "${STATUS[$i]}" = cached ]; then
    log "$id: cached"
    HITS=$((HITS + 1))
    continue
  fi
  sources=() kinds=()
  if staged=$(stage_for "$id"); then sources+=("$staged"); kinds+=(stage); fi
  if [ "${SEEDS[$i]}" != "-" ] && [ -n "${SEEDS[$i]}" ]; then
    for s in ${SEEDS[$i]}; do sources+=("$s"); kinds+=(seed); done
  fi
  if [ "${URLS[$i]}" != "-" ]; then
    if [ $OFFLINE = 1 ]; then :; else sources+=("${URLS[$i]}"); kinds+=(url); fi
  fi
  done_ok=0 hard=0
  for j in ${sources[@]+"${!sources[@]}"}; do
    src=${sources[$j]} kind=${kinds[$j]}
    TMPF=$(mktemp "$CACHE/tmp/$sha.XXXXXX")
    if ! fetch "$src" "$TMPF"; then
      log "$id: $kind source $src unavailable"
      rm -f "$TMPF"; TMPF=""
      [ "$kind" = stage ] && { log "$id: explicit --stage failed"; hard=1; break; }
      continue
    fi
    if intact "$TMPF" "$sha" "$size"; then
      chmod 0444 "$TMPF"
      mv -f "$TMPF" "$obj"; TMPF=""
      if [ "$kind" = stage ]; then log "$id: fetched from staged $src"; else log "$id: fetched from $kind $src"; fi
      done_ok=1; FETCHED=$((FETCHED + 1))
      break
    fi
    case "$GOT_SHA" in *[!0-9a-f]*) GOT_SHA=$(sha_of "$TMPF") ;; esac
    rm -f "$TMPF"; TMPF=""
    if [ "$kind" = url ] && [ "${ROLL[$i]}" = 1 ]; then
      log "$id: upstream drifted: $src now serves sha256 $GOT_SHA size $GOT_SIZE; the lock pins $sha. Stage the pinned bytes (--stage $id=...) or re-pin deliberately."
      hard=1; break
    fi
    log "$id: $kind $src mismatch: got sha256 $GOT_SHA size $GOT_SIZE, lock pins $sha size $size"
    [ "$kind" = seed ] || { hard=1; break; }
  done
  if [ $done_ok = 0 ]; then
    FAILED=$((FAILED + 1))
    if [ $hard = 0 ]; then
      if [ $OFFLINE = 1 ] && [ "${URLS[$i]}" != "-" ]; then
        log "$id: not cached and --offline forbids fetching ${URLS[$i]}"
      else
        log "$id: no source produced the pinned bytes; stage them with --stage $id=SRC"
      fi
    fi
  fi
done

# --- ctx/inputs: exactly the planned digests ---------------------------------
for f in "$CTX/inputs"/* "$CTX/inputs"/.[!.]*; do
  [ -e "$f" ] || [ -L "$f" ] || continue
  name=${f##*/}
  if ! is_hex64 "$name" || [ ! -f "$f" ] || [ -L "$f" ]; then
    die "refusing to prune unexpected entry $f in ctx/inputs (remove it by hand)"
  fi
  keep=0
  for sha in "${SHAS[@]}"; do [ "$sha" = "$name" ] && keep=1; done
  [ $keep = 1 ] || { rm -f "$f"; log "pruned stale ctx input $name"; }
done
if [ $FAILED -gt 0 ]; then
  die "$FAILED input(s) unsatisfied; ctx/inputs left incomplete"
fi
for sha in "${SHAS[@]}"; do
  dest="$CTX/inputs/$sha"
  if ! ln -f "$CACHE/sha256/$sha" "$dest" 2>/dev/null; then
    if [ ! -f "$dest" ] || [ "$(size_of "$dest")" != "$(size_of "$CACHE/sha256/$sha")" ]; then
      cp -f "$CACHE/sha256/$sha" "$dest.part" && mv -f "$dest.part" "$dest"
    fi
  fi
done
log "${#IDS[@]} inputs: $HITS cached, $FETCHED fetched; ctx/inputs ready"
