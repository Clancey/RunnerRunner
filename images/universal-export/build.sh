#!/usr/bin/env bash
# Build rr-universal-export on the Tower Docker host and smoke-test it.
#
#   build.sh [--host root@tower.local] [--tag TAG] [--stage ID=SRC]...
#            [--no-cache] [--remote-root PATH]
#            [--allow-rr-service PROJECT/SERVICE]...
#            (--apple-pack-remote PATH [--apple-build B] | --allow-no-apple-smoke)
#   build.sh --dry-run    local validation, then print the remote commands; no ssh
#   build.sh --preflight  read-only checks on the host; builds nothing
#
# CANDIDATE-ONLY: creates exactly one new tag rr-universal-export:<TAG>
# (default ue-<UTC date>-<recipe digest[:12]>). Existing and protected tags
# (lock image.protected_tags) are refused; nothing is pushed or pruned. The
# build writes a private temporary tag ue-tmp-<TAG>-<pid>; only after a
# successful build, and a recheck that <TAG> still does not exist, is it
# tagged <TAG>. The temporary tag is removed only after verified smoke.
# Promotion (an alias tag plus a RunnerRunner target change) is a
# separate approved step outside this script.
#
# Flow: lockctl validate + check-dockerfile + plan (local) -> locks (local,
# then remote) -> rsync the context -> cas.sh puts every pinned input in the
# CAS and hardlinks it into ctx/inputs/<sha256> (--stage sources, lock seeds,
# then upstream) -> disk/exclusivity admission -> supervised docker build
# (BuildKit, timeout + disk watchdog) -> final tag -> receipt -> supervised
# smoke (docker run --network none; the
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
# (7200), BUILD_PROJECTED_BYTES (25 GiB; 0 does NOT disable admission),
# BUILD_START_MIN_BYTES (100 GiB minimum), BUILD_TAIL_MIN_BYTES (20 GiB minimum),
# BUILD_TAIL_PCT (10 minimum), BUILD_WATCHDOG_SECONDS (30; range 1..30).
# UE_REMOTE_ROOT must be the default unless --remote-root is explicitly given.
# No running containers allowed by default. Allow-list entries require exact
# compose project/service + oneoff=False labels and no RR job labels; only
# server, postgres, host-worker are eligible. Coordinator must hold CI admission.
# bash 3.2 compatible (runs from macOS).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=safe_rm.sh
. "$HERE/safe_rm.sh"

usage() { sed -n '2,/^set -/p' "$0" | sed '$d; s/^# \{0,1\}//' >&2; exit 64; }
die() { printf 'build: %s\n' "$*" >&2; exit 1; }
usage_die() { printf 'build: %s\n' "$*" >&2; exit 64; }
lockctl() { python3 "$HERE/lib/lockctl.py" --lock "$HERE/toolchain.lock.json" "$@"; }
q() { printf '%q' "$1"; }

HOST=root@tower.local
DEFAULT_REMOTE_ROOT=/mnt/user/appdata/rr-builds/universal-export
REMOTE_ROOT=${UE_REMOTE_ROOT-$DEFAULT_REMOTE_ROOT}
ROOT_EXPLICIT=0
ALLOW_RR_SERVICES=()
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
PROJECTED=${BUILD_PROJECTED_BYTES:-26843545600}
START_MIN=${BUILD_START_MIN_BYTES:-107374182400}
TAIL_MIN=${BUILD_TAIL_MIN_BYTES:-21474836480}
TAIL_PCT=${BUILD_TAIL_PCT:-10}
WATCHDOG_SECONDS=${BUILD_WATCHDOG_SECONDS:-30}
while [ $# -gt 0 ]; do
  case $1 in
    --host|--tag|--stage|--apple-pack-remote|--apple-build|--remote-root|--allow-rr-service)
      [ $# -ge 2 ] && [ -n "$2" ] || usage_die "$1 needs a value"
      case $1 in
        --host) HOST=$2 ;;
        --tag) TAG=$2 ;;
        --remote-root) REMOTE_ROOT=$2; ROOT_EXPLICIT=1 ;;
        --allow-rr-service)
          [[ $2 =~ ^[a-z0-9][a-z0-9_-]*/(server|postgres|host-worker)$ ]] \
            || usage_die "--allow-rr-service needs an approved PROJECT/{server,postgres,host-worker}"
          ALLOW_RR_SERVICES+=("$2") ;;
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
for VALUE in "$BUILD_TIMEOUT_SECONDS" "$SMOKE_TIMEOUT_SECONDS" "$PROJECTED" "$START_MIN" "$TAIL_MIN" "$TAIL_PCT" "$WATCHDOG_SECONDS"; do
  [[ $VALUE =~ ^(0|[1-9][0-9]{0,15})$ ]] || usage_die "thresholds/timeouts must be canonical nonnegative integers (at most 16 digits)"
