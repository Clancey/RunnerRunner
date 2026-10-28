#!/usr/bin/env bash
# Build rr-universal-export on the Tower Docker host and smoke-test it.
#
#   build.sh [--host root@tower.local] [--tag TAG] [--stage ID=SRC]...
#            [--no-cache]
#            (--apple-pack-remote PATH [--apple-build B] | --allow-no-apple-smoke)
#   build.sh --dry-run    local validation, then print the remote commands; no ssh
#   build.sh --preflight  read-only checks on the host; builds nothing
#
# CANDIDATE-ONLY: creates exactly one new tag rr-universal-export:<TAG>
# (default ue-<UTC date>-<recipe digest[:12]>). Existing and protected tags
# (lock image.protected_tags) are refused; nothing is pushed or pruned. The
# build writes a private temporary tag ue-tmp-<TAG>-<pid>; only after a
# successful build, and a recheck that <TAG> still does not exist, is it
# tagged <TAG> and the temporary tag removed. Promotion (an alias tag plus a RunnerRunner target change) is a
# separate approved step outside this script.
#
# Flow: lockctl validate + check-dockerfile + plan (local) -> locks (local,
# then remote) -> rsync the context -> cas.sh puts every pinned input in the
# CAS and hardlinks it into ctx/inputs/<sha256> (--stage sources, lock seeds,
# then upstream) -> startup disk admission -> docker build (BuildKit,
# timeout) -> final tag -> receipt -> smoke (docker run --network none; the
# Apple link smoke admits the SDK pack given by --apple-pack-remote) -> fetch
# smoke-report.json and smoke-report-logs.tar.gz -> smoke/report.py validate.
#
# Remote layout under UE_REMOTE_ROOT (/mnt/user/appdata/rr-builds/universal-export):
#   ctx/ (build context)  cache/ (CAS; persistent, never pruned)
#   smoke/<tag>/smoke-report.json (+ smoke-report-logs.tar.gz)
#   receipts/<tag>.json  .build-lock/
#
# Exit: 0 built, smoke rc 0 and the report validates as promotable for the
# pack (needs a pack); 2 built, smoke rc 2 and the report validates as a
# no-pack pass (--allow-no-apple-smoke only); 1 any other outcome, incl. a
# missing, malformed or incomplete report or a missing log archive; 64 usage.
# Env: BUILD_TIMEOUT_SECONDS (14400; 0 disables), SMOKE_TIMEOUT_SECONDS
# (7200), BUILD_PROJECTED_BYTES (25 GiB; 0 disables admission),
# BUILD_TAIL_MIN_BYTES (20 GiB), BUILD_TAIL_PCT (10), UE_REMOTE_ROOT.
# bash 3.2 compatible (runs from macOS).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=safe_rm.sh
. "$HERE/safe_rm.sh"

usage() { sed -n '2,38p' "$0" | sed 's/^# \{0,1\}//' >&2; exit 64; }
die() { printf 'build: %s\n' "$*" >&2; exit 1; }
usage_die() { printf 'build: %s\n' "$*" >&2; exit 64; }
lockctl() { python3 "$HERE/lib/lockctl.py" --lock "$HERE/toolchain.lock.json" "$@"; }
q() { printf '%q' "$1"; }

HOST=root@tower.local
REMOTE_ROOT=${UE_REMOTE_ROOT:-/mnt/user/appdata/rr-builds/universal-export}
TAG=""
MODE=build
NO_CACHE=""
STAGE_ARGS=""
STAGE_IDS=" "
APPLE_PACK=""
APPLE_BUILD=""
ALLOW_NO_APPLE=0
BUILD_TIMEOUT_SECONDS=${BUILD_TIMEOUT_SECONDS:-14400}
SMOKE_TIMEOUT_SECONDS=${SMOKE_TIMEOUT_SECONDS:-7200}
while [ $# -gt 0 ]; do
  case $1 in
    --host|--tag|--stage|--apple-pack-remote|--apple-build)
      [ $# -ge 2 ] && [ -n "$2" ] || usage_die "$1 needs a value"
      case $1 in
        --host) HOST=$2 ;;
        --tag) TAG=$2 ;;
        --stage)
          case $2 in ?*=?*) ;; *) usage_die "--stage needs ID=SRC, got '$2'" ;; esac
          STAGE_ARGS="$STAGE_ARGS --stage $(q "$2")"; STAGE_IDS="$STAGE_IDS${2%%=*} " ;;
        --apple-pack-remote) APPLE_PACK=$2 ;;
        --apple-build) APPLE_BUILD=$2 ;;
      esac
      shift 2 ;;
    --no-cache) NO_CACHE=--no-cache; shift ;;
    --dry-run) MODE=dry-run; shift ;;
    --preflight) MODE=preflight; shift ;;
    --allow-no-apple-smoke) ALLOW_NO_APPLE=1; shift ;;
    -h|--help) usage ;;
    *) usage_die "unknown argument '$1'" ;;
  esac
