#!/bin/sh
# shellcheck disable=SC2086,SC2012  # vendored from LogicLand: fixed, space-free image paths; clang dir names are numeric versions
# Build the Darwin resource-dir glue inside the image. Links point into the
# SDK bundle baked at $APPLE_SDK (dangling until it is unpacked) and into the
# swift.org toolchain at $SWIFT_HOME.
set -eu
A=${APPLE_SDK:-/opt/apple-sdk}
SWIFT_HOME=${SWIFT_HOME:-/opt/swift}
TC=$A/Developer/Toolchains/XcodeDefault.xctoolchain/usr/lib
CV=$(ls $SWIFT_HOME/lib/clang | sort -n | tail -1)
CR=/opt/linux-apple/clang-resource
SR=/opt/linux-apple/swift-resource
mkdir -p $CR/lib $SR
# clang: the swift.org toolchain bundles clang and its intrinsic headers;
# Apple's compiler-rt for Darwin comes from the Xcode toolchain copy.
ln -sfn $SWIFT_HOME/lib/clang/$CV/include $CR/include
if [ -d "$SWIFT_HOME/lib/clang/$CV/share" ]; then
	ln -sfn $SWIFT_HOME/lib/clang/$CV/share $CR/share
fi
ln -sfn $TC/clang/current/lib/darwin $CR/lib/darwin
# swift: no Linux module maps (dispatch, os, Block, CoreFoundation) here.
ln -sfn $CR $SR/clang
ln -sfn $SWIFT_HOME/lib/swift/host $SR/host
ln -sfn $SWIFT_HOME/lib/swift/migrator $SR/migrator
ln -sfn $TC/swift/apinotes $SR/apinotes
for p in xros xrsimulator iphoneos iphonesimulator; do ln -sfn $TC/swift/$p $SR/$p; done
