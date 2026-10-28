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
| `build.sh`, `build-safety.sh`, `cas.sh`, `build-lock.sh`, `safe_rm.sh` | Tower build orchestration (run from a Mac); remote safety checks use the streamed local helper, not a pre-existing remote copy. |
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

### Interim delivery: fetch into per-job scratch

RunnerRunner's Docker backend currently sets `Binds = null`. Use the existing init-step mechanism, not a RunnerRunner
code change: an admin-managed Apple target's **PreRunner** step fetches the pinned pack from the Tower share and admits
it before starting an ephemeral JIT runner. Set `ContinueOnError=false` and an initial `TimeoutSeconds=1800`, then
measure fetch/admission latency in the canary. This is the deployment contract; no target or share configuration is
changed by this recipe.

The init step must:

1. Select the approved share directory and Xcode build from target configuration, not job inputs. Keep the pack cached
   on the share; transfer it once per new Apple container. Do not fetch Xcode or rebuild the SDK in the job.
2. Check the free-byte capacity below, then create a private directory under `/opt/apple-sdk-fetch` (0700, `umask 077`). Keep it
   outside the checkout and RAM-backed runner work directory. Use that directory as `TMPDIR` for admission, so its
   private compressed copy does not consume `/tmp` tmpfs. The extracted tree always stages beside the destination,
   at `/opt/.apple-sdk.stage-<pid>`, and is atomically renamed to `/opt/apple-sdk`; no second extracted tree is made.
3. There is no generic credential-file injection today. The init step writes its SMB credentials to a private 0600 file
   (`APPLE_SDK_SMB_AUTH_FILE`) for `smbclient -A`; this avoids a password on **smbclient's** command line. It does not
   hide credentials from the job: RunnerRunner renders init scripts and step env into the container entrypoint argv,
   visible in Docker inspection and to the root job via `/proc/1/cmdline`. Removing the file does not erase that copy.
   Use only a dedicated, SDK-directory-scoped, read-only, rotatable account treated as job-readable, never reusable or
   privileged Tower credentials. An alternative is an explicitly approved credential-free read-only SDK export with
   host/network ACLs; this recipe does not authorize creating one. Disable tracing, never log secrets or place them in
   source/URLs, and keep credential setup/cleanup admin-managed. Do not assume `$RUNNER_TEMP` exists yet.
4. Fetch only the configured pack and run the existing admission CLI. For example, inside the private directory,
   after the preceding checks and credential setup:

   ```sh
   smbclient "$APPLE_SDK_SHARE" -A "$APPLE_SDK_SMB_AUTH_FILE" -D "$APPLE_SDK_REMOTE_DIR" \
     -c 'get apple-sdk-27A9275.tar.zst'
   TMPDIR="$PWD" apple-sdk-admit admit --build 27A9275 --pack "$PWD/apple-sdk-27A9275.tar.zst"
   ```

   `APPLE_SDK_SHARE` has the form `//<Tower-address>/<approved-share>`; both share variables are admin-owned. Use a name
   resolvable inside the container or an approved IP; do not assume mDNS `tower.local` resolves there. Run these commands
   with fail-on-error semantics. `apple-sdk-admit` takes a local `--pack`, not an SMB URL or `--source`.
   It checks the compressed size/hash, the pinned manifest and every file, and the lock-pinned tree structure before
   exposing the SDK. Neither the share nor a downloaded manifest supplies new pins.
5. Remove the downloaded archive, authentication file, and private fetch directory on success or failure; admission
   removes its own private copy and failed staging tree on handled errors. Any fetch, capacity, or verification failure
   aborts runner startup. Do not fall back to another pack or a stale SDK. Re-run `apple-sdk-admit verify` immediately
   before compile/sign. The sealed SDK is job-local, not immutable against root; normal container removal reclaims it.
   RunnerRunner retains stopped containers until cleanup, so merely exiting the job does not reclaim the disk.

Never put the SDK or credentials in image layers, Git, Actions artifacts, or a job-shared writable cache. The existing
`build.sh` smoke uses an operator-supplied read-only pack bind with `--network none`; it does not exercise this SMB fetch.
An approved target canary must separately demonstrate fetching, fail-closed admission, and cleanup.

#### Scratch capacity

For the locked **27A9275** pack, sizes come from `apple_sdk.known_packs`:

| Allocation | Bytes | Approximate size |
|---|---:|---:|
| Downloaded archive (`C`) | 71,354,724 | 68.05 MiB |
| Admission's private archive (`C`) | 71,354,724 | 68.05 MiB |
| Extracted SDK (`U`) | 924,768,955 | 881.93 MiB |
| Peak logical bytes (`2C + U`) | 1,067,478,403 | 0.994 GiB |