done
case $BUILD_TIMEOUT_SECONDS$SMOKE_TIMEOUT_SECONDS in *[!0-9]*) usage_die "timeouts must be whole seconds" ;; esac

# ---- local validation (no network, no host) ----------------------------------
lockctl validate || die "toolchain.lock.json is invalid"
lockctl check-dockerfile "$HERE/Dockerfile" || die "Dockerfile does not follow the lock rules"
IMAGE=$(lockctl get --raw image.name)
PLATFORM=$(lockctl get --raw image.platform)
BASE_IMAGE=$(lockctl get --raw image.base)
RECIPE=$(lockctl recipe-digest)
[ -n "$TAG" ] || TAG=$(lockctl default-tag)
lockctl check-tag -- "$TAG" || usage_die "refusing tag '$TAG'"
REF="$IMAGE:$TAG"
TMP_TAG="ue-tmp-$TAG-$$"
lockctl check-tag -- "$TMP_TAG" 2>/dev/null || usage_die "tag '$TAG' is too long for the temporary build tag $TMP_TAG"
TMP_REF="$IMAGE:$TMP_TAG"

if [ -z "$APPLE_BUILD" ]; then
  APPLE_BUILD=$(python3 -c 'import json,sys; k=list(json.load(open(sys.argv[1]))["apple_sdk"]["known_packs"]); print(k[0] if len(k)==1 else "")' "$HERE/toolchain.lock.json")
fi
if [ -n "$APPLE_PACK" ]; then
  [ -n "$APPLE_BUILD" ] || usage_die "--apple-build is required (the lock knows more than one pack)"
  APPLE_PACK_SHA=$(lockctl get --raw "apple_sdk.known_packs.$APPLE_BUILD.tarball_sha256" 2>/dev/null) \
    || usage_die "Apple SDK build $APPLE_BUILD is not in the lock's apple_sdk.known_packs"
  [ "$ALLOW_NO_APPLE" = 0 ] || usage_die "--apple-pack-remote and --allow-no-apple-smoke are exclusive"
elif [ "$ALLOW_NO_APPLE" = 0 ] && [ "$MODE" = build ]; then
  usage_die "--apple-pack-remote PATH is required (the Apple link smoke gates promotion); --allow-no-apple-smoke builds a candidate that can never be promoted"
fi

BUILD_ARGS=""
while IFS= read -r kv; do
  [ -n "$kv" ] && BUILD_ARGS="$BUILD_ARGS --build-arg $(q "$kv")"
done <<EOF
$(lockctl build-args)
EOF

CTX="$REMOTE_ROOT/ctx"
CACHE="$REMOTE_ROOT/cache"
SMOKE_DIR="$REMOTE_ROOT/smoke/$TAG"
RECEIPT="$REMOTE_ROOT/receipts/$TAG.json"
REMOTE_LOCK="$REMOTE_ROOT/.build-lock"
SMOKE_NAME="ue-smoke-$TAG"
CAS_CMD="cd $(q "$CTX") && bash ./cas.sh --plan ./.plan.tsv --cache $(q "$CACHE") --ctx $(q "$CTX")$STAGE_ARGS"
DOCKER_BUILD="DOCKER_BUILDKIT=1 docker build --platform $(q "$PLATFORM") $NO_CACHE$BUILD_ARGS -t $(q "$TMP_REF") ."
if [ -n "$APPLE_PACK" ]; then
  SMOKE_APPLE="-v $(q "$APPLE_PACK"):/run/apple-sdk/pack.tar.zst:ro"
  SMOKE_ARGS="--apple-pack /run/apple-sdk/pack.tar.zst --apple-build $(q "$APPLE_BUILD")"
