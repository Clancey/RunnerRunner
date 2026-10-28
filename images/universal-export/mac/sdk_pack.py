#!/usr/bin/env python3
"""Helpers for make_sdk_bundle.sh (Mac side). Stdlib only; runs on macOS's python3 (3.9+).

  xcode-build PLIST              print ProductBuildVersion of Xcode's Contents/version.plist
  finalize --xcode X --stage S --out O --build B --clang-version V
                                 drop prebuilt-modules, normalise modes, patch, write apple-sdk.info,
                                 MANIFEST.sha256, MANIFEST.tree, a deterministic tar | zstd -19 -T0,
                                 the .sha256 file, and print the known_packs stanza on stdout
  rmtree PATH                    remove a stage dir (making it writable first, never following links)

MANIFEST/tree formats and the tree checks come from ../bin/apple-sdk-admit, so a
pack this writes is checked by the same code that will admit it.
"""
import argparse
import datetime
import hashlib
import importlib.machinery
import importlib.util
import json
import os
import plistlib
import re
import shutil
import stat
import subprocess
import sys
import tarfile
from pathlib import Path

sys.dont_write_bytecode = True
HERE = Path(os.path.realpath(__file__)).parent
PLATFORMS = ("XROS", "XRSimulator", "iPhoneOS", "iPhoneSimulator")
PATCHES = ["modulemap-obsolete-v1"]


def _load_admit():
    loader = importlib.machinery.SourceFileLoader("apple_sdk_admit", str(HERE.parent / "bin" / "apple-sdk-admit"))
    mod = importlib.util.module_from_spec(importlib.util.spec_from_loader(loader.name, loader))
    loader.exec_module(mod)
    return mod


admit = _load_admit()


class PackError(Exception):
    pass


def log(msg: str) -> None:
    print(f"make_sdk_bundle: {msg}", file=sys.stderr, flush=True)


def read_plist(path: Path) -> dict:
    try:
        with open(path, "rb") as f:
            data = plistlib.load(f)
    except (OSError, plistlib.InvalidFileException, ValueError) as e:
        raise PackError(f"cannot read {path}: {e}") from None
    if not isinstance(data, dict):
        raise PackError(f"{path} is not a plist dictionary")
    return data


def plist_str(path: Path, key: str) -> str:
    val = read_plist(path).get(key)
    if not isinstance(val, str) or not val:
        raise PackError(f"{path} has no {key}")
    return val


def xcode_build(plist: Path) -> str:
    build = plist_str(plist, "ProductBuildVersion")
    if not re.fullmatch(r"[0-9A-Za-z]+", build):
        raise PackError(f"{plist}: unexpected ProductBuildVersion {build!r}")
    return build


def sdk_identity(sdk: Path) -> str:
    """'<CanonicalName> <SDK build>', e.g. 'xros27.0 24M361' (as in the format-1 apple-sdk.info)."""
    settings = sdk / "SDKSettings.json"
    if settings.is_file():
        try:
            canon = json.loads(settings.read_text()).get("CanonicalName")
        except ValueError as e:
            raise PackError(f"cannot parse {settings}: {e}") from None
    else:
        canon = read_plist(sdk / "SDKSettings.plist").get("CanonicalName")
    if not isinstance(canon, str) or not canon:
        raise PackError(f"{sdk}: no CanonicalName in SDKSettings")
    return f"{canon} {plist_str(sdk / 'System/Library/CoreServices/SystemVersion.plist', 'ProductBuildVersion')}"


def force_rmtree(path: Path) -> None:
    for dirpath, _, _ in os.walk(path):
        os.chmod(dirpath, 0o700)
    shutil.rmtree(path)


def normalise(stage: Path) -> None:
    """Drop every prebuilt-modules dir (only Apple's compiler can read them) and set the
    modes the tar will carry: dirs 0755, files 0755 with any x bit else 0644."""
    for dirpath, dirnames, filenames in os.walk(stage):
        os.chmod(dirpath, 0o755)
        if "prebuilt-modules" in dirnames:
            dirnames.remove("prebuilt-modules")
            victim = Path(dirpath, "prebuilt-modules")
            log(f"dropping {victim.relative_to(stage)}")
            if victim.is_symlink():
                victim.unlink()
            else:
                force_rmtree(victim)
        for name in filenames:
            path = Path(dirpath, name)
            mode = path.lstat().st_mode
            if stat.S_ISREG(mode):
                os.chmod(path, 0o755 if mode & 0o111 else 0o644)
            elif not stat.S_ISLNK(mode):
                raise PackError(f"{path}: not a regular file, directory or symlink")


def os_build() -> str:
    try:
        return subprocess.run(["sw_vers", "-buildVersion"], capture_output=True, text=True,
                              check=True).stdout.strip() or "unknown"
    except (OSError, subprocess.CalledProcessError):
        return "unknown"


def source_epoch(xcode: Path) -> int:
    """SOURCE_DATE_EPOCH, else the mtime of Xcode's version.plist: same Xcode -> same bytes."""
    env = os.environ.get("SOURCE_DATE_EPOCH")
    if env is not None:
        if not env.isdigit():
            raise PackError(f"SOURCE_DATE_EPOCH must be an integer, got {env!r}")
        return int(env)
    return int((xcode / "Contents" / "version.plist").stat().st_mtime)