Reserve **at least 2 GiB free disk and capacity for 50,000 filesystem entries per concurrent Apple job**, in addition to the image,
checkout, build outputs, Gradle caches, and the host's normal free-space reserve. The SDK has 34,754 regular files,
5,153 directories and 334 symlinks (40,241 entries); the budget allows for allocation rounding and metadata, not just
logical file bytes. On filesystems with fixed inode pools, the host operator must also reserve **50,000 free inodes**
per job. On btrfs/zfs and overlay views without meaningful inode totals, do not interpret zero/unavailable `df -i`
totals as exhausted; check metadata capacity on the host instead. Init gates check free bytes; inode/metadata sizing
is a host requirement. Recalculate for every new pack and confirm physical usage in the first canary.

With the recommended layout, fetch, private copy and extraction all use the Docker writable-layer backing filesystem.
If an operator deliberately separates them, budget at least **256 MiB** on the fetch/`TMPDIR` filesystem and **1.75 GiB
plus the inode/metadata capacity above** on the `/opt/apple-sdk` parent filesystem. Do not silently use RAM for either allocation:
RunnerRunner's optional `scratch` setting makes `_work` and `/tmp` tmpfs (defaults 4 GiB / 1 GiB); that is distinct from
this SDK disk budget. These are deployment admission requirements, not quotas or reservations enforced by
`apple-sdk-admit`. Concurrency planning must also count retained containers until they are actually removed.

### Future read-only binds: design only, awaiting Clancey's decision

No Docker mount support is implemented here. If approved, add per-target **`sharedDirs[]`**, reusing the Tart schema
name and its entry fields `name`, `hostPath`, `readOnly` (`TartImageConfig.SharedDirs` / `SharedDirMount`), rather than a
second mount schema. Proposed configuration, **not accepted by Docker targets today**:

```json
{
  "sharedDirs": [
    {
      "name": "apple-sdk",
      "hostPath": "/mnt/user/<approved-share>/apple-sdk/27A9275",
      "readOnly": true
    }
  ]
}
```

For this proposal, the recognized `name: "apple-sdk"` maps to the fixed container destination `/opt/apple-sdk`; no
job-supplied destination or new `guestPath` is needed. Requirements for a future implementation:

- Only target administrators may configure entries. Workflow inputs, labels, environment variables, repositories and
  jobs cannot add entries or override either path or `readOnly`.
- The host administrator allowlists narrow, canonical absolute source directories on the **Docker daemon's host**.
  Validate at configuration time and revalidate on that host at launch, rejecting path traversal, symlink escapes,
  missing paths, arbitrary destinations, sockets/devices and broad host-root or credential-directory access. Prevent
  source substitution between validation and mounting; an allowlist alone is not protection against path races.
- Require `readOnly: true` for these Docker shares; reject writable or omitted flags rather than inheriting Tart's
  default false. Enforce a read-only bind (including any submounts, or reject them); do not grant privilege or mount
  capabilities that let the job undo it. Refuse startup if the source cannot be validated or mounted.
- Mount only an admin-prepared **format-2** tree and run `apple-sdk-admit verify-mounted --build <B>` against the
  image's lock before the runner starts. The current format-1 pack cannot use `verify-mounted`; preparing and pinning a
  format-2 replacement is a prerequisite. Do not write admission metadata into the shared mount or silently fall back.
  Drivers also use `verify-mounted` immediately before compile/sign on this route, not `verify` (which requires a
  job-local admission sidecar).
- Keep each host tree versioned and immutable while jobs use it. A read-only container mount does not stop host writers;
  verification must fail on drift. Jobs still need their own writable build/output scratch, but not a copied SDK tree.

Model/API persistence, admin UI, provisioning propagation, host allowlisting and Docker mount enforcement would be a
separate reviewed RunnerRunner change. The existing Tart behavior remains unchanged by this proposal.

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

## Docker-host capacity: proposal, not implemented

Every job's writes (`_work`, `/tmp`, Gradle copy-ups under `/opt/gradle-home`, the SDK fetch and `/opt/apple-sdk`) land
in the container writable layer, which is on the Docker filesystem unless RunnerRunner's tmpfs `scratch` is set. Exited
containers keep those bytes until RunnerRunner removes them. Every automatic removal path needs a healthy Server,
Postgres and HostWorker connection. After a normally reported exit, the container stays at least 10 minutes. On Tower,
Postgres reportedly shares that filesystem, which was reported full on 2026-10-28 (reported, not verified here).

Proposed, for Clancey's decision (no RunnerRunner, daemon or target change is made here):

- Reserve floor `R = max(30 GiB, 10%)` of the Docker filesystem, never budgeted. Admit a job only if
  `free − reservations of admitted, not-yet-removed jobs − its reservation ≥ R`, per host, failing closed on a missing
  or stale reading. A job reserves its budget if a writable-layer quota enforces it, and twice its budget otherwise.
  Without a quota, admission cannot stop a runaway job from consuming `R`. Today's
  `DynamicProvisioning:MinimumFreeDiskGb` is global, defaults to off and fails open.
- Provisional budgets: 12 GiB per non-Apple job; 16 GiB per Apple job, which includes the 2 GiB SDK scratch above.
  Replace them with measured peak × 1.5.
- HostWorker removes exited managed containers itself, after persisting the exit record and bounded logs, keeping at
  most one failed container per target for 24 h. This needs idempotent, acknowledged exit events.