else
  SMOKE_APPLE=""
  SMOKE_ARGS="--allow-no-apple-smoke"
fi
SMOKE_RUN="docker run --rm --name $(q "$SMOKE_NAME") --network none --entrypoint /opt/universal-export/smoke/run_smoke.sh $SMOKE_APPLE -v $(q "$SMOKE_DIR"):/smoke-out $(q "$REF") --mode offline --sets all --work /tmp/ue-smoke --report /smoke-out/smoke-report.json $SMOKE_ARGS"
RSYNC_EXCLUDES=(--exclude /inputs/ --exclude /tests/ --exclude /mac/ --exclude /logs/ --exclude /README.md
  --exclude /.gitignore --exclude /.build-lock/ --exclude __pycache__/ --exclude '*.pyc' --exclude .DS_Store
  --exclude /ruby/vendor/ --exclude /ruby/.bundle/)

if [ "$MODE" = dry-run ]; then
  PLAN=$(mktemp)
  lockctl plan > "$PLAN"
  echo "image:   $REF ($PLATFORM)"
  echo "base:    $BASE_IMAGE"
  echo "recipe:  $RECIPE"
  echo "inputs:  $(wc -l < "$PLAN" | tr -d ' ') planned"
  echo "apple:   ${APPLE_PACK:-none (not promotable)} build=${APPLE_BUILD:-?}"
  echo "## would run on $HOST:"
  echo "rsync -a --delete ${RSYNC_EXCLUDES[*]} $HERE/ $HOST:$CTX/"
  echo "$CAS_CMD"
  echo "cd $(q "$CTX") && $DOCKER_BUILD"
  echo "! docker image inspect $(q "$REF") && docker tag $(q "$TMP_REF") $(q "$REF") && docker rmi $(q "$TMP_REF")"
  echo "docker run --rm --network none --entrypoint cat $(q "$REF") /opt/universal-export/receipt.json > $(q "$RECEIPT")"
  echo "mkdir -p $(q "$SMOKE_DIR") && $SMOKE_RUN"
  rm -f "$PLAN"
  exit 0
fi

ssh_host() { ssh -o BatchMode=yes "$HOST" "$@"; }

# Startup disk admission (not runtime enforcement): free bytes on the docker
# root must cover one cold attempt (layers ~14 GiB + the BuildKit copy of the
# ~4 GiB inputs + a smoke container) plus max(20 GiB, 10% of the filesystem).
PROJECTED=${BUILD_PROJECTED_BYTES:-26843545600}
TAIL_MIN=${BUILD_TAIL_MIN_BYTES:-21474836480}
TAIL_PCT=${BUILD_TAIL_PCT:-10}
disk_admission() { # prints the verdict; returns 1 when below the requirement
  local root df avail total pct tail req
  root=$(ssh_host "docker info --format '{{.DockerRootDir}}'")
  df=$(ssh_host "df -B1 --output=avail,size $(q "$root")" | tail -n 1)
  avail=$(printf '%s' "$df" | awk '{print $1}' | tr -dc '0-9')
  total=$(printf '%s' "$df" | awk '{print $2}' | tr -dc '0-9')
  [ -n "$avail" ] && [ -n "$total" ] || { echo "could not measure free bytes on $HOST:$root"; return 1; }
  pct=$(( TAIL_PCT * total / 100 ))
  tail=$(( TAIL_MIN > pct ? TAIL_MIN : pct ))
  req=$(( PROJECTED + tail ))
  if [ "$avail" -lt "$req" ]; then
    echo "docker root $root: $avail bytes free, below $req (projection $PROJECTED + tail $tail)"; return 1
  fi
  echo "docker root $root: $avail bytes free (required $req = projection $PROJECTED + tail $tail)"
}