done
[ "$START_MIN" -ge 107374182400 ] && [ "$TAIL_MIN" -ge 21474836480 ] \
  && [ "$TAIL_PCT" -ge 10 ] && [ "$TAIL_PCT" -le 100 ] \
  && [ "$WATCHDOG_SECONDS" -ge 1 ] && [ "$WATCHDOG_SECONDS" -le 30 ] \
  || usage_die "startup >=100 GiB, tail >=20 GiB and 10..100%, watchdog 1..30 seconds required"
[ "$REMOTE_ROOT" = "$DEFAULT_REMOTE_ROOT" ] || [ "$ROOT_EXPLICIT" = 1 ] \
  || usage_die "UE_REMOTE_ROOT override requires explicit --remote-root PATH"
# Deliberately narrow characters and >=4 path components: never a broad
# destination such as /, /mnt/user, /tmp or a shell/rsync option. The remote
# guard additionally rejects ALL symlink ancestors and canonical mismatches.
[[ $REMOTE_ROOT =~ ^(/[a-zA-Z0-9_-][a-zA-Z0-9._-]*){4,}$ ]] \
  || usage_die "--remote-root must be a narrow canonical absolute path (at least four components)"

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
DOCKER_BUILD="exec env DOCKER_BUILDKIT=1 docker build --platform $(q "$PLATFORM") $NO_CACHE$BUILD_ARGS -t $(q "$TMP_REF") ."
if [ -n "$APPLE_PACK" ]; then
  SMOKE_APPLE="-v $(q "$APPLE_PACK"):/run/apple-sdk/pack.tar.zst:ro"
  SMOKE_ARGS="--apple-pack /run/apple-sdk/pack.tar.zst --apple-build $(q "$APPLE_BUILD")"
else
  SMOKE_APPLE=""
  SMOKE_ARGS="--allow-no-apple-smoke"
fi
SMOKE_RUN="exec docker run --rm --name $(q "$SMOKE_NAME") --network none --entrypoint /opt/universal-export/smoke/run_smoke.sh $SMOKE_APPLE -v $(q "$SMOKE_DIR"):/smoke-out $(q "$REF") --mode offline --sets all --work /tmp/ue-smoke --report /smoke-out/smoke-report.json $SMOKE_ARGS"
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
  echo "## remote path guards before mkdir/rsync/receipt/smoke; fail closed on probe errors"
  echo "## exclusivity: ${ALLOW_RR_SERVICES[*]:-no running containers allowed}"
  echo "## startup >=$START_MIN bytes; build/receipt/smoke watchdog every ${WATCHDOG_SECONDS}s, tail max($TAIL_MIN bytes, $TAIL_PCT%)"
  echo "rsync -a --delete ${RSYNC_EXCLUDES[*]} $HERE/ $HOST:$CTX/"
  echo "$CAS_CMD"
  echo "cd $(q "$CTX") && $DOCKER_BUILD"
  echo "! docker image inspect $(q "$REF") && docker tag $(q "$TMP_REF") $(q "$REF")"
  echo "docker run --rm --network none --entrypoint cat $(q "$REF") /opt/universal-export/receipt.json > $(q "$RECEIPT")"
  echo "mkdir -p $(q "$SMOKE_DIR") && $SMOKE_RUN"
  echo "## only after verified smoke and completed watchdog:"
  echo "docker rmi $(q "$TMP_REF")"
  rm -f "$PLAN"
  exit 0
fi

ssh_host() { ssh -o BatchMode=yes -o ConnectTimeout=10 -o ServerAliveInterval=10 -o ServerAliveCountMax=3 "$HOST" "$@"; }
safety() { # execute the LOCAL helper, never an untrusted pre-existing remote file
  local command="bash -s --" arg
  for arg in "$@"; do command="$command $(q "$arg")"; done
  ssh_host "$command" < "$HERE/build-safety.sh"
}
guard_paths() { safety paths "$REMOTE_ROOT" "$@"; }
disk_admission() { safety disk startup "$PROJECTED" "$START_MIN" "$TAIL_MIN" "$TAIL_PCT"; }
exclusive_admission() { safety exclusive ${ALLOW_RR_SERVICES[@]+"${ALLOW_RR_SERVICES[@]}"}; }
supervise() { # phase timeout command
  safety watch "$1" "$2" "$WATCHDOG_SECONDS" "$PROJECTED" "$START_MIN" "$TAIL_MIN" "$TAIL_PCT" "$3"
}

