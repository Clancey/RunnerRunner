#!/usr/bin/env bash
# Build-time Gradle/Maven pre-warm (runs during `docker build`, network on):
# for every Godot set, real Android debug APK / release APK / release AAB
# exports of a generated project, then a standalone Gradle project resolving
# the lock's android.maven_prewarm coordinates with plugin_prewarm's AGP and
# Kotlin, so GRADLE_USER_HOME holds everything offline jobs need. Set/Godot
# files and the Android SDK package set must stay untouched (checked; any
# change fails the build).
set -euo pipefail
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
UE_PROG=android_prewarm
# shellcheck source=SCRIPTDIR/lib.sh
. "$HERE/lib.sh"

if [ $# -gt 0 ]; then
  case $1 in
    -h | --help)
      echo "usage: android_prewarm.sh   (no arguments; run in the image build with network)"
      exit 0 ;;
    *) echo "usage: android_prewarm.sh   (no arguments)" >&2; exit 64 ;;
  esac
fi

UE=${UE_HOME:-$(cd "$HERE/.." && pwd)}
lockget() { python3 "$UE/lib/lockctl.py" get --raw "$1"; }
GRADLE_DIST=$(python3 "$UE/lib/lockctl.py" gradle-dist-dir)
GRADLE=$GRADLE_DIST/gradle-$(lockget inputs.gradle.version)/bin/gradle
[ -x "$GRADLE" ] || { ue_log "no Gradle at $GRADLE"; exit 1; }
: "${ANDROID_HOME:?}" "${JAVA_HOME:?}" "${GRADLE_USER_HOME:?}"
export GRADLE_OPTS="${GRADLE_OPTS:+$GRADLE_OPTS }-Dorg.gradle.daemon=false"

WORK=$(mktemp -d "${TMPDIR:-/tmp}/ue-prewarm.XXXXXX")
cleanup() {
  "$GRADLE" --stop >/dev/null 2>&1 || true
  rm -rf "$GRADLE_USER_HOME/daemon" "$WORK"
}
trap cleanup EXIT

SETS=$(godot-set list --ids)
[ -n "$SETS" ] || { ue_log "no Godot sets"; exit 1; }

# Everything a set owns (editor dirs, template data, set.json root) is
# fingerprinted before and after.
WATCH=${GODOT_SETS_ROOT:-/opt/godot/sets}
for sid in $SETS; do
  WATCH="$WATCH
$(eval "$(godot-set env "$sid")" && dirname "$GODOT" && printf '%s\n' "$XDG_DATA_HOME")"
done
fingerprint() {
  printf '%s\n' "$WATCH" | LC_ALL=C sort -u | while IFS= read -r d; do
    [ -e "$d" ] && find "$d" -printf '%p %y %s %T@ %m %l\n'
  done | LC_ALL=C sort
}
sdk_packages() { find "$ANDROID_HOME" -mindepth 1 -maxdepth 2 -type d | LC_ALL=C sort; }
fingerprint >"$WORK/godot-before.txt"
sdk_packages >"$WORK/sdk-before.txt"

(umask 077 && mkdir "$WORK/keys")
UE_REDACT=$(openssl rand -hex 16)
ue_make_release_keystore "$WORK/keys" "$UE_REDACT" || { ue_log "keytool failed"; exit 1; }