- Cap job writes with a per-container writable-layer quota, or move them to per-job scratch on a separate filesystem.
  Relocate Postgres off the Docker filesystem.

The full design, with thresholds, budgets and the decision tree, is in the session design document (§12), summarised in
the Tower build packet.

## Building on Tower

See the Tower build packet. Building needs owner approval **and** confirmed Docker-root headroom (at least the
`build.sh` gate) after any user-approved cleanup. It also needs an exclusive window: Home holds all runner admission;
no other CI, image/tag operations, builds, or pruning may run. Finish the isolated Cooking proof and its disk-peak
measurement first. The following commands are templates, not permission to run:

```sh
# Populate only with operator-verified, explicitly approved Compose label pairs.
# Example shape: RR_SERVICES=(--allow-rr-service approved-project/server ...)
RR_SERVICES=()  # empty means NO running containers are permitted
images/universal-export/build.sh --preflight --apple-pack-remote /mnt/user/<share>/apple-sdk-27A9275.tar.zst \
  "${RR_SERVICES[@]}"
images/universal-export/build.sh --apple-pack-remote /mnt/user/<share>/apple-sdk-27A9275.tar.zst \
  "${RR_SERVICES[@]}" \
  --stage godot-fork-c064-editor=/path/on/tower/godot.linuxbsd.editor.x86_64
```

`--allow-rr-service PROJECT/SERVICE` is repeatable and accepts only
`server`, `postgres`, or `host-worker`. The running container must have exactly the approved Compose project/service
identity, `com.docker.compose.oneoff=False`, and no RunnerRunner job labels. Names alone grant nothing; missing/unknown
labels or probe errors fail closed. Unlabelled RR infrastructure requires an operator decision, not a broad bypass.
Checks run before staging/build, receipt and smoke; they are snapshots, not a scheduler lock, so the admission hold is
mandatory throughout.

Remote writes default to `/mnt/user/appdata/rr-builds/universal-export`. A nondefault `UE_REMOTE_ROOT` is refused unless
the caller explicitly supplies `--remote-root PATH`; this packet does not require that override. Roots must be narrow,
canonical absolute paths. Before directory creation, rsync, and receipt/smoke writes, the helper rejects symlink
components and requires `realpath` to equal the literal path. The checks include CAS-owned `ctx/inputs` and
`cache/{sha256,tmp,quarantine}`, and repeat immediately before CAS execution. Smoke leaves must be directories (or
absent), receipt leaves regular files (or absent). The operator must prevent concurrent path replacement.
`rsync --delete` is limited to the validated `ctx/` tree and its excludes, never the CAS or SDK share.

Startup free bytes must satisfy `max(100 GiB, projected build bytes + tail)`, with projected bytes defaulting to 25 GiB
and `tail = max(20 GiB, 10% of Docker-root filesystem size)`. Build, receipt and smoke each run under a remote watchdog:
pre-start, periodic (30 seconds by default), and final disk probes must pass. Failed or malformed probes are failures,
not unknown-but-successful measurements. Controls may be made stricter:

| Environment variable | Default / enforced constraint |
|---|---|
| `BUILD_START_MIN_BYTES` | 107374182400; at least 100 GiB |
| `BUILD_PROJECTED_BYTES` | 26843545600; zero does not disable admission |
| `BUILD_TAIL_MIN_BYTES` | 21474836480; at least 20 GiB |
| `BUILD_TAIL_PCT` | 10; range 10-100 |
| `BUILD_WATCHDOG_SECONDS` | 30; range 1-30 seconds |
| `BUILD_TIMEOUT_SECONDS`, `SMOKE_TIMEOUT_SECONDS` | 14400 / 7200; zero disables that deadline, not the watchdog |

Each supervised phase resolves and validates `DockerRootDir` once; subsequent/final samples run `df` against that
same path, without repeatedly querying a busy daemon. No daemon restart or Docker-root path change is allowed during
the exclusive window. Each probe has a 10-second timeout plus 2-second kill grace. Monitoring is not a quota and cannot prevent a burst
exhausting disk between polls. A low-space trip, probe failure, or phase timeout cancels only the tracked Docker CLI
PID (parentage checked; TERM then bounded KILL escalation). It does **not** prove the daemon's build or smoke container
stopped. The run exits `STOP UNKNOWN`, retains marked locks, its temporary tag and available evidence, and requires
owner-confirmed shutdown/headroom recovery before unlock or retry. Diagnostics name the host, smoke container, temporary
tag, and local/remote evidence locations. It never kills processes by name or prunes Docker.

`build.sh` creates exactly one new tag, `ue-<date>-<recipe12>`. The image is built under a private temporary tag
`ue-tmp-<tag>-<pid>` and moved to the final tag only after a successful build, and only if the final tag still does not
exist. This check and `docker tag` are not atomic; no other actor may change tags during the window. The receipt and
smoke use the final tag; the temporary tag is removed only after supervision and report/log validation succeed.

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
