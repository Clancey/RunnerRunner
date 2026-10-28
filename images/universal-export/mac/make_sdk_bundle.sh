#!/usr/bin/env bash
# Pack the Apple SDK subset of an installed Xcode.app as a format-2 pack for
# bin/apple-sdk-admit: the same subset as the 27A9275 format-1 pack, minus every
# prebuilt-modules dir (only Apple's compiler can read them), pre-patched, with
# MANIFEST.sha256 + MANIFEST.tree, as a deterministic tar | zstd -19 -T0.
# Prints the apple_sdk.known_packs stanza on stdout; logs go to stderr.
# Runs on macOS (bash 3.2, python3 >= 3.9, zstd); never uploads anything.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)
helper=$here/sdk_pack.py
xcode=/Applications/Xcode.app
out=$PWD
platforms=(XROS XRSimulator iPhoneOS iPhoneSimulator)
runtimes=(ios iossim xros xrossim)
swift_dirs=(apinotes xros xrsimulator iphoneos iphonesimulator)
lib=Developer/Toolchains/XcodeDefault.xctoolchain/usr/lib

usage() {
  echo "usage: ${0##*/} [--xcode /Applications/Xcode.app] [--out DIR]" >&2
  exit 64
}
log() { echo "make_sdk_bundle: $*" >&2; }
die() {
  log "error: $*"
  exit 1
}

while (($#)); do
  case $1 in
    --xcode | --out)
      (($# >= 2)) || usage
      if [[ $1 == --xcode ]]; then xcode=$2; else out=$2; fi
      shift 2
      ;;
    *) usage ;;
  esac
done

python3 -c 'import sys; sys.exit(sys.version_info < (3, 9))' || die "python3 >= 3.9 required"
command -v zstd >/dev/null || die "zstd not found on PATH (brew install zstd)"
contents=${xcode%/}/Contents
[[ -f $contents/version.plist && ! -L $contents/version.plist ]] ||
  die "$contents/version.plist not found (not an Xcode.app?)"
build=$(python3 "$helper" xcode-build "$contents/version.plist")
mkdir -p "$out"
out=$(cd "$out" && pwd -P)
for f in "$out/apple-sdk-$build.tar.zst" "$out/apple-sdk-$build.tar.zst.sha256"; do
  [[ ! -e $f && ! -L $f ]] || die "$f already exists; refusing to overwrite"
done

stage=$out/.apple-sdk-$build.stage-$$
cleanup() { if [[ -e $stage ]]; then python3 "$helper" rmtree "$stage"; fi; }
trap cleanup EXIT
mkdir -m 700 "$stage"

# APFS clones (copy-on-write, so patching the stage never touches Xcode) where
# cp supports -c; otherwise ordinary copies.
cp_flags=(-R -P -p)
if cp -c -p "$contents/version.plist" "$stage/version.plist" 2>/dev/null; then
  cp_flags=(-c "${cp_flags[@]}")
else
  log "cp -c (APFS clone) unsupported; falling back to cp -p"
  rm -f "$stage/version.plist"
  cp -p "$contents/version.plist" "$stage/version.plist"
fi

take() { # take REL: copy Contents/REL (must exist, must not be a symlink) into the stage
  [[ -e $contents/$1 && ! -L $contents/$1 ]] || die "$contents/$1 is missing or a symlink"
  mkdir -p "$stage/$(dirname "$1")"
  cp "${cp_flags[@]}" "$contents/$1" "$stage/$1"
}

for p in "${platforms[@]}"; do
  pdir=Developer/Platforms/$p.platform
  take "$pdir/Info.plist"
  take "$pdir/version.plist"
  take "$pdir/Developer/SDKs/$p.sdk"
  for link in "$contents/$pdir/Developer/SDKs"/*.sdk; do # versioned aliases, e.g. XROS27.0.sdk -> XROS.sdk
    if [[ -L $link && $(readlink "$link") == "$p.sdk" ]]; then
      ln -s "$p.sdk" "$stage/$pdir/Developer/SDKs/${link##*/}"
    fi
  done
done

clang_ver='' n=0
for d in "$contents/$lib/clang"/*; do
  if [[ -d $d && ! -L $d ]]; then
    clang_ver=${d##*/} n=$((n + 1))
  fi
done
((n == 1)) || die "expected exactly one clang resource dir under $contents/$lib/clang, found $n"
for rt in "${runtimes[@]}"; do
  take "$lib/clang/$clang_ver/lib/darwin/libclang_rt.$rt.a"
done
ln -s "$clang_ver" "$stage/$lib/clang/current"
for d in "${swift_dirs[@]}"; do
  take "$lib/swift/$d"
done

python3 "$helper" finalize --xcode "$xcode" --stage "$stage" --out "$out" --build "$build" \
  --clang-version "$clang_ver"