prewarm_set() { # SID
  local sid=$1 sdir=$WORK/$1 e proj version id name plat mode file
  local -a extra
  eval "$(godot-set env "$sid")"
  mkdir -p "$sdir/data/godot" "$sdir/logs"
  # Private data dir: Godot may write editor data there, never into the set.
  for e in "$XDG_DATA_HOME"/godot/*; do
    [ -e "$e" ] && ln -s "$e" "$sdir/data/godot/"
  done
  export XDG_DATA_HOME=$sdir/data XDG_CONFIG_HOME=$sdir/config XDG_CACHE_HOME=$sdir/cache
  proj=$sdir/project
  python3 "$HERE/gen_project.py" --set "$sid" --out "$proj" >"$sdir/logs/gen_project.log" 2>&1 ||
    { ue_tail "$sdir/logs/gen_project.log"; return 1; }
  version=$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1]))["version"])' "$proj/smoke.json")
  ue_write_editor_settings "$XDG_CONFIG_HOME" "$version"
  timeout 1800 "${GODOT:?}" --headless --path "$proj" --import </dev/null >"$sdir/logs/import.log" 2>&1 || true
  python3 -c '
import json, sys
for p in json.load(open(sys.argv[1], encoding="utf-8"))["presets"]:
    if p["platform"] == "Android":
        print("\t".join((p["id"], p["name"], p["platform"], p["mode"], p["file"])))
' "$proj/smoke.json" >"$sdir/android.tsv"
  [ -s "$sdir/android.tsv" ] || { ue_log "$sid: no Android smoke presets"; return 0; }
  while IFS=$'\t' read -r id name plat mode file <&3; do
    extra=()
    [ -d "$proj/android/build" ] || extra=(--install-android-build-template)
    ue_log "$sid: $plat $id ($mode)"
    mkdir -p "$sdir/out/$id"
    if ! ue_godot_export "$sdir/logs/$id.log" "$proj" "$mode" "$name" "$sdir/out/$id/$file" \
      ${extra[@]+"${extra[@]}"}; then
      ue_log "$sid/$id: $UE_EXPORT_ERROR"
      ue_tail "$sdir/logs/$id.log" 60
      return 1
    fi
  done 3<"$sdir/android.tsv"
}

for sid in $SETS; do
  set +e
  (
    set -e
    prewarm_set "$sid"
  ) </dev/null
  rc=$?
  set -e
  [ "$rc" -eq 0 ] || { ue_log "pre-warm exports failed for $sid"; exit 1; }
done

# Standalone project with plugin_prewarm's AGP, Kotlin and SDK levels: a
# library that compiles against every maven_prewarm coordinate (as native
# Android plugins do) and an app that also packages them (runtime graph).
P=$WORK/gradle-prewarm
compile_sdk=$(lockget android.plugin_prewarm.compile_sdk)
min_sdk=$(lockget android.plugin_prewarm.min_sdk)
build_tools=${ANDROID_BUILD_TOOLS_VERSION:-$(lockget android.build_tools)}
lib_deps='' app_deps=''
while IFS= read -r c; do
  [ -n "$c" ] || continue
  lib_deps="$lib_deps    compileOnly \"$c\"
"
  case $c in
    org.godotengine:*) app_deps="$app_deps    compileOnly \"$c\"
" ;;
    *) app_deps="$app_deps    implementation \"$c\"
" ;;
  esac
done < <(lockget android.maven_prewarm)

module() { # NAME PLUGIN DEFAULT_CONFIG EXTRA DEPS
  local src=$P/$1/src/main
  mkdir -p "$src/kotlin/org/universalexport/prewarm"
  printf '<manifest xmlns:android="http://schemas.android.com/apk/res/android" />\n' >"$src/AndroidManifest.xml"
  printf 'package org.universalexport.prewarm\n\nobject Prewarm {\n    const val NAME = "%s"\n}\n' "$1" \
    >"$src/kotlin/org/universalexport/prewarm/Prewarm.kt"
  cat >"$P/$1/build.gradle" <<EOF
plugins {
    id "$2"
    id "org.jetbrains.kotlin.android"
}
android {
    namespace "org.universalexport.prewarm.$1"
    compileSdk $compile_sdk
    buildToolsVersion "$build_tools"
    defaultConfig { $3 }
    compileOptions {
        sourceCompatibility JavaVersion.VERSION_17
        targetCompatibility JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
        freeCompilerArgs += ["-Xskip-metadata-version-check"]
    }
$4}
dependencies {
$5}
EOF
}
mkdir -p "$P"
cat >"$P/settings.gradle" <<EOF
pluginManagement {
    repositories { google(); mavenCentral(); gradlePluginPortal() }
}
dependencyResolutionManagement {
    repositories { google(); mavenCentral(); gradlePluginPortal() }
}
rootProject.name = "ue-prewarm"
include ":lib", ":app"
EOF
cat >"$P/build.gradle" <<EOF
plugins {
    id "com.android.library" version "$(lockget android.plugin_prewarm.agp)" apply false
    id "com.android.application" version "$(lockget android.plugin_prewarm.agp)" apply false
    id "org.jetbrains.kotlin.android" version "$(lockget android.plugin_prewarm.kotlin)" apply false
}
EOF
module lib com.android.library "minSdk $min_sdk" "" "$lib_deps"
module app com.android.application \
  "applicationId \"org.universalexport.prewarm\"; minSdk $min_sdk; targetSdk $compile_sdk" \
  "    lint { checkReleaseBuilds = false; abortOnError = false }
" "$app_deps"
printf '%s\n' android.useAndroidX=true "android.suppressUnsupportedCompileSdk=$compile_sdk" \
  kotlin.compiler.execution.strategy=in-process org.gradle.jvmargs=-Xmx2g >"$P/gradle.properties"
printf 'sdk.dir=%s\n' "$ANDROID_HOME" >"$P/local.properties"
ue_log "standalone Gradle project: assembleDebug assembleRelease"
if ! "$GRADLE" --no-daemon --console=plain -p "$P" assembleDebug assembleRelease </dev/null >"$WORK/gradle.log" 2>&1; then
  ue_tail "$WORK/gradle.log" 80
  ue_log "standalone Gradle pre-warm failed"
  exit 1
fi

fingerprint >"$WORK/godot-after.txt"
if ! cmp -s "$WORK/godot-before.txt" "$WORK/godot-after.txt"; then
  ue_log "pre-warm changed Godot set files:"
  diff "$WORK/godot-before.txt" "$WORK/godot-after.txt" | head -n 40 >&2 || true
  exit 1
fi
sdk_packages >"$WORK/sdk-after.txt"
if ! cmp -s "$WORK/sdk-before.txt" "$WORK/sdk-after.txt"; then
  ue_log "Gradle/AGP changed the Android SDK package set (pin the packages in the lock):"
  diff "$WORK/sdk-before.txt" "$WORK/sdk-after.txt" | grep '^[<>]' >&2 || true
  exit 1
fi
ue_log "done: GRADLE_USER_HOME=$GRADLE_USER_HOME"