if [ "$MODE" = preflight ]; then
  BAD=0
  chk() { # label status detail
    printf '%-14s %-5s %s\n' "$1" "$2" "$3"
    [ "$2" != FAIL ] || BAD=1
  }
  ssh_host true || die "cannot ssh to $HOST"
  guard_paths "$SMOKE_DIR" "$RECEIPT" || die "remote path admission failed"
  if X=$(exclusive_admission 2>&1); then chk exclusive ok "$X"; else chk exclusive FAIL "$X"; fi
  SV=$(ssh_host "docker version --format '{{.Server.Version}}'" 2>/dev/null || true)
  if [ "${SV%%.*}" -ge 23 ] 2>/dev/null; then chk docker ok "server $SV"; else chk docker FAIL "server '${SV:-?}' (need >= 23 for the built-in RUN --mount frontend)"; fi
  if BX=$(ssh_host "docker buildx version" 2>/dev/null); then chk buildx ok "$BX"; else chk buildx FAIL "docker buildx missing (DOCKER_BUILDKIT=1 needs it)"; fi
  for t in timeout rsync curl sha256sum df realpath; do
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

# Locks, markers and STOP UNKNOWN handling: see build-lock.sh. Clear markers
# only after supervised build rc=0 or supervised smoke rc=0/2, including the
# final disk probe. Every unconfirmed submitted operation retains both locks.
# shellcheck source=build-lock.sh
. "$HERE/build-lock.sh"
BUILD_LOCK_SSH_HOST=$HOST
BUILD_STOP_UNKNOWN=0
BUILD_STOP_REASON=""
BUILD_SUBMITTED=0
SMOKE_SUBMITTED=0
# shellcheck disable=SC2329  # invoked by the EXIT trap
build_on_exit() {
  trap '' INT TERM
  # Unexpected exit after any daemon submission must never release the locks.
  if [ "$BUILD_SUBMITTED" = 1 ]; then BUILD_STOP_UNKNOWN=1; fi
  if [ "$BUILD_STOP_UNKNOWN" = 1 ]; then
    build_lock_mark_all_unknown "${BUILD_STOP_REASON:-unconfirmed-stop}" \
      || echo "warning: could not update every unknown marker" >&2
    if [ "$SMOKE_SUBMITTED" = 1 ]; then
      echo "STOP UNKNOWN smoke: container $SMOKE_NAME on $HOST; temporary image $TMP_REF; evidence $HOST:$SMOKE_DIR, $HOST:$RECEIPT and local log $LOG; no automatic force-remove; daemon-side stop is not confirmed. The owner must verify shutdown before unlocking." >&2
    fi
    echo "STOP UNKNOWN: locks retained and marked; a human must confirm no build is running, then remove the marker and lock" >&2
    return
  fi
  build_lock_release_all
}
trap 'build_on_exit' EXIT
trap 'if [ "$BUILD_SUBMITTED" != 1 ]; then build_lock_clear_all_unknown 2>/dev/null || true; else BUILD_STOP_UNKNOWN=1; BUILD_STOP_REASON="interrupted after submission"; fi; exit 130' INT TERM
build_lock_acquire "$HERE/.build-lock" "local staging ($HERE)" 0

lockctl plan > "$HERE/.plan.tsv"
guard_paths "$SMOKE_DIR" "$RECEIPT" || die "remote path admission failed"
exclusive_admission || die "exclusive admission failed; nothing was built"
disk_admission || die "startup disk admission failed; nothing was built"
ssh_host "mkdir -p $(q "$CTX") $(q "$CACHE") $(q "$REMOTE_ROOT/smoke") $(q "$REMOTE_ROOT/receipts")"
guard_paths "$SMOKE_DIR" "$RECEIPT" || die "remote path admission failed after mkdir"
build_lock_acquire "$REMOTE_LOCK" "remote context ($HOST:$REMOTE_ROOT)" 1

echo "Syncing the build context to $HOST:$CTX ..."
guard_paths "$SMOKE_DIR" "$RECEIPT" || die "remote path admission failed before rsync"
rsync -a --delete -e "ssh -o BatchMode=yes" "${RSYNC_EXCLUDES[@]}" "$HERE/" "$HOST:$CTX/"

if ssh_host "docker image inspect $(q "$REF")" >/dev/null 2>&1; then
  die "$REF already exists on $HOST; candidate tags are unique, pick a new one"
fi

