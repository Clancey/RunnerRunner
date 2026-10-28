#!/usr/bin/env bash
# Regenerate Gemfile.lock for the image (x86_64-linux, Ruby 3.2.3, with
# CHECKSUMS) and prove every locked gem supports that Ruby. Control-host
# tool: needs network and a Bundler matching the lock's BUNDLED WITH.
#
#   images/universal-export/ruby/relock.sh
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOCK="$HERE/../toolchain.lock.json"
RUBY_TARGET="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["ruby"]["ruby_version"])' "$LOCK")"
BUNDLER="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["ruby"]["bundler"]["version"])' "$LOCK")"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cp "$HERE/Gemfile" "$work/Gemfile"
export GEM_HOME="$work/gems" BUNDLE_USER_HOME="$work/bundle" BUNDLE_GEMFILE="$work/Gemfile"
gem install --no-document --install-dir "$GEM_HOME" bundler -v "$BUNDLER" >/dev/null
b() { "$GEM_HOME/bin/bundle" "_${BUNDLER}_" "$@"; }

b lock --add-platform x86_64-linux >/dev/null
for p in $(sed -n '/^PLATFORMS/,/^$/p' "$work/Gemfile.lock" | sed '1d;/^$/d' | awk '{print $1}'); do
  case "$p" in ruby|x86_64-linux) ;; *) b lock --remove-platform "$p" >/dev/null ;; esac
done
b lock --add-checksums >/dev/null

python3 "$HERE/check_ruby_compat.py" "$work/Gemfile.lock" --ruby "$RUBY_TARGET"
cp "$work/Gemfile.lock" "$HERE/Gemfile.lock"
echo "wrote $HERE/Gemfile.lock"
echo "now set ruby.gemfile_lock_sha256 in toolchain.lock.json to: $(python3 -c 'import hashlib,sys; print(hashlib.sha256(open(sys.argv[1],"rb").read()).hexdigest())' "$HERE/Gemfile.lock")"
