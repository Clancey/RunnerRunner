# rr-universal-export: the one Linux export image for every Godot game and
# platform (Android/Quest/Android XR, iOS, visionOS, Linux/Windows/macOS
# desktop, web), plus store tooling (fastlane, ovr-platform-util, steamcmd).
#
# Rules (enforced by `lockctl.py check-dockerfile`, run by build.sh):
# - Every pinned payload comes from toolchain.lock.json through build args and
#   the content-addressed context dir inputs/<sha256> (filled by cas.sh). It
#   is bind-mounted read-only, re-verified with sha256sum and never COPYed, so
#   no digest or download URL lives in this file.
# - Each ARG is declared right before the layer that uses it, so changing one
#   pin rebuilds only from that layer down.
# - The Apple SDK is NOT in the image. /opt/apple-sdk is created at job time
#   by `apple-sdk-admit` from a host-bound pack (see README.md).
# - No secrets: signing material is injected per job by the runner.
# - No `# syntax=` line: the built-in frontend of Docker Engine >= 23 handles
#   RUN --mount, and a syntax line would pull a mutable frontend image.
#
# Build with build.sh (never `docker build` by hand): it validates the lock,
# stages inputs, passes `lockctl.py build-args`, and runs the smoke matrix.

ARG BASE_IMAGE
FROM ${BASE_IMAGE}

# hadolint 2.15 forgets SHELL after the next ARG and lints RUN as POSIX sh;
# every RUN here is bash, so the two POSIX-only warnings are suppressed.
# hadolint global ignore=SC3054,SC3015
SHELL ["/bin/bash", "-o", "pipefail", "-c"]

# Ubuntu noble packages (names from the lock; versions recorded in the
# receipt). The lib32 runtime is for steamcmd.
ARG APT_PACKAGES
ARG APT_I386_PACKAGES
# hadolint ignore=DL3008
RUN read -r -a pkgs <<< "${APT_PACKAGES:?} ${APT_I386_PACKAGES:?}" \
 && apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends "${pkgs[@]}" \
 && rm -rf /var/lib/apt/lists/* \
 && ln -s "/usr/lib/jvm/java-17-openjdk-$(dpkg --print-architecture)" /opt/jdk \
 && git config --system --add safe.directory '*'

# Fixed layout. lockctl's receipt check (image-env) fails the build if any of
# these disagree with the lock.
ENV APPLE_SDK=/opt/apple-sdk \
    SWIFT_HOME=/opt/swift \
    JAVA_HOME=/opt/jdk \
    ANDROID_HOME=/opt/android-sdk \
    ANDROID_SDK_ROOT=/opt/android-sdk \
    GRADLE_USER_HOME=/opt/gradle-home \
    UE_HOME=/opt/universal-export

# Swift (swift.org toolchain: clang, lld and the llvm tools live under
# /opt/swift).
ARG SWIFT_SHA256
RUN --mount=type=bind,source=inputs,target=/inputs \
    echo "${SWIFT_SHA256:?}  /inputs/${SWIFT_SHA256:?}" | sha256sum -c --quiet - \
 && mkdir -p /opt/swift \
 && tar -xzf "/inputs/${SWIFT_SHA256:?}" -C /opt/swift --strip-components=2 \
 && /opt/swift/bin/swiftc --version

# darwin-tools-linux-llvm (ld64.lld, libtool, dsymutil).
ARG DARWIN_TOOLS_SHA256
RUN --mount=type=bind,source=inputs,target=/inputs \
    echo "${DARWIN_TOOLS_SHA256:?}  /inputs/${DARWIN_TOOLS_SHA256:?}" | sha256sum -c --quiet - \
 && mkdir -p /opt/darwin-tools \
 && tar -xzf "/inputs/${DARWIN_TOOLS_SHA256:?}" -C /opt/darwin-tools \
 && test -x /opt/darwin-tools/bin/ld64.lld

# apple-codesign (rcodesign), static musl build.
ARG RCODESIGN_SHA256
RUN --mount=type=bind,source=inputs,target=/inputs \
    echo "${RCODESIGN_SHA256:?}  /inputs/${RCODESIGN_SHA256:?}" | sha256sum -c --quiet - \
 && w=$(mktemp -d) \
 && tar -xzf "/inputs/${RCODESIGN_SHA256:?}" -C "$w" \
 && install -m 0755 "$w"/apple-codesign-*/rcodesign /usr/local/bin/rcodesign \
 && rm -rf "$w"

