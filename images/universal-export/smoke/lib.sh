# shellcheck shell=bash
# Helpers shared by run_smoke.sh and android_prewarm.sh (sourced). Bash 3.2 safe.

# Log lines that mean an export failed even when Godot exits 0.
UE_EXPORT_ERRORS='SCRIPT ERROR|Parse Error|Export failed|Project export for preset .* failed|Cannot export project with preset|Invalid export preset name'

ue_log() { printf '%s: %s\n' "${UE_PROG:-smoke}" "$*" >&2; }

ue_major_minor() { # 4.8.dev -> 4.8
  local maj=${1%%.*} rest=${1#*.}
  printf '%s.%s\n' "$maj" "${rest%%.*}"
}

ue_tres_str() {
  local s=${1//\\/\\\\}
  s=${s//\"/\\\"}
  printf '"%s"' "$s"
}

# Throwaway editor settings in CONFIG_HOME/godot for Godot VERSION (both the
# editor_settings-<maj>.tres and -<maj>.<min>.tres names). Godot fills in the
# remaining defaults when it loads the file.
ue_write_editor_settings() { # CONFIG_HOME VERSION
  local dir="$1/godot" mm f
  mm=$(ue_major_minor "$2")
  mkdir -p "$dir"
  for f in "editor_settings-${mm%%.*}.tres" "editor_settings-$mm.tres"; do
    {
      printf '[gd_resource type="EditorSettings" format=3]\n\n[resource]\n'
      printf 'export/android/android_sdk_path = %s\n' "$(ue_tres_str "${ANDROID_HOME:?}")"
      printf 'export/android/java_sdk_path = %s\n' "$(ue_tres_str "${JAVA_HOME:?}")"
      printf 'export/android/debug_keystore = %s\n' "$(ue_tres_str "${GODOT_ANDROID_KEYSTORE_DEBUG_PATH:-}")"
      printf 'export/android/debug_keystore_user = %s\n' "$(ue_tres_str "${GODOT_ANDROID_KEYSTORE_DEBUG_USER:-androiddebugkey}")"
      printf 'export/android/debug_keystore_pass = %s\n' "$(ue_tres_str "${GODOT_ANDROID_KEYSTORE_DEBUG_PASSWORD:-android}")"
      printf 'network/connection/network_mode = 0\n'
    } >"$dir/$f"
  done
}

# Ephemeral release keystore (PKCS12, 2 days) in KEYDIR; exports Godot's
# GODOT_ANDROID_KEYSTORE_RELEASE_* for the export. PASSWORD comes from the caller.
ue_make_release_keystore() { # KEYDIR PASSWORD
  local ks="$1/release.keystore"
  rm -f "$ks"
  KS_PASS=$2 keytool -genkeypair -keystore "$ks" -storetype PKCS12 -alias uesmoke -keyalg RSA -keysize 2048 \
    -validity 2 -dname "CN=UE Smoke Release,O=Universal Export,C=US" \
    -storepass:env KS_PASS -keypass:env KS_PASS -noprompt >/dev/null 2>&1 || return 1
  export GODOT_ANDROID_KEYSTORE_RELEASE_PATH="$ks" GODOT_ANDROID_KEYSTORE_RELEASE_USER=uesmoke
  export GODOT_ANDROID_KEYSTORE_RELEASE_PASSWORD="$2"
}

# Ephemeral self-signed code-signing identity: KEYDIR/codesign.p12 with its
# password in KEYDIR/codesign.pass. Legacy SHA1/3DES PKCS#12 because rcodesign
# 0.29 cannot read OpenSSL 3's default PBES2/AES export.
ue_make_codesign_p12() { # KEYDIR PASSWORD
  local d=$1
  printf '[req]\ndistinguished_name = dn\nx509_extensions = ext\nprompt = no\n[dn]\nCN = UE Smoke Codesign\n[ext]\nbasicConstraints = critical,CA:false\nkeyUsage = critical,digitalSignature\nextendedKeyUsage = critical,codeSigning\n' >"$d/codesign.cnf"
  (umask 077 && printf '%s' "$2" >"$d/codesign.pass")
  openssl req -x509 -newkey rsa:2048 -nodes -days 2 -config "$d/codesign.cnf" \
    -keyout "$d/codesign.key" -out "$d/codesign.crt" >/dev/null 2>&1 || return 1
  openssl pkcs12 -export -inkey "$d/codesign.key" -in "$d/codesign.crt" -name "UE Smoke Codesign" \
    -certpbe PBE-SHA1-3DES -keypbe PBE-SHA1-3DES -macalg sha1 \
    -passout "file:$d/codesign.pass" -out "$d/codesign.p12" >/dev/null 2>&1 || return 1
  rm -f "$d/codesign.key"
}

# Headless export of PRESET to OUT (absolute), logged to LOG. Extra args (e.g.
# --install-android-build-template) go before the export flag. On failure
# returns 1 with the reason in UE_EXPORT_ERROR. A missing OUT next to other
# output is left to the caller's artifact checks (export layouts vary).
ue_godot_export() { # LOG PROJECT debug|release PRESET OUT [extra...]
  local log=$1 proj=$2 mode=$3 preset=$4 out=$5 rc=0 hit
  shift 5
  UE_EXPORT_ERROR=
  timeout "${SMOKE_EXPORT_TIMEOUT:-1800}" "${GODOT:?}" --headless --path "$proj" "$@" \
    "--export-$mode" "$preset" "$out" </dev/null >"$log" 2>&1 || rc=$?
  hit=$(grep -E -m1 "$UE_EXPORT_ERRORS" "$log" || true)
  if [ "$rc" -eq 124 ]; then
    UE_EXPORT_ERROR="export timed out after ${SMOKE_EXPORT_TIMEOUT:-1800}s"
  elif [ "$rc" -ne 0 ]; then
    UE_EXPORT_ERROR="godot exited $rc${hit:+: $hit}"
  elif [ -n "$hit" ]; then
    UE_EXPORT_ERROR="export log: $hit"
  elif [ ! -e "$out" ] && [ -z "$(ls -A "$(dirname "$out")" 2>/dev/null)" ]; then
    UE_EXPORT_ERROR="godot exited 0 but wrote nothing"
  fi
  [ -z "$UE_EXPORT_ERROR" ]
}

# Last N lines of FILE on stderr, with UE_REDACT (the run's throwaway key
# password) masked.
ue_tail() { # FILE [N]
  local l
  tail -n "${2:-25}" "$1" 2>/dev/null | while IFS= read -r l || [ -n "$l" ]; do
    if [ -n "${UE_REDACT:-}" ]; then l=${l//"$UE_REDACT"/<redacted>}; fi
    printf '  | %s\n' "$l" >&2
  done || true
}