if [ "$MODE" = preflight ]; then
  BAD=0
  chk() { # label status detail
    printf '%-14s %-5s %s\n' "$1" "$2" "$3"
    [ "$2" != FAIL ] || BAD=1
  }
  ssh_host true || die "cannot ssh to $HOST"
  SV=$(ssh_host "docker version --format '{{.Server.Version}}'" 2>/dev/null || true)
  if [ "${SV%%.*}" -ge 23 ] 2>/dev/null; then chk docker ok "server $SV"; else chk docker FAIL "server '${SV:-?}' (need >= 23 for the built-in RUN --mount frontend)"; fi
  if BX=$(ssh_host "docker buildx version" 2>/dev/null); then chk buildx ok "$BX"; else chk buildx FAIL "docker buildx missing (DOCKER_BUILDKIT=1 needs it)"; fi
  for t in timeout rsync curl sha256sum df; do
    if ssh_host "command -v $t" >/dev/null 2>&1; then chk "$t" ok ""; else chk "$t" FAIL "not on $HOST"; fi
  done
  if ssh_host "docker image inspect $(q "$REF")" >/dev/null 2>&1; then chk tag FAIL "$REF already exists (tags are unique)"; else chk tag ok "$REF is new"; fi
  if ssh_host "test -e $(q "$REMOTE_LOCK")"; then
    chk lock FAIL "$REMOTE_LOCK exists: $(ssh_host "cat $(q "$REMOTE_LOCK")/owner $(q "$REMOTE_LOCK")/unknown 2>/dev/null" | tr '\n' ' ')"
  else chk lock ok "free"; fi
  if ssh_host "docker image inspect $(q "$BASE_IMAGE")" >/dev/null 2>&1; then chk base ok "present"; else chk base warn "not present; docker build pulls the digest-pinned base"; fi
  if D=$(disk_admission); then chk disk ok "$D"; else chk disk FAIL "$D"; fi
  if [ -n "$APPLE_PACK" ]; then
    GOT=$(ssh_host "sha256sum $(q "$APPLE_PACK")" 2>/dev/null | awk '{print $1}' || true)
    if [ "$GOT" = "$APPLE_PACK_SHA" ]; then chk apple-pack ok "$APPLE_PACK matches build $APPLE_BUILD"; else chk apple-pack FAIL "$APPLE_PACK sha256 '${GOT:-unreadable}' != lock $APPLE_PACK_SHA"; fi
  else chk apple-pack warn "none given: the candidate would not be promotable"; fi
  # CAS check against a throwaway copy of cas.sh and the plan (nothing in
  # ctx/ or cache/ is written).
  T=$(ssh_host "mktemp -d")
  ssh_host "cat > $(q "$T")/cas.sh" < "$HERE/cas.sh"
  lockctl plan | ssh_host "cat > $(q "$T")/plan.tsv"
  CHECK=$(ssh_host "bash $(q "$T")/cas.sh --check --plan $(q "$T")/plan.tsv --cache $(q "$CACHE") --ctx $(q "$T")/ctx" 2>&1 || true)
  ssh_host "rm -rf $(q "$T")"
  printf '%s\n' "$CHECK" | sed 's/^/  cas: /'
  NEED=""
  for id in $(printf '%s\n' "$CHECK" | awk -F'\t' '$2 != "cached" && NF == 3 {print $1}'); do
    case "$STAGE_IDS" in *" $id "*) continue ;; esac
    ROW=$(lockctl plan | awk -F'\t' -v id="$id" '$1 == id')
    URL=$(printf '%s' "$ROW" | cut -f5); ROLL=$(printf '%s' "$ROW" | cut -f6); SEEDS=$(printf '%s' "$ROW" | cut -f7)
    if [ "$URL" = - ] && [ "$SEEDS" = - ]; then NEED="$NEED $id(no-source)"
    elif [ "$ROLL" = 1 ]; then NEED="$NEED $id(rolling-upstream)"; fi
  done
  if [ -n "$NEED" ]; then chk inputs warn "missing and not staged:$NEED (no-source must be staged; rolling upstreams may have drifted, see the packet)"; else chk inputs ok ""; fi
  [ "$BAD" = 0 ] || die "preflight FAILED"
  echo "preflight ok: $REF on $HOST"
  exit 0
fi

# ---- build -------------------------------------------------------------------
mkdir -p "$HERE/logs"
LOG="$HERE/logs/$(date +%Y%m%d-%H%M%S)-$TAG.log"
exec > >(tee "$LOG") 2>&1
echo "build: $REF on $HOST (recipe $RECIPE)"