def write_tar_zst(stage: Path, entries: dict, epoch: int, dest: Path) -> None:
    """Deterministic PAX tar ('./' first, then sorted './path', root:root, fixed mtime) | zstd -19 -T0."""
    zstd = shutil.which("zstd")
    if not zstd:
        raise PackError("zstd not found on PATH (brew install zstd)")
    fd = os.open(dest, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o644)
    try:
        proc = subprocess.Popen([zstd, "-19", "-T0", "-q", "-c"], stdin=subprocess.PIPE, stdout=fd)
    finally:
        os.close(fd)
    try:
        with tarfile.open(fileobj=proc.stdin, mode="w|", format=tarfile.PAX_FORMAT) as tf:
            for rel in [None] + sorted(entries):
                kind, target = entries[rel] if rel else ("d", None)
                ti = tarfile.TarInfo(f"./{rel}" if rel else ".")
                ti.uid = ti.gid = 0
                ti.uname = ti.gname = "root"
                ti.mtime = epoch
                if kind == "l":
                    ti.type, ti.linkname, ti.mode = tarfile.SYMTYPE, target, 0o777
                    tf.addfile(ti)
                elif kind == "d":
                    ti.type, ti.mode = tarfile.DIRTYPE, 0o755
                    tf.addfile(ti)
                else:
                    with os.fdopen(os.open(stage / rel, os.O_RDONLY | os.O_NOFOLLOW), "rb") as f:
                        ti.size, ti.mode = os.fstat(f.fileno()).st_size, 0o755 if kind == "x" else 0o644
                        tf.addfile(ti, f)
    finally:
        proc.stdin.close()
        if proc.wait() != 0:
            raise PackError(f"zstd exited with {proc.returncode}")


def file_sha256(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def finalize(xcode: Path, stage: Path, out: Path, build: str, clang_version: str) -> None:
    contents = xcode / "Contents"
    normalise(stage)
    for rec in admit.patches.apply(stage, PATCHES):
        log(f"patched {rec['path']}")
    sdks = {p: sdk_identity(contents / f"Developer/Platforms/{p}.platform/Developer/SDKs/{p}.sdk")
            for p in PLATFORMS}
    xcode_version = plist_str(contents / "version.plist", "CFBundleShortVersionString")
    epoch = source_epoch(xcode)
    info = [("XCODE_VERSION", xcode_version), ("XCODE_BUILD", build), ("CLANG_RESOURCE_VERSION", clang_version),
            ("BUILD_MACHINE_OS_BUILD", os_build()),
            ("PACKED_AT", datetime.datetime.fromtimestamp(epoch, datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"))]
    info += [(f"SDK_{p}", sdks[p]) for p in PLATFORMS] + [("FORMAT", "2")]
    (stage / "apple-sdk.info").write_text("".join(f"{k}={v}\n" for k, v in info))
    os.chmod(stage / "apple-sdk.info", 0o644)

    log("hashing the stage")
    tree = admit.scan_tree(stage)
    if set(tree.entries) & {admit.MANIFEST, admit.TREE}:
        raise PackError("the Xcode subset unexpectedly contains a MANIFEST file")
    manifest = admit.manifest_text(tree.hashes)
    (stage / admit.MANIFEST).write_bytes(manifest)
    tree.entries[admit.MANIFEST] = ("f", None)
    mtree = admit.tree_text(tree.entries)
    (stage / admit.TREE).write_bytes(mtree)
    tree.entries[admit.TREE] = ("f", None)
    tree.size += len(manifest) + len(mtree)
    for name in (admit.MANIFEST, admit.TREE):
        os.chmod(stage / name, 0o644)
    pin = {"format": 2, "content_sha256": admit.sha256(manifest), "tree_sha256": admit.sha256(mtree)}
    admit.check_tree(stage, tree, pin, counts=())  # the checks apple-sdk-admit will run, incl. symlink containment

    name = f"apple-sdk-{build}.tar.zst"
    final, sha_file, partial = out / name, out / f"{name}.sha256", out / f".{name}.partial-{os.getpid()}"
    log(f"writing {final} (zstd -19 -T0)")
    try:
        write_tar_zst(stage, tree.entries, epoch, partial)
        digest, size = file_sha256(partial), partial.stat().st_size
        try:
            with open(sha_file, "x") as f:
                f.write(f"{digest}  {name}\n")
        except FileExistsError:
            raise PackError(f"{sha_file} already exists; refusing to overwrite") from None
        try:
            os.link(partial, final)
        except FileExistsError:
            sha_file.unlink()
            raise PackError(f"{final} already exists; refusing to overwrite") from None
    finally:
        if partial.exists():
            partial.unlink()
    stats = tree.stats()
    stanza = {build: {"format": 2, "xcode_version": xcode_version, "clang_resource_version": clang_version,
                      "tarball_sha256": digest, "tarball_size": size, "content_sha256": pin["content_sha256"],
                      "tree_sha256": pin["tree_sha256"], **{k: stats[k] for k in admit.COUNTS},
                      "patches_required": [], "sdks": sdks}}
    log(f"wrote {final} ({size} bytes, {stats['regular_files']} files); add this to apple_sdk.known_packs:")
    print(json.dumps(stanza, indent=2))


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="sdk_pack.py")
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("xcode-build").add_argument("plist", type=Path)
    sub.add_parser("rmtree").add_argument("path", type=Path)
    p = sub.add_parser("finalize")
    for opt in ("--xcode", "--stage", "--out"):
        p.add_argument(opt, required=True, type=Path)
    p.add_argument("--build", required=True)
    p.add_argument("--clang-version", required=True)
    args = ap.parse_args(argv)
    try:
        if args.cmd == "xcode-build":
            print(xcode_build(args.plist))
        elif args.cmd == "rmtree":
            if args.path.is_dir() and not args.path.is_symlink():
                force_rmtree(args.path)
        else:
            finalize(args.xcode, args.stage, args.out, args.build, args.clang_version)
    except (PackError, admit.AdmitError, OSError) as e:
        log(f"error: {e}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