echo "Staging inputs (cas.sh) ..."
guard_paths "$SMOKE_DIR" "$RECEIPT" || die "remote path admission failed before CAS"
ssh_host "$CAS_CMD" || die "cas.sh could not stage every input (see above; --stage ID=SRC for inputs without a usable source)"

exclusive_admission || die "exclusive admission failed before build"
disk_admission || die "startup disk admission failed; nothing was built"

echo "Building $REF (timeout ${BUILD_TIMEOUT_SECONDS}s) ..."
if ! build_lock_mark_all_unknown "build submitted, completion unconfirmed"; then
  echo "refusing to submit: could not persist the in-flight marker in every held lock" >&2
  build_lock_clear_all_unknown || true
  exit 1
fi
BUILD_SUBMITTED=1
set +e
supervise build "$BUILD_TIMEOUT_SECONDS" "cd $(q "$CTX") && $DOCKER_BUILD"
BUILD_RC=$?
set -e
if [ "$BUILD_RC" != 0 ]; then
  build_lock_classify_stop "$BUILD_RC"
  echo "BUILD FAILED (rc=$BUILD_RC): STOP UNKNOWN - ${BUILD_STOP_REASON}; the daemon-side build may still be running" >&2
  echo "inspect on $HOST: docker ps, docker buildx du, and $LOG before any retry" >&2
  BUILD_STOP_UNKNOWN=1
  exit 1
fi
BUILD_SUBMITTED=0
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

echo "Receipt ..."
guard_paths "$SMOKE_DIR" "$RECEIPT" || die "remote path admission failed before receipt"
exclusive_admission || die "exclusive admission failed before receipt"
if ! build_lock_mark_all_unknown "receipt/smoke submitted, completion unconfirmed"; then
  BUILD_STOP_UNKNOWN=1
  die "refusing to submit receipt: could not persist every marker"
fi
BUILD_SUBMITTED=1
# Receipt also launches a Docker client: supervise it and never overwrite a
# pre-existing receipt (including a hard link). TMP_REF survives every failure.
supervise receipt "$SMOKE_TIMEOUT_SECONDS" "set -C; exec docker run --rm --network none --entrypoint cat $(q "$REF") /opt/universal-export/receipt.json > $(q "$RECEIPT")" \
  || { BUILD_STOP_REASON="receipt completion unconfirmed"; die "receipt failed; STOP UNKNOWN"; }
# Keep the receipt/smoke marker live across their transition. Any interruption
# or failed guard here is conservatively UNKNOWN, not implicit lock release.
ssh_host "cat $(q "$RECEIPT")" > "$HERE/logs/$TAG.receipt.json"

echo "Smoke (network none, timeout ${SMOKE_TIMEOUT_SECONDS}s) ..."
guard_paths "$SMOKE_DIR" "$RECEIPT" || die "remote path admission failed before smoke"
exclusive_admission || die "exclusive admission failed before smoke"
ssh_host "mkdir -p $(q "$SMOKE_DIR")"
guard_paths "$SMOKE_DIR" "$RECEIPT" || die "remote path admission failed after smoke mkdir"
SMOKE_SUBMITTED=1
BUILD_SUBMITTED=1
set +e
supervise smoke "$SMOKE_TIMEOUT_SECONDS" "$SMOKE_RUN"
SMOKE_RC=$?
set -e
if [ "$SMOKE_RC" != 0 ] && [ "$SMOKE_RC" != 2 ]; then
  BUILD_STOP_REASON="smoke completion unconfirmed rc=$SMOKE_RC (including watchdog/probe failure)"
  die "smoke failed; STOP UNKNOWN; temporary tag and evidence retained"
fi
BUILD_SUBMITTED=0
if ! build_lock_clear_all_unknown; then
  BUILD_STOP_UNKNOWN=1
  BUILD_STOP_REASON="marker clear failed after supervised smoke"
  die "$BUILD_STOP_REASON"
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
if [ -z "$VERDICT" ]; then
  ssh_host "docker rmi $(q "$TMP_REF")" >/dev/null || die "could not remove temporary tag after verified smoke"
fi
if [ -z "$VERDICT" ] && [ -n "$APPLE_PACK" ]; then
  echo "RESULT: $REF ($ID) built; smoke PASSED; PROMOTABLE (promotion is a separate approved step)"; exit 0
fi
if [ -z "$VERDICT" ]; then
  echo "RESULT: $REF ($ID) built; smoke passed WITHOUT the Apple link smoke; NOT promotable"; exit 2
fi
echo "RESULT: $REF ($ID) built; smoke FAILED ($VERDICT; apple pack: ${APPLE_PACK:-none}); tag kept as evidence, retry with a new tag" >&2
exit 1