# Android SDK: cmdline-tools from the lock, then the lock's package list.
# platform-tools is the one unversioned package (sdkmanager has no pin for it).
ARG ANDROID_CMDLINE_TOOLS_SHA256
ARG ANDROID_SDK_PACKAGES
ARG ANDROID_BUILD_TOOLS_VERSION
RUN --mount=type=bind,source=inputs,target=/inputs \
    echo "${ANDROID_CMDLINE_TOOLS_SHA256:?}  /inputs/${ANDROID_CMDLINE_TOOLS_SHA256:?}" | sha256sum -c --quiet - \
 && w=$(mktemp -d) \
 && unzip -q "/inputs/${ANDROID_CMDLINE_TOOLS_SHA256:?}" -d "$w" \
 && mkdir -p "$ANDROID_HOME/cmdline-tools" \
 && mv "$w/cmdline-tools" "$ANDROID_HOME/cmdline-tools/latest" \
 && rm -rf "$w" \
 && read -r -a pkgs <<< "${ANDROID_SDK_PACKAGES:?}" \
 && (yes || true) | "$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager" --licenses > /dev/null \
 && "$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager" "${pkgs[@]}" > /dev/null \
 && test -x "$ANDROID_HOME/build-tools/${ANDROID_BUILD_TOOLS_VERSION:?}/apksigner"
ENV ANDROID_BUILD_TOOLS_VERSION=${ANDROID_BUILD_TOOLS_VERSION}

# Gradle, unpacked where Godot's gradlew looks for it (GRADLE_DIST_DIR is
# lockctl's <GRADLE_USER_HOME>/wrapper/dists/<dist>/<base36 md5 of the URL>),
# with the wrapper's .ok marker so no job ever downloads it.
ARG GRADLE_SHA256
ARG GRADLE_DIST_DIR
RUN --mount=type=bind,source=inputs,target=/inputs \
    echo "${GRADLE_SHA256:?}  /inputs/${GRADLE_SHA256:?}" | sha256sum -c --quiet - \
 && mkdir -p "${GRADLE_DIST_DIR:?}" \
 && unzip -q "/inputs/${GRADLE_SHA256:?}" -d "${GRADLE_DIST_DIR:?}" \
 && touch "${GRADLE_DIST_DIR:?}/$(basename "$(dirname "${GRADLE_DIST_DIR:?}")").zip.ok"

ARG NODE_SHA256
RUN --mount=type=bind,source=inputs,target=/inputs \
    echo "${NODE_SHA256:?}  /inputs/${NODE_SHA256:?}" | sha256sum -c --quiet - \
 && mkdir -p /opt/node \
 && tar -xJf "/inputs/${NODE_SHA256:?}" -C /opt/node --strip-components=1

ARG APKTOOL_SHA256
RUN --mount=type=bind,source=inputs,target=/inputs \
    echo "${APKTOOL_SHA256:?}  /inputs/${APKTOOL_SHA256:?}" | sha256sum -c --quiet - \
 && install -m 0644 "/inputs/${APKTOOL_SHA256:?}" /opt/apktool.jar

