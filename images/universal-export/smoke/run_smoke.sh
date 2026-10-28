#!/usr/bin/env bash
# Offline smoke test of the universal export image: exports a generated tiny
# project with every Godot set's smoke presets and checks each artifact.
# Runs inside the image under `docker run --network none`.
set -euo pipefail
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
UE_PROG=run_smoke
# shellcheck source=SCRIPTDIR/lib.sh
. "$HERE/lib.sh"

usage() {
  cat <<'EOF'
usage: run_smoke.sh --mode offline [--sets all|ID[,ID...]] [--work DIR] [--report FILE]
                    (--apple-pack PATH --apple-build BUILD | --allow-no-apple-smoke)

  --mode offline          the only mode (run under docker --network none)
  --sets                  Godot set ids to test (default: all, from `godot-set list --ids`)
  --work DIR              absent or empty work dir (default: mktemp -d); logs stay there
  --report FILE           JSON report (default: WORK/smoke-report.json); outside WORK, the
                          scrubbed logs are first archived to <FILE minus .json>-logs.tar.gz
                          (a failed archive fails the run)
  --apple-pack PATH       Apple SDK pack to admit (apple-sdk-admit) for the iOS/visionOS link smoke
  --apple-build BUILD     the pack's Xcode build, e.g. 27A9275
  --allow-no-apple-smoke  run without an Apple SDK pack; the result is then never promotable

Last stdout line: "SMOKE RESULT: PASS promotable=true|false" or "SMOKE RESULT: FAIL".
Exit 0 = pass and promotable, 2 = pass but not promotable, 1 = failure, 64 = usage.
Hygiene rows: secret-env, verify-installed, isolation-check (run before and after the
Apple SDK admission; missing or failing either time fails) and ephemeral-keys.
Env: SMOKE_EXPORT_TIMEOUT (seconds per export, default 1800),
     SMOKE_SKIP_ISOLATION_PROBE=1 (tests only; the run is then not promotable).
EOF
}

die_usage() {
  printf 'run_smoke: %s (see --help)\n' "$1" >&2
  exit 64
}

MODE='' SETS=all WORK='' REPORT='' PACK='' BUILD='' ALLOW=0
while [ $# -gt 0 ]; do
  case $1 in
    -h | --help) usage; exit 0 ;;
    --allow-no-apple-smoke) ALLOW=1; shift ;;
    --mode | --sets | --work | --report | --apple-pack | --apple-build)
      [ $# -ge 2 ] || die_usage "$1 needs a value"
      case $2 in --*) die_usage "$1 needs a value" ;; esac
      case $1 in
        --mode) MODE=$2 ;;
        --sets) SETS=$2 ;;
        --work) WORK=$2 ;;
        --report) REPORT=$2 ;;
        --apple-pack) PACK=$2 ;;
        --apple-build) BUILD=$2 ;;
      esac
      shift 2 ;;
    *) die_usage "unknown argument: $1" ;;
  esac
done

ID_RE='^[A-Za-z0-9][A-Za-z0-9._-]*$'
SETS_RE='^[A-Za-z0-9][A-Za-z0-9._-]*(,[A-Za-z0-9][A-Za-z0-9._-]*)*$'
BUILD_RE='^[A-Za-z0-9.]+$'
[ "$MODE" = offline ] || die_usage "--mode offline is required"
[[ $SETS =~ $SETS_RE ]] || die_usage "--sets must be 'all' or comma-separated set ids"
if [ -n "$PACK$BUILD" ]; then
  [ -n "$PACK" ] && [ -n "$BUILD" ] || die_usage "--apple-pack and --apple-build go together"
  [ "$ALLOW" = 0 ] || die_usage "--allow-no-apple-smoke contradicts --apple-pack"
  [ -f "$PACK" ] || die_usage "--apple-pack $PACK is not a file"
  [[ $BUILD =~ $BUILD_RE ]] || die_usage "--apple-build $BUILD is not an Xcode build id"
elif [ "$ALLOW" = 0 ]; then
  die_usage "give --apple-pack/--apple-build, or --allow-no-apple-smoke (not promotable)"
fi
if [ -n "$WORK" ] && [ -e "$WORK" ]; then
  [ -d "$WORK" ] && [ -z "$(ls -A "$WORK")" ] || die_usage "--work $WORK must be absent or an empty directory"
fi
if [ -n "$REPORT" ]; then
  rdir=$(dirname "$REPORT")
  [ -d "$rdir" ] || [ "$rdir" = "${WORK%/}" ] || die_usage "--report directory $rdir does not exist"
fi

