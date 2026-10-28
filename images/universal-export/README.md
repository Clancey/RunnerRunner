# rr-universal-export

One Linux (linux/amd64) image that exports, compiles and signs every Godot game for every platform: Android (incl. Quest and
Android XR), iOS, visionOS, Linux, Windows, macOS desktop and web. It runs as a RunnerRunner Docker target on the Tower
Docker host. Images are local tags (`PullNever`); nothing is pushed to a registry.

macOS is needed only for:

- `actool`/`ibtool` (asset catalogs, storyboards, launch screens), `metal`, and Xcode-only plugin builds, run on the Tart
  pool. Results are cached on the Tower share and keyed by inputs plus Xcode build, so the Mac runs only when assets or
  Xcode change.
- producing the Apple SDK pack from an installed `Xcode.app` (`mac/make_sdk_bundle.sh`), once per Xcode build.

Everything else, including Swift/clang compile, `ld64.lld` link, `rcodesign` signing, IPA/pkg packaging and store
uploads, runs in this image.

## Layout

| Path | What |
|---|---|
| `Dockerfile` | Recipe. Every downloaded input is bind-mounted read-only from `inputs/<sha256>` and re-verified; nothing is fetched unpinned except apt and the Maven pre-warm (see the lock's notes). steamcmd is never run at build time, so the image holds exactly the pinned archive (the receipt checks every file); it self-updates from Valve at job runtime, outside the locked image. |
| `toolchain.lock.json` | The only source of pins: base image digest, every input (sha256, size, URL, seeds), Godot sets, Android SDK packages, Ruby gems, Apple SDK packs, apt. |
| `lib/lockctl.py` | Lock tool: `validate`, `plan`, `build-args`, `check-dockerfile`, `verify-installed`, `receipt`, `check-tag`, `default-tag`. Stdlib only. |
| `bin/godot-set` | Select and verify the Godot builds in the image. |
| `bin/apple-sdk-admit` | Admit a pinned Apple SDK pack into `/opt/apple-sdk`, or verify an admitted tree. |
| `bin/fastlane`, `bin/apktool` | Wrappers for the image's locked fastlane bundle (`ruby/Gemfile.lock`) and the pinned apktool jar. |
| `bin/xcrun`, `bin/apple-bundle-util`, `bin/isolation-check` | linux-apple glue from LogicLand (`/opt/linux-apple/bin`, which precedes `/opt/swift/bin` on `PATH` so `dsymutil` is darwin-tools', not swift.org's). |
| `smoke/` | Smoke harness: generates a tiny Godot project and exports it for every platform and Godot set (`run_smoke.sh`), plus the Android Gradle pre-warm run at build time. |
| `mac/` | Apple SDK pack producer (macOS only, not in the image). |
| `build.sh`, `cas.sh`, `build-lock.sh`, `safe_rm.sh` | Tower build orchestration (run from a Mac). |
| `tests/` | Static tests: `bash tests/run_all.sh`. |

## Godot sets

| Set | Godot | Use |
|---|---|---|
| `official-4.7.2` (default) | stock 4.7.2-stable editor + full template set | every stock export |
| `fork-c064` | Clancey/godot `c0643ef9d` (4.8.dev) editor + templates incl. visionOS | visionOS, fork-only features |

The default set is on `PATH` as `godot` (`$GODOT`). For a fork job:

```yaml
env:
  GODOT_SET: fork-c064
steps:
  - run: godot-set verify "$GODOT_SET" && godot-set github-env "$GODOT_SET"   # later steps see GODOT, VISIONOS_GODOT, ...
  # or, within one step:  eval "$(godot-set env "$GODOT_SET")"
```

The image also keeps the `linux-apple-runner:1.0` fork surface (`GODOT_FORK`, `VISIONOS_GODOT`, `VISIONOS_TEMPLATE`,
`/opt/godot-fork/godot`) pointing at `fork-c064`, so existing visionOS workflows run unchanged. `godot-set verify --all`
re-hashes every editor and template against the lock.

## Selecting the image

Workflows keep `runs-on: [self-hosted, Linux, X64, rr-<game>-linux]`. RunnerRunner maps the label to a target whose image is
`rr-universal-export:<tag>`. Rollout is per game: a canary target on a candidate tag, shadow builds, then the main target is
repointed. Rollback means repointing to the previous tag. Tags are never moved or pruned by `build.sh`.

## Apple SDK (host-bound input, never in the image)

The Xcode-derived SDK subset is a pack `apple-sdk-<XCODE_BUILD>.tar.zst` pinned in `apple_sdk.known_packs`. It is the
swappable seam: today it comes from `mac/make_sdk_bundle.sh` (format 2). A future xtool SDKBuilder adapter can produce the
same layout from `Xcode.xip` on Linux, and admission does not care which producer made the pack.

```sh
# Apple jobs, before compile/sign (fetch the pack however the target provides it, e.g. smbclient with injected creds):
apple-sdk-admit admit --build 27A9275 --pack "$RUNNER_TEMP/apple-sdk-27A9275.tar.zst"
apple-sdk-admit verify --build 27A9275     # full re-hash; call right before compile/sign if desired
```

Pins come only from the image's lock; no flag or environment variable can override them. Admission verifies the tarball
before extracting it, rejects unsafe entries, verifies every file, applies the format-1 modulemap patch where needed, and
renames the tree to `/opt/apple-sdk` (`$APPLE_SDK`). The tree's structure (entries, file types, exec bits, symlink
targets) is pinned in the lock: format 2 by `tree_sha256` (`MANIFEST.tree`), format 1 by `admitted_tree_sha256`, the
digest of the tool's tree listing of an admission (patching never changes it). The admission sidecar is never trusted
for content or structure. A read-only bind of a format-2 tree is checked with `verify-mounted`.
`isolation-check sdk-state` reports whether the SDK is mounted read-only, admitted (re-verified with `apple-sdk-admit
verify`; a record without its tree fails), or absent. The full `isolation-check` also exits nonzero when the network is
reachable or an ELF executable or build tool resolves inside the SDK.