# Locks, markers and STOP UNKNOWN handling: see build-lock.sh. Once the build
# may have been submitted, only its own rc=0 releases the locks.
# shellcheck source=build-lock.sh
. "$HERE/build-lock.sh"
BUILD_LOCK_SSH_HOST=$HOST
BUILD_STOP_UNKNOWN=0
BUILD_STOP_REASON=""
BUILD_SUBMITTED=0
# shellcheck disable=SC2329  # invoked by the EXIT trap
build_on_exit() {
  trap '' INT TERM
  if [ "$BUILD_STOP_UNKNOWN" = 1 ]; then
    build_lock_mark_all_unknown "${BUILD_STOP_REASON:-unconfirmed-stop}" \
      || echo "warning: could not update every unknown marker" >&2
    echo "STOP UNKNOWN: locks retained and marked; a human must confirm no build is running, then remove the marker and lock" >&2
    return
  fi
  build_lock_release_all
}
trap 'build_on_exit' EXIT
trap 'if [ "$BUILD_SUBMITTED" != 1 ]; then build_lock_clear_all_unknown 2>/dev/null || true; else BUILD_STOP_UNKNOWN=1; BUILD_STOP_REASON="interrupted after submission"; fi; exit 130' INT TERM
build_lock_acquire "$HERE/.build-lock" "local staging ($HERE)" 0

lockctl plan > "$HERE/.plan.tsv"
ssh_host "mkdir -p $(q "$CTX") $(q "$CACHE") $(q "$REMOTE_ROOT/smoke") $(q "$REMOTE_ROOT/receipts")"
build_lock_acquire "$REMOTE_LOCK" "remote context ($HOST:$REMOTE_ROOT)" 1

echo "Syncing the build context to $HOST:$CTX ..."
rsync -a --delete -e "ssh -o BatchMode=yes" "${RSYNC_EXCLUDES[@]}" "$HERE/" "$HOST:$CTX/"

if ssh_host "docker image inspect $(q "$REF")" >/dev/null 2>&1; then
  die "$REF already exists on $HOST; candidate tags are unique, pick a new one"
fi

echo "Staging inputs (cas.sh) ..."
ssh_host "$CAS_CMD" || die "cas.sh could not stage every input (see above; --stage ID=SRC for inputs without a usable source)"

TIMEOUT=""
if [ "$BUILD_TIMEOUT_SECONDS" != 0 ]; then
  ssh_host "command -v timeout" >/dev/null 2>&1 || die "timeout(1) missing on $HOST (or set BUILD_TIMEOUT_SECONDS=0)"
  TIMEOUT="timeout -k 30 $BUILD_TIMEOUT_SECONDS "
fi
if [ "$PROJECTED" != 0 ]; then
  disk_admission || die "startup disk admission failed; nothing was built"
fi

echo "Building $REF (timeout ${BUILD_TIMEOUT_SECONDS}s) ..."
if ! build_lock_mark_all_unknown "build submitted, completion unconfirmed"; then
  echo "refusing to submit: could not persist the in-flight marker in every held lock" >&2
  build_lock_clear_all_unknown || true
  exit 1
fi
BUILD_SUBMITTED=1
set +e
ssh_host "cd $(q "$CTX") && $TIMEOUT$DOCKER_BUILD"
BUILD_RC=$?
set -e
if [ "$BUILD_RC" != 0 ]; then
  build_lock_classify_stop "$BUILD_RC"
  echo "BUILD FAILED (rc=$BUILD_RC): STOP UNKNOWN - ${BUILD_STOP_REASON}; the daemon-side build may still be running" >&2
  echo "inspect on $HOST: docker ps, docker buildx du, and $LOG before any retry" >&2
  BUILD_STOP_UNKNOWN=1
  exit 1
fi
if ! build_lock_clear_all_unknown; then
  BUILD_STOP_UNKNOWN=1
  BUILD_STOP_REASON="marker clear failed after confirmed completion rc=0"
  echo "BUILD OK but the in-flight marker could not be cleared: locks retained; $REF is unverified (no receipt or smoke ran)" >&2
  exit 1
fi