# Ruby: the pinned bundler, then fastlane and its gems from the locked
# Gemfile (Gemfile.lock CHECKSUMS are verified by bundler). The bin/fastlane
# binstub pins BUNDLE_GEMFILE, so a game checkout cannot swap the bundle.
ARG BUNDLER_SHA256
COPY ruby/Gemfile ruby/Gemfile.lock /opt/universal-export/ruby/
# hadolint ignore=DL3028
RUN --mount=type=bind,source=inputs,target=/inputs \
    echo "${BUNDLER_SHA256:?}  /inputs/${BUNDLER_SHA256:?}" | sha256sum -c --quiet - \
 && cp "/inputs/${BUNDLER_SHA256:?}" /tmp/bundler.gem \
 && gem install --local --no-document /tmp/bundler.gem \
 && rm -f /tmp/bundler.gem \
 && BUNDLE_GEMFILE=/opt/universal-export/ruby/Gemfile \
    BUNDLE_PATH=/opt/universal-export/ruby/vendor \
    BUNDLE_DEPLOYMENT=true BUNDLE_FROZEN=true \
    bundle install --jobs 4 --retry 3 \
 && rm -rf /root/.bundle/cache /root/.local/share/gem

# Godot sets. Each set is self-contained under /opt/godot/sets/<id>; the
# legacy paths older workflows use are hardlinks made in the SAME layer, so
# they cost no extra bytes. The templates directory name comes from the
# archive's version.txt (lockctl set-json later checks it against the lock).
ARG GODOT_OFFICIAL_EDITOR_SHA256
ARG GODOT_OFFICIAL_TEMPLATES_SHA256
RUN --mount=type=bind,source=inputs,target=/inputs \
    echo "${GODOT_OFFICIAL_EDITOR_SHA256:?}  /inputs/${GODOT_OFFICIAL_EDITOR_SHA256:?}" | sha256sum -c --quiet - \
 && echo "${GODOT_OFFICIAL_TEMPLATES_SHA256:?}  /inputs/${GODOT_OFFICIAL_TEMPLATES_SHA256:?}" | sha256sum -c --quiet - \
 && s=/opt/godot/sets/official-4.7.2 && w=$(mktemp -d) \
 && unzip -q "/inputs/${GODOT_OFFICIAL_EDITOR_SHA256:?}" -d "$w/editor" \
 && mapfile -t members < <(find "$w/editor" -mindepth 1) \
 && [ "${#members[@]}" -eq 1 ] && [ -f "${members[0]}" ] \
 && install -D -m 0755 "${members[0]}" "$s/godot" \
 && unzip -q "/inputs/${GODOT_OFFICIAL_TEMPLATES_SHA256:?}" 'templates/*' -d "$w/tpz" \
 && v=$(tr -d '[:space:]' < "$w/tpz/templates/version.txt") \
 && [[ "$v" =~ ^[0-9]+\.[0-9]+(\.[0-9]+)?\.[a-z0-9_]+$ ]] \
 && t="$s/data/godot/export_templates/$v" && mkdir -p "$(dirname "$t")" \
 && mv "$w/tpz/templates" "$t" && rm -rf "$w" \
 && chmod -R u+rwX,go+rX,go-w "$s" \
 && mkdir -p /opt/godot/official "/opt/godot-data/godot/export_templates/$v" \
 && ln "$s/godot" /opt/godot/official/godot \
 && ln "$t"/* "/opt/godot-data/godot/export_templates/$v/"

# Clancey/godot fork build c064 (visionOS-capable). visionos.zip is the
# templates archive's own copy, hardlinked (no second 200 MB file).
ARG GODOT_FORK_C064_EDITOR_SHA256
ARG GODOT_FORK_C064_TEMPLATES_SHA256
RUN --mount=type=bind,source=inputs,target=/inputs \
    echo "${GODOT_FORK_C064_EDITOR_SHA256:?}  /inputs/${GODOT_FORK_C064_EDITOR_SHA256:?}" | sha256sum -c --quiet - \
 && echo "${GODOT_FORK_C064_TEMPLATES_SHA256:?}  /inputs/${GODOT_FORK_C064_TEMPLATES_SHA256:?}" | sha256sum -c --quiet - \
 && s=/opt/godot/sets/fork-c064 && w=$(mktemp -d) \
 && install -D -m 0755 "/inputs/${GODOT_FORK_C064_EDITOR_SHA256:?}" "$s/godot" \
 && unzip -q "/inputs/${GODOT_FORK_C064_TEMPLATES_SHA256:?}" 'templates/*' -d "$w/tpz" \
 && v=$(tr -d '[:space:]' < "$w/tpz/templates/version.txt") \
 && [[ "$v" =~ ^[0-9]+\.[0-9]+(\.[0-9]+)?\.[a-z0-9_]+$ ]] \
 && t="$s/data/godot/export_templates/$v" && mkdir -p "$(dirname "$t")" \
 && mv "$w/tpz/templates" "$t" && rm -rf "$w" \
 && chmod -R u+rwX,go+rX,go-w "$s" \
 && ln "$t/visionos.zip" "$s/visionos.zip" \
 && mkdir -p /opt/godot-fork/c064 "/opt/godot-data/godot/export_templates/$v" \
 && ln "$s/godot" /opt/godot-fork/c064/godot \
 && ln "$s/godot" /opt/godot-fork/godot \
 && ln "$s/visionos.zip" /opt/godot-fork/c064/visionos.zip \
 && ln "$s/visionos.zip" /opt/godot-fork/visionos.zip \
 && ln "$t"/* "/opt/godot-data/godot/export_templates/$v/"

# Store tooling with rolling upstreams (the lock pins the bytes we admitted).
# steamcmd is NOT run here: its first run self-updates from Valve's unpinned
# CDN, so that happens at job runtime in the container, outside the locked
# image. The manifest lets lockctl's receipt prove /opt/steamcmd is exactly
# the pinned archive's files.
ARG STEAMCMD_SHA256
RUN --mount=type=bind,source=inputs,target=/inputs \
    echo "${STEAMCMD_SHA256:?}  /inputs/${STEAMCMD_SHA256:?}" | sha256sum -c --quiet - \
 && mkdir -p /opt/steamcmd /opt/universal-export \
 && tar -xzf "/inputs/${STEAMCMD_SHA256:?}" -C /opt/steamcmd \
 && test -x /opt/steamcmd/steamcmd.sh \
 && find /opt/steamcmd ! -type d -print0 | LC_ALL=C sort -z | xargs -0 sha256sum \
      | sed 's|  /opt/steamcmd/|  ./|' > /opt/universal-export/steamcmd.files.sha256

ARG OVR_PLATFORM_UTIL_SHA256
RUN --mount=type=bind,source=inputs,target=/inputs \
    echo "${OVR_PLATFORM_UTIL_SHA256:?}  /inputs/${OVR_PLATFORM_UTIL_SHA256:?}" | sha256sum -c --quiet - \
 && install -D -m 0755 "/inputs/${OVR_PLATFORM_UTIL_SHA256:?}" /opt/ovr/ovr-platform-util

# linux-apple glue: xcrun stand-in, bundle assembler, isolation probe and the
# clang/swift resource dirs (links into /opt/apple-sdk stay dangling until a
# job admits an SDK pack). dsymutil is darwin-tools' (Mach-O dSYMs); PATH puts
# /opt/linux-apple/bin before /opt/swift/bin so swift.org's copy never wins.
COPY bin/xcrun bin/apple-bundle-util bin/isolation-check /opt/linux-apple/bin/
COPY lib/setup-resource-dirs.sh /opt/linux-apple/
RUN /opt/linux-apple/setup-resource-dirs.sh \
 && ln -sf /opt/swift/bin/llvm-objcopy /opt/linux-apple/bin/llvm-strip \
 && ln -sf /opt/swift/bin/llvm-objdump /opt/linux-apple/bin/llvm-otool \
 && ln -sf /opt/darwin-tools/bin/dsymutil /opt/linux-apple/bin/dsymutil \
 && test ! -e /opt/apple-sdk

# Public Android debug keystore (androiddebugkey / android, the same
# well-known values every Android SDK install uses; NOT a release key).
# One file, hardlinked into every set's data dir and the legacy data dir.
RUN install -d -m 0755 /opt/android \
 && keytool -genkeypair -keystore /opt/android/debug.keystore \
      -storepass android -keypass android -alias androiddebugkey \
      -dname "CN=Android Debug,O=Android,C=US" -keyalg RSA -keysize 2048 -validity 10000 \
 && chmod 0644 /opt/android/debug.keystore \
 && for d in /opt/godot-data /opt/godot/sets/*/data; do \
      mkdir -p "$d/godot/keystores" && ln /opt/android/debug.keystore "$d/godot/keystores/debug.keystore"; \
    done