# --- side effects from here on ------------------------------------------------
if [ -z "$WORK" ]; then
  WORK=$(mktemp -d)
else
  mkdir -p "$WORK"
fi
WORK=$(cd "$WORK" && pwd)
REPORT=${REPORT:-$WORK/smoke-report.json}
RESULTS=$WORK/results.tsv
KEYS=$WORK/keys
SECRET=
mkdir -p "$WORK/logs" "$WORK/sets"
: >"$RESULTS"
trap 'rm -rf "$KEYS"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

record() { # SET PRESET pass|fail|skip DETAIL [SHA256]
  local d=${4:-}
  d=${d//$'\t'/ }
  d=${d//$'\r'/ }
  d=${d//$'\n'/ }
  if [ -n "$SECRET" ]; then d=${d//"$SECRET"/<redacted>}; fi
  [ ${#d} -le 600 ] || d="${d:0:600}..."
  printf '%s\t%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "$d" "${5:-}" >>"$RESULTS"
  ue_log "$3  $1/$2${d:+: $d}"
}

chk() { python3 "$HERE/checks.py" "$@"; }

first_lines() { # FILE [N]: the first N lines of FILE joined with "; "
  head -n "${2:-3}" "$1" 2>/dev/null | tr '\n' ';' | sed 's/;$//; s/;/; /g'
}

# --- image hygiene (before any key exists) ------------------------------------
secret_names=
shopt -s nocasematch
for n in $(compgen -e); do
  [ "$n" = GODOT_ANDROID_KEYSTORE_DEBUG_PASSWORD ] && continue # public debug password
  if [[ $n =~ PASSWORD|SECRET|TOKEN|KEY_CONTENT|KEYSTORE_BASE64|P12 ]]; then
    secret_names="${secret_names:+$secret_names }$n"
  fi
done
shopt -u nocasematch
if [ -n "$secret_names" ]; then
  record _image secret-env fail "secret-like env names in the image/run: $secret_names"
else
  record _image secret-env pass "no secret-like env names"
fi

if [ -n "${UE_HOME:-}" ] && [ -f "$UE_HOME/lib/lockctl.py" ]; then
  ue_log "lockctl verify-installed ..."
  if python3 "$UE_HOME/lib/lockctl.py" verify-installed >"$WORK/logs/verify-installed.log" 2>&1; then
    record _image verify-installed pass "lockctl verify-installed ok"
  else
    grep '^FAIL' "$WORK/logs/verify-installed.log" >"$WORK/logs/verify-installed.fail" || true
    [ -s "$WORK/logs/verify-installed.fail" ] || tail -n 3 "$WORK/logs/verify-installed.log" >"$WORK/logs/verify-installed.fail"
    record _image verify-installed fail "$(first_lines "$WORK/logs/verify-installed.fail" 5)"
  fi
else
  record _image verify-installed skip "UE_HOME unset (not in the image)"
fi

# --- network isolation ----------------------------------------------------------
ISOLATED=false
if [ "${SMOKE_SKIP_ISOLATION_PROBE:-}" = 1 ]; then
  ue_log "isolation probe skipped (SMOKE_SKIP_ISOLATION_PROBE=1): not promotable"
elif ! command -v timeout >/dev/null; then
  ue_log "no timeout(1): cannot prove isolation"
else
  ISOLATED=true
  for target in 1.1.1.1/443 8.8.8.8/53; do
    if timeout 5 bash -c "exec 3<>/dev/tcp/$target" 2>/dev/null; then
      ISOLATED=false
      ue_log "network reachable: tcp $target (run under docker --network none)"
    fi
  done
  ue_log "network isolated: $ISOLATED"
fi
# isolation-check is a mandatory hygiene row: it runs before and again after the
# Apple SDK admission, and either run failing (or the tool missing) fails it.
ISO_FAIL=
isolation_check() { # PHASE
  if ! command -v isolation-check >/dev/null; then
    ISO_FAIL="${ISO_FAIL:+$ISO_FAIL; }$1: isolation-check not on PATH"
  elif ! isolation-check >"$WORK/logs/isolation-check-$1.log" 2>&1; then
    ISO_FAIL="${ISO_FAIL:+$ISO_FAIL; }$1: $(grep -iE 'fail|reachable|forbidden' "$WORK/logs/isolation-check-$1.log" | head -n 3 | tr '\n' ';' | sed 's/;$//' || true)"
    ue_log "isolation-check ($1) reported problems (logs/isolation-check-$1.log)"
  fi
}
isolation_check before-admission

# --- Apple SDK admission ----------------------------------------------------------
ADMITTED=0
if [ -n "$PACK" ]; then
  ue_log "admitting Apple SDK build $BUILD ..."
  if apple-sdk-admit admit --build "$BUILD" --pack "$PACK" >"$WORK/logs/apple-sdk-admit.log" 2>&1; then
    ADMITTED=1
  else
    ue_log "apple-sdk-admit failed:"
    ue_tail "$WORK/logs/apple-sdk-admit.log"
  fi
fi
isolation_check after-admission
if [ -n "$ISO_FAIL" ]; then
  record _image isolation-check fail "$ISO_FAIL"
else
  record _image isolation-check pass "isolation-check ok before and after the Apple SDK admission"
fi

# --- ephemeral signing keys (deleted on exit, scrubbed from every file) -------------
(umask 077 && mkdir "$KEYS")
KEYS_OK=0
if SECRET=$(openssl rand -hex 16 2>/dev/null) && [ ${#SECRET} -ge 16 ]; then
  UE_REDACT=$SECRET
  if ! ue_make_release_keystore "$KEYS" "$SECRET"; then
    record _image ephemeral-keys fail "keytool could not create the release keystore"
  elif ! ue_make_codesign_p12 "$KEYS" "$SECRET"; then
    record _image ephemeral-keys fail "openssl could not create the code-signing p12"
  else
    KEYS_OK=1
  fi
else
  SECRET=
  record _image ephemeral-keys fail "openssl rand failed"
fi
[ "$KEYS_OK" = 0 ] || record _image ephemeral-keys pass "release keystore and code-signing p12 generated"
export GRADLE_OPTS="${GRADLE_OPTS:+$GRADLE_OPTS }-Dorg.gradle.daemon=false"

ENTITLEMENTS='<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>com.apple.security.cs.allow-jit</key><true/></dict></plist>'

# --- per preset -------------------------------------------------------------------
# Sets DETAIL and SHA; returns 1 on a failed check.
check_artifact() { # SDIR ID PLATFORM MODE OUT PRODUCT
  local sdir=$1 id=$2 plat=$3 mode=$4 out=$5 product=$6 odir msg arch exe app cn certs name json st
  odir=$(dirname "$out")
  DETAIL='' SHA=''
  case $plat in
    Linux | "Windows Desktop")
      if [ "$plat" = Linux ]; then
        arch=x86_64
        case $id in *arm64) arch=arm64 ;; esac
        msg=$(chk elf "$out" --machine "$arch" 2>&1) || { DETAIL=$msg; return 1; }
        DETAIL="ELF $arch"
      else
        msg=$(chk pe "$out" --product-name "$product" 2>&1) || { DETAIL=$msg; return 1; }
        DETAIL="PE32+ x86_64, .rsrc has the UTF-16LE product name"
      fi
      [ -f "${out%.*}.pck" ] || { DETAIL="no $(basename "${out%.*}").pck next to the binary"; return 1; }
      DETAIL="$DETAIL + pck"
      ;;
    macOS)
      exe=$(chk macos-zip "$out" 2>&1) || { DETAIL=$exe; return 1; }
      unzip -q "$out" -d "$odir/unzipped" >"$sdir/logs/$id-sign.log" 2>&1 || { DETAIL="unzip failed"; return 1; }
      app=$odir/unzipped/${exe%%/*}
      exe=$odir/unzipped/$exe
      printf '%s\n' "$ENTITLEMENTS" >"$odir/entitlements.plist"
      [ "$KEYS_OK" = 1 ] || { DETAIL="no ephemeral code-signing identity"; return 1; }
      rcodesign sign --timestamp-url none --p12-file "$KEYS/codesign.p12" --p12-password-file "$KEYS/codesign.pass" \
        --entitlements-xml-file "$odir/entitlements.plist" "$app" >>"$sdir/logs/$id-sign.log" 2>&1 ||
        { DETAIL="rcodesign sign failed (logs/$id-sign.log)"; return 1; }
      rcodesign verify "$exe" >>"$sdir/logs/$id-sign.log" 2>&1 ||
        { DETAIL="rcodesign verify failed (logs/$id-sign.log)"; return 1; }
      msg=$(chk macho "$exe" --platform macos 2>&1) || { DETAIL=$msg; return 1; }
      DETAIL="signed (self-signed p12) and verified; $(printf '%s' "$msg" | tr '\n' ' ')"
      ;;
    Web)
      (cd "$odir" && zip -qrX "../$id.zip" .) || { DETAIL="zip failed"; return 1; }
      out=$odir/../$id.zip
      msg=$(chk web-zip "$out" 2>&1) || { DETAIL=$msg; return 1; }
      DETAIL="index.html/.js/.wasm/.pck"
      ;;
    Android)
      cn="CN=UE Smoke Release"
      [ "$mode" = release ] || cn="CN=Android Debug"
      case $out in
        *.aab)
          certs=$(jarsigner -verify -verbose -certs "$out" 2>&1) || { DETAIL="jarsigner -verify failed"; return 1; }
          printf '%s\n' "$certs" | grep -q 'jar verified\.' || { DETAIL="jarsigner: jar not verified"; return 1; }
          printf '%s\n' "$certs" | grep -q "$cn" || { DETAIL="AAB is not signed by $cn"; return 1; }
          msg=$(chk aab-zip "$out" 2>&1) || { DETAIL=$msg; return 1; }
          DETAIL="jarsigner verified, $cn, BundleConfig.pb + manifest + classes.dex + arm64-v8a godot .so"
          ;;
        *)
          certs=$("$ANDROID_HOME/build-tools/${ANDROID_BUILD_TOOLS_VERSION:?}/apksigner" verify --print-certs "$out" 2>&1) ||
            { DETAIL="apksigner verify failed: $(printf '%s' "$certs" | tail -n 2 | tr '\n' ' ')"; return 1; }
          printf '%s\n' "$certs" | grep -q "DN: $cn" ||
            { DETAIL="APK signer is not $cn: $(printf '%s\n' "$certs" | grep -m1 'DN:' || true)"; return 1; }
          msg=$(chk zip-has "$out" AndroidManifest.xml classes.dex lib/arm64-v8a/ 2>&1) || { DETAIL=$msg; return 1; }
          DETAIL="apksigner verified, $cn, arm64-v8a"
          ;;
      esac
      ;;
    iOS | visionOS)
      name=$(chk xcode-export "$odir" --platform "$id" 2>&1) || { DETAIL=$name; return 1; }
      SHA=$(chk sha256 "$odir/$name.xcodeproj/project.pbxproj")
      if [ -z "$PACK" ]; then
        DETAIL="Xcode project $name.xcodeproj + libgodot device slice; link smoke skipped (no Apple SDK pack)"
        return 0
      fi
      [ "$ADMITTED" = 1 ] || { DETAIL="Apple SDK not admitted; link smoke not run"; return 1; }
      json=$sdir/apple-$id.json
      python3 "$HERE/apple_link_smoke.py" --platform "$id" --export "$odir" --work "$sdir/apple-$id" \
        --json "$json" >"$sdir/logs/$id-link.log" 2>&1 || true
      [ -f "$json" ] || { DETAIL="apple_link_smoke crashed (logs/$id-link.log)"; return 1; }
      { IFS= read -r st && IFS= read -r SHA && IFS= read -r DETAIL; } < <(python3 -c '
import json, sys
r = json.load(open(sys.argv[1]))
print(r.get("status", "fail")); print(r.get("exe_sha256") or ""); print(" ".join(str(r.get("detail", "")).split()))
' "$json")
      [ "$st" = pass ] || { DETAIL="${DETAIL:-apple_link_smoke failed} (logs/$id-link.log)"; return 1; }
      return 0
      ;;
    *) DETAIL="no check for platform $plat"; return 1 ;;
  esac
  SHA=$(chk sha256 "$out")
}

smoke_preset() { # SID SDIR PROJECT ID NAME PLATFORM MODE FILE PRODUCT
  local sid=$1 sdir=$2 proj=$3 id=$4 name=$5 plat=$6 mode=$7 file=$8 product=$9 out
  local -a extra=()
  mkdir -p "$sdir/out/$id"
  out=$sdir/out/$id/$file
  if [ "$plat" = Android ] && [ ! -d "$proj/android/build" ]; then
    extra=(--install-android-build-template)
  fi
  ue_log "$sid: export $id ($plat, $mode)"
  if ! ue_godot_export "$sdir/logs/$id.log" "$proj" "$mode" "$name" "$out" ${extra[@]+"${extra[@]}"}; then
    record "$sid" "$id" fail "$UE_EXPORT_ERROR (sets/$sid/logs/$id.log)"
    return 0
  fi
  if check_artifact "$sdir" "$id" "$plat" "$mode" "$out" "$product"; then
    record "$sid" "$id" pass "$DETAIL" "$SHA"
  else
    record "$sid" "$id" fail "$DETAIL"
  fi
}

smoke_set() { # SID
  local sid=$1 sdir=$WORK/sets/$1 envout proj version product id name plat mode file
  local -a gen
  mkdir -p "$sdir/logs" "$sdir/out"
  if ! envout=$(godot-set env "$sid" 2>"$sdir/logs/godot-set.log"); then
    record "$sid" setup fail "godot-set env $sid failed: $(first_lines "$sdir/logs/godot-set.log")"
    return 0
  fi
  eval "$envout"
  export XDG_CONFIG_HOME=$sdir/config XDG_CACHE_HOME=$sdir/cache
  proj=$sdir/project
  gen=(python3 "$HERE/gen_project.py" --set "$sid" --out "$proj")
  if [ -n "${VISIONOS_TEMPLATE:-}" ]; then gen+=(--visionos-template "$VISIONOS_TEMPLATE"); fi
  if ! "${gen[@]}" >"$sdir/logs/gen_project.log" 2>&1; then
    record "$sid" setup fail "gen_project failed: $(first_lines "$sdir/logs/gen_project.log")"
    return 0
  fi
  python3 -c '
import json, sys
m = json.load(open(sys.argv[1], encoding="utf-8"))
print(m["version"]); print(m["product_name"])
for p in m["presets"]:
    print("\t".join((p["id"], p["name"], p["platform"], p["mode"], p["file"])))
' "$proj/smoke.json" >"$sdir/presets.tsv"
  { IFS= read -r version && IFS= read -r product; } <"$sdir/presets.tsv"
  ue_write_editor_settings "$XDG_CONFIG_HOME" "$version"
  ue_log "$sid: import ($version)"
  timeout "${SMOKE_EXPORT_TIMEOUT:-1800}" "${GODOT:?}" --headless --path "$proj" --import </dev/null \
    >"$sdir/logs/import.log" 2>&1 || ue_log "$sid: --import exited $? (exports import again)"
  while IFS=$'\t' read -r id name plat mode file <&3; do
    [ -n "$file" ] || continue
    smoke_preset "$sid" "$sdir" "$proj" "$id" "$name" "$plat" "$mode" "$file" "$product"
  done 3< <(tail -n +3 "$sdir/presets.tsv")
}

if [ "$SETS" = all ]; then
  if ! set_list=$(godot-set list --ids 2>"$WORK/logs/godot-set-list.log"); then
    record all setup fail "godot-set list --ids failed: $(first_lines "$WORK/logs/godot-set-list.log")"
    set_list=
  fi
else
  set_list=${SETS//,/ }
fi
seen=" "
for sid in $set_list; do
  case $seen in *" $sid "*) continue ;; esac
  seen="$seen$sid "
  if ! [[ $sid =~ $ID_RE ]]; then
    record "$sid" setup fail "invalid set id"
    continue
  fi
  set +e
  (
    set -e
    smoke_set "$sid"
  ) </dev/null
  rc=$?
  set -e
  [ "$rc" -eq 0 ] || record "$sid" harness fail "smoke harness error (exit $rc); see sets/$sid/logs"
done

# --- report -----------------------------------------------------------------------------
rm -rf "$KEYS"
if [ -n "$SECRET" ]; then
  # Godot/Gradle may echo the release keystore password; redact it from every file.
  printf '%s' "$SECRET" | python3 -c '
import sys
from pathlib import Path
s = sys.stdin.read().encode()
for p in Path(sys.argv[1]).rglob("*"):
    if p.is_file() and not p.is_symlink():
        try:
            b = p.read_bytes()
        except OSError:
            continue
        if s in b:
            p.write_bytes(b.replace(s, b"<redacted>"))
' "$WORK"
fi
# WORK usually dies with the container; keep the (scrubbed) logs next to an outside
# report. The archive is written before the report, and failing to write it fails the run.
ARCHIVE_OK=1
if [ "$(cd "$(dirname "$REPORT")" && pwd)" != "$WORK" ]; then
  keep=(results.tsv logs)
  for f in "$WORK"/sets/*/logs "$WORK"/sets/*/apple-*.json "$WORK"/sets/*/presets.tsv; do
    [ -e "$f" ] && keep+=("${f#"$WORK"/}")
  done
  if ! tar -czf "${REPORT%.json}-logs.tar.gz" -C "$WORK" "${keep[@]}"; then
    ARCHIVE_OK=0
    rm -f "${REPORT%.json}-logs.tar.gz"
    record _image log-archive fail "could not archive the logs next to $REPORT"
  fi
fi
set +e
python3 "$HERE/report.py" --results "$RESULTS" --out "$REPORT" --isolated "$ISOLATED" \
  --apple-build "$BUILD" --apple-admitted "$ADMITTED"
rc=$?
set -e
[ "$ARCHIVE_OK" = 1 ] || rc=1
case $rc in
  0 | 1 | 2) exit "$rc" ;;
  *) echo "SMOKE RESULT: FAIL"; exit 1 ;;
esac
