#!/usr/bin/env bash
# bin/fastlane must use the image's locked bundle whatever the job's
# environment says. bash 3.2 compatible.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
FL="$HERE/../bin/fastlane"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
cat > "$TMP/bundle" <<'SH'
#!/bin/sh
printf '%s\n' "$BUNDLE_GEMFILE" "$BUNDLE_PATH" "$BUNDLE_DEPLOYMENT" "$BUNDLE_FROZEN" "$*"
SH
chmod +x "$TMP/bundle"
got=$(PATH="$TMP:$PATH" UE_HOME="$TMP/evil" BUNDLE_GEMFILE="$TMP/evil/Gemfile" BUNDLE_PATH="$TMP/evil" \
  BUNDLE_FROZEN=false sh "$FL" supply --track internal)
want="/opt/universal-export/ruby/Gemfile
/opt/universal-export/ruby/vendor
true
true
exec fastlane supply --track internal"
if [ "$got" != "$want" ]; then
  printf 'FAIL: fastlane wrapper honoured the job environment\n--- got:\n%s\n--- want:\n%s\n' "$got" "$want" >&2
  exit 1
fi
echo "ok: fastlane ignores UE_HOME and BUNDLE_* overrides"