A new Xcode means a new pack and a new lock entry, which makes a new image candidate (the Swift toolchain is paired with
the Xcode build). `known_packs` is keyed by Xcode build, so re-packing the same build replaces its entry.

## Secrets

The image and its build contain no secrets: no secret build args and no `--secret`. The smoke run asserts that no
secret-like env names are set. Jobs receive secrets as env from GitHub secrets or RunnerRunner env-var sets, write file
secrets to `$RUNNER_TEMP` with mode 0600, and never echo them. The Android debug keystore at
`/opt/android/debug.keystore` (`androiddebugkey`/`android`) is the standard public debug key, not a secret. Games never
upload Actions artifacts.

## Store uploads

Run `fastlane` directly (not `bundle exec`) so the image's locked bundle is used regardless of a game's own Gemfile. The
bundle includes `supply`, `pilot` and `deliver`. Meta's `ovr-platform-util` and Steam's `steamcmd.sh` are on `PATH`. The first `steamcmd.sh` run in a job self-updates
from Valve's unpinned CDN; that update lives only in the job's container, never in the image.

## Building on Tower

See the Tower build packet. In short, from a Mac with ssh access:

```sh
images/universal-export/build.sh --preflight --apple-pack-remote /mnt/user/<share>/apple-sdk-27A9275.tar.zst
images/universal-export/build.sh --apple-pack-remote /mnt/user/<share>/apple-sdk-27A9275.tar.zst \
  --stage godot-fork-c064-editor=/path/on/tower/godot.linuxbsd.editor.x86_64
```

`build.sh` creates exactly one new tag, `ue-<date>-<recipe12>`. The image is built under a private temporary tag
`ue-tmp-<tag>-<pid>` and moved to the final tag only after a successful build, and only if the final tag still does not
exist. The receipt and the smoke run against the final tag.

The exit code fails closed. `build.sh` fetches `smoke-report.json` and `smoke-report-logs.tar.gz`; both are required.
It then runs `smoke/report.py validate` against the lock, which checks:

- `schema` 1 and `network_isolated` true;
- every lock `godot_sets` entry, with each of its `smoke_presets`, present and passing;
- every hygiene row passing (`secret-env`, `verify-installed`, `isolation-check`, `ephemeral-keys`);
- the Apple result matching the pack.

The exits are:

- **0:** smoke rc 0 and a promotable report for the pack.
- **2:** smoke rc 2 and a valid no-pack report, with `--allow-no-apple-smoke` (not promotable).
- **1:** everything else.

The smoke run itself checks each artifact: a macOS zip needs a universal (`x86_64` + `arm64`) executable and a `.pck`
under `Contents/Resources`, and an AAB needs `BundleConfig.pb`, the base manifest, `base/dex/classes.dex` and
`base/lib/arm64-v8a/*godot*.so`. The iOS/visionOS link force-loads `libgodot` (and MoltenVK), so an empty archive cannot
link. `isolation-check` runs before and after the Apple SDK admission, and the log archive is written before the
report. At image build time, `android_prewarm.sh` fails if Gradle changes the Android SDK package set.

## Changing a pin

1. Edit `toolchain.lock.json` (new URL, sha256 and size; or a new Godot set or Apple pack). Never put a digest in the
   Dockerfile; `lockctl check-dockerfile` enforces this.
2. If the Dockerfile needs a new input, add an `ARG <ID>_SHA256` and use `/inputs/${<ID>_SHA256:?}` with `sha256sum -c`.
3. `bash tests/run_all.sh`, then build a candidate tag on Tower and run the smoke.