# DL3064: GODOT_ANDROID_KEYSTORE_DEBUG_PASSWORD is the public Android debug
# password ("android"), not a secret.
# hadolint ignore=DL3064
ENV XDG_DATA_HOME=/opt/godot-data \
    GODOT=/opt/godot/official/godot \
    GODOT_TEMPLATES_DIR=/opt/godot-data/godot/export_templates/4.7.2.stable \
    GODOT_ANDROID_KEYSTORE_DEBUG_PATH=/opt/android/debug.keystore \
    GODOT_ANDROID_KEYSTORE_DEBUG_USER=androiddebugkey \
    GODOT_ANDROID_KEYSTORE_DEBUG_PASSWORD=android \
    GODOT_FORK_SETS="c064" \
    GODOT_FORK_C064_COMMIT=c0643ef9d8e5c7efbe2893502611a77025cc0a86 \
    GODOT_FORK_C064_EDITOR=/opt/godot-fork/c064/godot \
    GODOT_FORK_C064_VISIONOS_TEMPLATE=/opt/godot-fork/c064/visionos.zip \
    GODOT_FORK_C064_TEMPLATES=/opt/godot-data/godot/export_templates/4.8.dev \
    GODOT_FORK=/opt/godot-fork/c064/godot \
    VISIONOS_GODOT=/opt/godot-fork/c064/godot \
    VISIONOS_TEMPLATE=/opt/godot-fork/c064/visionos.zip \
    FASTLANE_SKIP_UPDATE_CHECK=1 \
    FASTLANE_OPT_OUT_USAGE=YES \
    PATH=/opt/universal-export/bin:/opt/node/bin:/opt/ovr:/opt/steamcmd:/opt/linux-apple/bin:/opt/swift/bin:/opt/godot/official:/opt/android-sdk/cmdline-tools/latest/bin:/opt/android-sdk/platform-tools:/opt/jdk/bin:${PATH}