# Move the candidate from the private temporary tag to its final tag, unless
# that tag appeared while building (then the build stays under TMP_REF).
if ssh_host "docker image inspect $(q "$REF")" >/dev/null 2>&1; then
  die "$REF appeared on $HOST during the build; refusing to retag (the build is kept as $TMP_REF)"
fi
ssh_host "docker tag $(q "$TMP_REF") $(q "$REF")" || die "docker tag $TMP_REF $REF failed (the build is kept as $TMP_REF)"
ssh_host "docker rmi $(q "$TMP_REF")" >/dev/null || die "could not remove the temporary tag $TMP_REF (the candidate is $REF)"

echo "Receipt ..."
ssh_host "docker run --rm --network none --entrypoint cat $(q "$REF") /opt/universal-export/receipt.json > $(q "$RECEIPT")" \
  || die "no receipt in $REF (the image is kept as evidence)"
ssh_host "cat $(q "$RECEIPT")" > "$HERE/logs/$TAG.receipt.json"

echo "Smoke (network none, timeout ${SMOKE_TIMEOUT_SECONDS}s) ..."
STO=""
[ "$SMOKE_TIMEOUT_SECONDS" = 0 ] || STO="timeout -k 30 $SMOKE_TIMEOUT_SECONDS "
set +e
ssh_host "mkdir -p $(q "$SMOKE_DIR") && $STO$SMOKE_RUN"
SMOKE_RC=$?
set -e
if [ "$SMOKE_RC" = 124 ] || [ "$SMOKE_RC" = 137 ] || [ "$SMOKE_RC" = 255 ]; then
  ssh_host "docker rm -f $(q "$SMOKE_NAME")" >/dev/null 2>&1 || true
fi
# Fail closed: the exit code needs the smoke rc, the fetched report validated
# against the lock (smoke/report.py validate) and the fetched log archive.
REPORT_LOCAL="$HERE/logs/$TAG.smoke-report.json"
LOGS_LOCAL="$HERE/logs/$TAG.smoke-logs.tar.gz"
VERDICT=""
if ! ssh_host "cat $(q "$SMOKE_DIR")/smoke-report.json" > "$REPORT_LOCAL" 2>/dev/null; then
  rm -f "$REPORT_LOCAL"
  VERDICT="smoke-report.json could not be fetched"
fi
if ! ssh_host "cat $(q "$SMOKE_DIR")/smoke-report-logs.tar.gz" > "$LOGS_LOCAL" 2>/dev/null \
  || ! tar -tzf "$LOGS_LOCAL" >/dev/null 2>&1; then
  rm -f "$LOGS_LOCAL"
  VERDICT="${VERDICT:+$VERDICT; }smoke-report-logs.tar.gz could not be fetched or is not a gzip tar"
fi
if [ -n "$APPLE_PACK" ]; then
  VALIDATE=(--apple-build "$APPLE_BUILD")
  WANT_RC=0
else
  VALIDATE=(--no-apple-pack)
  WANT_RC=2
fi
if [ -z "$VERDICT" ] && ! python3 "$HERE/smoke/report.py" validate --report "$REPORT_LOCAL" \
  --lock "$HERE/toolchain.lock.json" "${VALIDATE[@]}"; then
  VERDICT="smoke-report.json does not pass report.py validate (see above)"
fi
[ "$SMOKE_RC" = "$WANT_RC" ] || VERDICT="${VERDICT:+$VERDICT; }smoke rc $SMOKE_RC, want $WANT_RC"
ID=$(ssh_host "docker image inspect --format '{{.Id}}' $(q "$REF")" || echo "?")
if [ -z "$VERDICT" ] && [ -n "$APPLE_PACK" ]; then
  echo "RESULT: $REF ($ID) built; smoke PASSED; PROMOTABLE (promotion is a separate approved step)"; exit 0
fi
if [ -z "$VERDICT" ]; then
  echo "RESULT: $REF ($ID) built; smoke passed WITHOUT the Apple link smoke; NOT promotable"; exit 2
fi
echo "RESULT: $REF ($ID) built; smoke FAILED ($VERDICT; apple pack: ${APPLE_PACK:-none}); tag kept as evidence, retry with a new tag" >&2
exit 1