# The image's own tools, lock and smoke harness. set.json files index each
# Godot set (checked again by `godot-set verify`).
COPY toolchain.lock.json /opt/universal-export/
COPY lib/lockctl.py lib/apple_sdk_patches.py /opt/universal-export/lib/
COPY bin/godot-set bin/apple-sdk-admit bin/fastlane bin/apktool /opt/universal-export/bin/
COPY smoke/ /opt/universal-export/smoke/
RUN mapfile -t sets < <(godot-set list --ids) \
 && [ "${#sets[@]}" -gt 0 ] \
 && for id in "${sets[@]}"; do \
      python3 /opt/universal-export/lib/lockctl.py set-json "$id" --root "/opt/godot/sets/$id"; \
    done \
 && godot-set verify --all

# Gradle pre-warm (network on): real debug/release APK and AAB exports of a
# generated project for every set, plus the lock's maven_prewarm coordinates,
# so job exports run with GRADLE_USER_HOME already complete.
RUN /opt/universal-export/smoke/android_prewarm.sh

# Receipt last: re-verifies every component and the ENV above against the
# lock; the build fails if anything is off.
ARG RECIPE_DIGEST
RUN python3 /opt/universal-export/lib/lockctl.py receipt --recipe-digest "${RECIPE_DIGEST:?}"
LABEL org.opencontainers.image.title="rr-universal-export" \
      org.opencontainers.image.description="Universal Linux Godot export image (all platforms; Apple SDK admitted per job)" \
      universal-export.recipe-digest="${RECIPE_DIGEST}"
