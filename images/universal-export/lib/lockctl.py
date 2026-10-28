#!/usr/bin/env python3
"""Single entry point for the universal export image's toolchain lock.

Runs on the control Mac (validate, plan, build-args, check-dockerfile,
recipe-digest, verify-file) and inside the image (get, gradle-dist-dir,
set-json, verify-installed, receipt). Stdlib only; Python 3.12+.

Exit codes: 0 ok, 1 check failed, 2 usage / unknown key or set.
"""
from __future__ import annotations

import argparse
import datetime as _dt
import hashlib
import json
import os
import re
import shlex
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_LOCK = HERE.parent / "toolchain.lock.json"
HEX64 = re.compile(r"^[0-9a-f]{64}$")
HEX64_ANY = re.compile(r"(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])")
SET_ID = re.compile(r"^[a-z0-9][a-z0-9.-]*$")
ARG_NAME = re.compile(r"^[A-Z][A-Z0-9_]*$")
GRADLE_USER_HOME = "/opt/gradle-home"
UE_HOME = "/opt/universal-export"
FORK_FLAT_EDITOR = "/opt/godot-fork/godot"
STEAMCMD_DIR = "/opt/steamcmd"
STEAMCMD_MANIFEST = f"{UE_HOME}/steamcmd.files.sha256"  # written by the Dockerfile's steamcmd layer
LINUX_APPLE_BIN = "/opt/linux-apple/bin"
DARWIN_TOOLS_BIN = "/opt/darwin-tools/bin"
DOCKER_TAG = re.compile(r"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")
SDK_PKG = re.compile(r"^[a-z][a-z0-9-]*(;[A-Za-z0-9._-]+)*$")
DEB_NAME = re.compile(r"^[a-z0-9][a-z0-9+.-]+$")
KNOWN_PRESETS = {
    "linux-x86_64", "linux-arm64", "windows-x86_64", "macos", "web-nothreads",
    "android-apk-debug", "android-apk-release", "android-aab-release", "ios", "visionos",
}
RECIPE_PATHS = ("Dockerfile", ".dockerignore", "toolchain.lock.json", "bin", "lib", "smoke", "ruby")
RECIPE_SKIP = {"__pycache__", ".DS_Store", "vendor", ".bundle"}


class Fail(Exception):
    pass


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def load_lock(path: Path) -> dict:
    return json.loads(Path(path).read_text())


# --- validate --------------------------------------------------------------

def validate(lock: dict, lock_path: Path) -> list[str]:
    errs: list[str] = []
    e = errs.append
    if lock.get("schema") != 1:
        e("schema must be 1")
    image = lock.get("image", {})
    if not re.search(r"@sha256:[0-9a-f]{64}$", image.get("base", "")):
        e("image.base must be pinned by @sha256 digest")
    if not ARG_NAME.match(image.get("tag_prefix", "").upper()) or not image.get("protected_tags"):
        e("image.tag_prefix / image.protected_tags missing")

    inputs = lock.get("inputs", {})
    args_seen: dict[str, str] = {}
    for iid, inp in inputs.items():
        p = f"inputs.{iid}"
        if not ARG_NAME.match(str(inp.get("arg", ""))):
            e(f"{p}.arg must be UPPER_SNAKE")
        elif inp["arg"] in args_seen:
            e(f"duplicate arg {inp['arg']} ({args_seen[inp['arg']]}, {iid})")
        else:
            args_seen[inp["arg"]] = iid
        if not HEX64.match(str(inp.get("sha256", ""))):
            e(f"{p}.sha256 must be 64 lowercase hex")
        size = inp.get("size")
        if size is not None and (not isinstance(size, int) or size <= 0):
            e(f"{p}.size must be a positive int or null")
        if not inp.get("filename") or "/" in inp["filename"]:
            e(f"{p}.filename must be a bare file name")
        if inp.get("url") is None and not concrete_seeds(inp):
            e(f"{p} has no url and no concrete seed")

    sets = lock.get("godot_sets", {})
    defaults = [s for s, v in sets.items() if v.get("default")]
    if len(defaults) != 1:
        e(f"godot_sets must have exactly one default (found {defaults})")
    bt = lock.get("android", {}).get("build_tools")
    if f"build-tools;{bt}" not in lock.get("android", {}).get("packages", []):
        e(f"android.packages must contain build-tools;{bt}")
    for pkg in lock.get("android", {}).get("packages", []):
        if not SDK_PKG.match(str(pkg)):
            e(f"android.packages entry {pkg!r} is not a plain sdkmanager package path")
    for key in ("packages", "i386_packages"):
        names = lock.get("apt", {}).get(key)
        if not isinstance(names, list) or not names:
            e(f"apt.{key} must be a non-empty list")
            continue
        for name in names:
            if not DEB_NAME.match(str(name)):
                e(f"apt.{key} entry {name!r} is not a Debian package name")
    for sid, s in sets.items():
        p = f"godot_sets.{sid}"
        if not SET_ID.match(sid):
            e(f"set id {sid!r} must match {SET_ID.pattern}")
        for ref in (s.get("editor", {}).get("input"), s.get("templates", {}).get("input")):
            if ref not in inputs:
                e(f"{p} references unknown input {ref}")
        ed = s.get("editor", {})
        if not HEX64.match(str(ed.get("sha256", ""))) or not isinstance(ed.get("size"), int):
            e(f"{p}.editor needs sha256 and size")
        try:
            re.compile(s.get("version_regex", ""))
        except re.error as exc:
            e(f"{p}.version_regex: {exc}")
        if not s.get("version_regex", "").startswith("^"):
            e(f"{p}.version_regex must be anchored")
        required = s.get("templates", {}).get("required", [])
        if "version.txt" not in required:
            e(f"{p}.templates.required must include version.txt")
        presets = s.get("smoke_presets", [])
        for preset in presets:
            if preset not in KNOWN_PRESETS:
                e(f"{p}.smoke_presets: unknown preset {preset}")
        vis = s.get("visionos")
        if vis:
            if vis.get("template") not in required:
                e(f"{p}.visionos.template must be one of templates.required")
            if not HEX64.match(str(vis.get("sha256", ""))):
                e(f"{p}.visionos.sha256 must be 64 lowercase hex")
            if not s.get("fork_set_id") or not re.match(r"^[a-z0-9]+$", s["fork_set_id"]):
                e(f"{p}: a visionos set needs fork_set_id")
        if ("visionos" in presets) != bool(vis):
            e(f"{p}: the visionos preset requires visionos (and vice versa)")
        if s.get("android", {}).get("build_tools") != bt:
            e(f"{p}.android.build_tools must equal android.build_tools ({bt})")
        for kind, paths in s.get("legacy", {}).items():
            for lp in paths:
                if not str(lp).startswith("/"):
                    e(f"{p}.legacy.{kind}: {lp} must be absolute")

    ruby = lock.get("ruby", {})
    if ruby.get("bundler", {}).get("input") not in inputs:
        e("ruby.bundler.input references an unknown input")
    gl = lock_path.parent / ruby.get("gemfile_lock", "ruby/Gemfile.lock")
    if not gl.is_file():
        e(f"ruby.gemfile_lock missing: {gl}")
    elif sha256_file(gl) != ruby.get("gemfile_lock_sha256"):
        e(f"ruby.gemfile_lock_sha256 does not match {gl.name} (run ruby/relock.sh output)")

    for build, pack in lock.get("apple_sdk", {}).get("known_packs", {}).items():
        p = f"apple_sdk.known_packs.{build}"
        for k in ("tarball_sha256", "content_sha256"):
            if not HEX64.match(str(pack.get(k, ""))):
                e(f"{p}.{k} must be 64 lowercase hex")
        if pack.get("format") not in (1, 2):
            e(f"{p}.format must be 1 or 2")
        if pack.get("format") == 2 and not HEX64.match(str(pack.get("tree_sha256", ""))):
            e(f"{p}.tree_sha256 required for format 2")
        if pack.get("format") == 1 and not HEX64.match(str(pack.get("admitted_tree_sha256", ""))):
            e(f"{p}.admitted_tree_sha256 required for format 1 (digest of the admitted tree listing)")
    return errs


def concrete_seeds(inp: dict) -> list[str]:
    return [s for s in inp.get("seeds", []) if "<" not in s and ">" not in s]


# --- get / build-args / plan ------------------------------------------------

def get_path(lock, dotted: str):
    segs = dotted.split(".")
    node, i = lock, 0
    while i < len(segs):
        if isinstance(node, list):
            try:
                node, i = node[int(segs[i])], i + 1
                continue
            except (ValueError, IndexError):
                raise KeyError(dotted) from None
        if not isinstance(node, dict):
            raise KeyError(dotted)
        for j in range(len(segs), i, -1):
            key = ".".join(segs[i:j])
            if key in node:
                node, i = node[key], j
                break
        else:
            raise KeyError(dotted)
    return node


def build_args(lock: dict, recipe: str) -> list[tuple[str, str]]:
    out = [("BASE_IMAGE", lock["image"]["base"])]
    out += [(f"{inp['arg']}_SHA256", inp["sha256"]) for inp in lock["inputs"].values()]
    out += [
        ("ANDROID_SDK_PACKAGES", " ".join(lock["android"]["packages"])),
        ("ANDROID_BUILD_TOOLS_VERSION", lock["android"]["build_tools"]),
        ("APT_PACKAGES", " ".join(lock["apt"]["packages"])),
        ("APT_I386_PACKAGES", " ".join(lock["apt"].get("i386_packages", []))),
    ]
    if "gradle" in lock["inputs"]:
        out.append(("GRADLE_DIST_DIR", str(gradle_dist_dir(lock, {"GRADLE_USER_HOME": GRADLE_USER_HOME}))))
    out.append(("RECIPE_DIGEST", recipe))
    return out


def plan_rows(lock: dict) -> list[list[str]]:
    rows = []
    for iid, inp in lock["inputs"].items():
        seeds = concrete_seeds(inp)
        rows.append([
            iid, inp["sha256"], str(inp["size"]) if inp.get("size") else "-", inp["filename"],
            inp.get("url") or "-", "1" if inp.get("rolling") else "0", " ".join(seeds) or "-",
        ])
    return rows


def gradle_dist_dir(lock: dict, env=os.environ) -> Path:
    url = lock["inputs"]["gradle"]["url"]
    m = re.search(r"/(gradle-[0-9.]+-(?:bin|all))\.zip$", url)
    if not m:
        raise Fail(f"cannot parse Gradle distribution url {url}")
    n = int.from_bytes(hashlib.md5(url.encode()).digest(), "big")
    digits = "0123456789abcdefghijklmnopqrstuvwxyz"
    key = ""
    while n:
        n, r = divmod(n, 36)
        key = digits[r] + key
    home = Path(env.get("GRADLE_USER_HOME") or "/opt/gradle-home")
    return home / "wrapper" / "dists" / m.group(1) / key


# --- recipe digest ----------------------------------------------------------

def recipe_files(root: Path) -> list[Path]:
    files = []
    for top in RECIPE_PATHS:
        p = root / top
        if p.is_symlink() or p.is_file():
            files.append(p)
        elif p.is_dir():
            for dirpath, dirnames, filenames in os.walk(p):
                dirnames[:] = sorted(d for d in dirnames if d not in RECIPE_SKIP)
                files += [Path(dirpath) / f for f in filenames if f not in RECIPE_SKIP and not f.endswith(".pyc")]
    return sorted(files, key=lambda x: x.relative_to(root).as_posix())


def recipe_digest(root: Path) -> str:
    h = hashlib.sha256()
    for f in recipe_files(root):
        rel = f.relative_to(root).as_posix()
        if f.is_symlink():
            h.update(f"l\t{rel}\t{os.readlink(f)}\n".encode())
        else:
            mode = "755" if os.stat(f).st_mode & 0o111 else "644"
            h.update(f"{mode}\t{rel}\t{sha256_file(f)}\n".encode())
    return h.hexdigest()


# --- check-dockerfile -------------------------------------------------------

def dockerfile_instructions(text: str) -> list[tuple[int, str, str]]:
    out, buf, start = [], "", 0
    for n, raw in enumerate(text.splitlines(), 1):
        line = raw.rstrip()
        if not buf and (not line.strip() or line.lstrip().startswith("#")):
            continue
        if buf and line.lstrip().startswith("#"):
            continue
        if not buf:
            start = n
        if line.endswith("\\"):
            buf += line[:-1] + " "
            continue
        buf += line
        kw, _, rest = buf.strip().partition(" ")
        out.append((start, kw.upper(), rest.strip()))
        buf = ""
    if buf:
        kw, _, rest = buf.strip().partition(" ")
        out.append((start, kw.upper(), rest.strip()))
    return out


def check_dockerfile(lock: dict, text: str, allow_args=frozenset({"TARGETARCH"})) -> list[str]:
    errs: list[str] = []
    e = errs.append
    for n, line in enumerate(text.splitlines(), 1):
        if HEX64_ANY.search(line):
            e(f"line {n}: 64-hex literal (digests belong in the lock)")
    expected = {k for k, _ in build_args(lock, "0" * 64)}
    instrs = dockerfile_instructions(text)
    froms = [(n, rest) for n, kw, rest in instrs if kw == "FROM"]
    if len(froms) != 1:
        e(f"exactly one FROM expected, found {len(froms)}")
    elif froms[0][1] not in ("${BASE_IMAGE}", "$BASE_IMAGE"):
        e(f"line {froms[0][0]}: FROM must be ${{BASE_IMAGE}} (pinned in the lock)")
    declared: dict[str, int] = {}
    for n, kw, rest in instrs:
        if kw != "ARG":
            continue
        for tok in rest.split():
            name, eq, _ = tok.partition("=")
            if eq:
                e(f"line {n}: ARG {name} must not have a default")
            declared[name] = n
    for name in sorted(expected - set(declared)):
        e(f"ARG {name} missing (lockctl build-args passes it)")
    for name in sorted(set(declared) - expected - allow_args):
        e(f"line {declared[name]}: ARG {name} is not produced by lockctl build-args")
    runs = [(n, rest) for n, kw, rest in instrs if kw == "RUN"]
    for name in sorted(expected & set(declared)):
        if name == "BASE_IMAGE":
            continue
        guarded = re.compile(r"\$\{" + name + r":\?[^}]*\}")
        any_ref = re.compile(r"\$(?:\{" + name + r"\b|" + name + r"\b)")
        used = False
        for n, rest in runs:
            refs = len(any_ref.findall(rest))
            good = len(guarded.findall(rest))
            if refs != good:
                e(f"line {n}: unguarded reference to {name} (use ${{{name}:?}})")
            used = used or good > 0
        if not used:
            e(f"ARG {name} is never consumed by a RUN")
    for inp in lock["inputs"].values():
        name = f"{inp['arg']}_SHA256"
        verified = False
        for n, rest in runs:
            if f"/inputs/${{{name}:?}}" in rest and "sha256sum -c" in rest:
                verified = True
        if not verified:
            e(f"{name} is never verified with sha256sum -c against /inputs/${{{name}:?}}")
    for n, rest in runs:
        if "/inputs/" in rest:
            mounts = re.findall(r"--mount=(\S+)", rest)
            inp = [m for m in mounts if "target=/inputs" in m]
            if not inp or "source=inputs" not in inp[0] or "type=bind" not in inp[0]:
                e(f"line {n}: RUN uses /inputs without the inputs bind mount")
            elif re.search(r"(^|,)rw(=true)?(,|$)", inp[0]) or "readwrite" in inp[0]:
                e(f"line {n}: the inputs bind mount must be read-only (no rw)")
        if re.search(r"steamcmd(\.sh)?\s+\+", rest):
            e(f"line {n}: steamcmd must not run at build time (it self-updates from an unpinned source; jobs update it)")
    for n, kw, rest in instrs:
        if kw == "ADD":
            e(f"line {n}: ADD is forbidden (inputs come from the verified CAS)")
        if kw == "COPY":
            srcs = [t for t in rest.split() if not t.startswith("--")][:-1]
            if any(s.rstrip("/").split("/")[0] == "inputs" for s in srcs):
                e(f"line {n}: COPY of inputs would bake payloads into a layer; bind-mount them")
    return errs


# --- set-json ---------------------------------------------------------------

def set_json(lock: dict, set_id: str, root: Path) -> dict:
    s = lock["godot_sets"][set_id]
    errs = []
    editor = root / "godot"
    if not editor.is_file() or sha256_file(editor) != s["editor"]["sha256"] or editor.stat().st_size != s["editor"]["size"]:
        errs.append(f"editor {editor} does not match the lock pin")
    tdir = root / "data" / "godot" / "export_templates" / s["version"]
    files = {}
    if tdir.is_dir():
        for dirpath, _, names in os.walk(tdir):
            for name in names:
                f = Path(dirpath) / name
                files[f.relative_to(tdir).as_posix()] = sha256_file(f)
    for req in s["templates"]["required"]:
        if req not in files:
            errs.append(f"template {req} missing from {tdir}")
    vt = tdir / "version.txt"
    if vt.is_file() and vt.read_text().strip() != s["version"]:
        errs.append(f"version.txt says {vt.read_text().strip()!r}, lock says {s['version']!r}")
    vis = None
    if s.get("visionos"):
        vp = root / "visionos.zip"
        if not vp.is_file() or sha256_file(vp) != s["visionos"]["sha256"]:
            errs.append(f"visionos {vp} does not match the lock pin")
        elif files.get(s["visionos"]["template"]) != s["visionos"]["sha256"]:
            errs.append("visionos template member differs from the visionos pin")
        vis = {"path": str(vp), "sha256": s["visionos"]["sha256"]}
    ks = root / "data" / "godot" / "keystores" / "debug.keystore"
    if not ks.is_file():
        errs.append(f"debug keystore missing at {ks}")
    if errs:
        raise Fail("; ".join(errs))
    return {
        "schema": 1, "id": set_id, "default": bool(s.get("default")), "version": s["version"],
        "version_regex": s["version_regex"], "commit": s["commit"], "root": str(root),
        "editor": {"path": str(editor), "sha256": s["editor"]["sha256"]},
        "data_dir": str(root / "data"),
        "templates": {"dir": str(tdir), "files": dict(sorted(files.items()))},
        "visionos": vis,
        "keystore": {"path": str(ks), "sha256": sha256_file(ks)},
        "android": s["android"], "smoke_presets": s["smoke_presets"],
        "fork_set_id": s.get("fork_set_id"), "legacy": s["legacy"],
    }


# --- verify-installed / receipt ---------------------------------------------

@dataclass
class Check:
    name: str
    argv: list[str] = field(default_factory=list)
    expect: str | None = None          # substring of combined output
    files: list[str] = field(default_factory=list)  # must exist
    absent: list[str] = field(default_factory=list)  # must not exist
    sha256: tuple[str, str] | None = None            # (path, digest)
    same_inode: list[str] = field(default_factory=list)
    env: dict[str, str] = field(default_factory=dict)  # image ENV that must equal these values
    links: dict[str, str] = field(default_factory=dict)     # symlink path -> exact readlink target (must exist)
    resolves: dict[str, str] = field(default_factory=dict)  # command -> path it must resolve to on PATH
    tree_manifest: tuple[str, str] | None = None            # (root, sha256sum manifest of every non-dir entry)


def tree_manifest_problems(root: str, manifest: str) -> list[str]:
    """Compare root against a `sha256sum ./path` manifest: no missing, extra or changed files."""
    try:
        lines = Path(manifest).read_text().splitlines()
    except OSError as exc:
        return [f"cannot read {manifest}: {exc}"]
    want = {}
    for line in lines:
        m = re.fullmatch(r"([0-9a-f]{64})  \./(.+)", line)
        if not m:
            return [f"{manifest}: malformed line {line[:80]!r}"]
        want[m[2]] = m[1]
    if not want:
        return [f"{manifest} is empty"]
    have = set()
    for dirpath, dirnames, filenames in os.walk(root):
        for name in filenames + [d for d in dirnames if os.path.islink(os.path.join(dirpath, d))]:
            have.add(os.path.relpath(os.path.join(dirpath, name), root))
    problems = []
    if extra := sorted(have - want.keys()):
        problems.append(f"{len(extra)} file(s) in {root} not from the pinned archive: {', '.join(extra[:5])}")
    if missing := sorted(want.keys() - have):
        problems.append(f"{len(missing)} file(s) missing from {root}: {', '.join(missing[:5])}")
    changed = []
    for rel in sorted(want.keys() & have):
        try:
            if sha256_file(Path(root, rel)) != want[rel]:
                changed.append(rel)
        except OSError:
            changed.append(rel)
    if changed:
        problems.append(f"{len(changed)} file(s) in {root} changed since extraction: {', '.join(changed[:5])}")
    return problems


def _which(cmd: str, path_env: str) -> str | None:
    for d in path_env.split(os.pathsep):
        cand = os.path.join(d, cmd)
        if d and os.path.isfile(cand) and os.access(cand, os.X_OK):
            return cand
    return None


def android_package_revisions(lock: dict, root: str | None = None) -> dict[str, str | None]:
    """Pkg.Revision from each installed package's source.properties (None if unreadable).
    The lock pins package ids; platform-tools has no version pin, so this is its audit trail."""
    root = root or lock["android"]["home"]
    revs = {}
    for pkg in ["cmdline-tools;latest"] + list(lock["android"]["packages"]):
        rev = None
        try:
            for line in Path(root, *pkg.split(";"), "source.properties").read_text().splitlines():
                key, eq, val = line.partition("=")
                if eq and key.strip() == "Pkg.Revision":
                    rev = val.strip()
        except (OSError, UnicodeDecodeError):
            pass
        revs[pkg] = rev
    return revs


def install_checks(lock: dict, env=os.environ) -> list[Check]:
    i = lock["inputs"]
    ah = lock["android"]["home"]
    gd = gradle_dist_dir(lock, env)
    gv = i["gradle"]["version"]
    ks = lock["android"]["debug_keystore"]["path"]
    checks = [
        Check("input:swift", ["/opt/swift/bin/swift", "--version"], f"Swift version {i['swift']['version']}"),
        Check("input:darwin-tools", ["/opt/darwin-tools/bin/ld64.lld", "--version"], "LLD",
              files=["/opt/darwin-tools/bin/libtool", "/opt/darwin-tools/bin/dsymutil"]),
        Check("input:rcodesign", ["rcodesign", "--version"], i["rcodesign"]["version"]),
        Check("input:android-cmdline-tools", files=[f"{ah}/cmdline-tools/latest/bin/sdkmanager"]),
        Check("input:gradle", [f"{gd}/gradle-{gv}/bin/gradle", "--version", "--offline"], f"Gradle {gv}",
              files=[f"{gd}/gradle-{gv}-bin.zip.ok"]),
        Check("input:ovr-platform-util", sha256=("/opt/ovr/ovr-platform-util", i["ovr-platform-util"]["sha256"])),
        # steamcmd self-updates at job runtime; the image must hold exactly the pinned archive's files.
        Check("input:steamcmd", files=[f"{STEAMCMD_DIR}/steamcmd.sh"], tree_manifest=(STEAMCMD_DIR, STEAMCMD_MANIFEST)),
        Check("input:apktool", ["apktool", "--version"], i["apktool"]["version"]),
        Check("input:node", ["node", "--version"], f"v{i['node']['version']}"),
        Check("input:bundler", ["bundle", f"_{lock['ruby']['bundler']['version']}_", "--version"],
              lock["ruby"]["bundler"]["version"]),
        Check("ruby", ["ruby", "-e", "print RUBY_VERSION"], lock["ruby"]["ruby_version"]),
        Check("fastlane", ["fastlane", "--version"], f"fastlane {lock['ruby']['fastlane']}"),
        Check("java", ["java", "-version"], 'version "17.'),
        Check("python-modules", ["python3", "-c", "import yaml, PIL; print('ok')"], "ok"),
        Check("godot-sets", ["godot-set", "verify", "--all"], "OK"),
        Check("debug-keystore", same_inode=[ks, "/opt/godot-data/godot/keystores/debug.keystore"]
              + [f"/opt/godot/sets/{sid}/data/godot/keystores/debug.keystore" for sid in lock["godot_sets"]]),
        Check("debug-keystore-entry",
              ["keytool", "-list", "-v", "-keystore", ks, "-storepass",
               lock["android"]["debug_keystore"]["public_debug_password"],
               "-alias", lock["android"]["debug_keystore"]["alias"]], "CN=Android Debug"),
        Check("image-env", env=image_env(lock)),
        Check("linux-apple-dsymutil", links={f"{LINUX_APPLE_BIN}/dsymutil": f"{DARWIN_TOOLS_BIN}/dsymutil"},
              resolves={"dsymutil": f"{LINUX_APPLE_BIN}/dsymutil"}),
        Check("apple-sdk-absent", absent=[lock.get("apple_sdk", {}).get("mount", "/opt/apple-sdk")],
              files=["/opt/linux-apple/bin/xcrun", "/opt/linux-apple/bin/apple-bundle-util"]),
    ]
    checks += [Check(f"base:{t}", [t, "--version"]) for t in lock.get("base_tools", [])]
    for pkg in lock["android"]["packages"]:
        kind, _, ver = pkg.partition(";")
        path = {
            "platform-tools": f"{ah}/platform-tools/adb",
            "platforms": f"{ah}/platforms/{ver}/android.jar",
            "build-tools": f"{ah}/build-tools/{ver}/apksigner",
        }.get(kind, f"{ah}/{kind}/{ver}")
        checks.append(Check(f"android:{pkg}", files=[path]))
    return checks


def image_env(lock: dict) -> dict[str, str]:
    """The image ENV values that are derived from the lock (Dockerfile ENV must match)."""
    ah = lock["android"]["home"]
    dk = lock["android"]["debug_keystore"]
    sets = lock["godot_sets"]
    default = next(s for s in sets.values() if s.get("default"))
    default_templates = default["legacy"]["templates"][0]
    env = {
        "APPLE_SDK": lock["apple_sdk"]["mount"],
        "ANDROID_HOME": ah,
        "ANDROID_SDK_ROOT": ah,
        "ANDROID_BUILD_TOOLS_VERSION": lock["android"]["build_tools"],
        "GRADLE_USER_HOME": GRADLE_USER_HOME,
        "GODOT": default["legacy"]["editor"][0],
        "GODOT_TEMPLATES_DIR": default_templates,
        "XDG_DATA_HOME": str(Path(default_templates).parents[2]),
        "GODOT_ANDROID_KEYSTORE_DEBUG_PATH": dk["path"],
        "GODOT_ANDROID_KEYSTORE_DEBUG_USER": dk["alias"],
        "GODOT_ANDROID_KEYSTORE_DEBUG_PASSWORD": dk["public_debug_password"],
        "UE_HOME": UE_HOME,
    }
    forks = [s for s in sets.values() if s.get("fork_set_id")]
    if forks:
        env["GODOT_FORK_SETS"] = " ".join(s["fork_set_id"] for s in forks)
    for s in forks:
        k = f"GODOT_FORK_{s['fork_set_id'].upper()}"
        env[f"{k}_COMMIT"] = s["commit"]
        env[f"{k}_EDITOR"] = s["legacy"]["editor"][0]
        env[f"{k}_VISIONOS_TEMPLATE"] = s["legacy"]["visionos"][0]
        env[f"{k}_TEMPLATES"] = s["legacy"]["templates"][0]
        if FORK_FLAT_EDITOR in s["legacy"]["editor"]:
            # The linux-apple-runner:1.0 contract (AO, BobaShop, Fingerslingers, NSR).
            env["GODOT_FORK"] = s["legacy"]["editor"][0]
            env["VISIONOS_GODOT"] = s["legacy"]["editor"][0]
            env["VISIONOS_TEMPLATE"] = s["legacy"]["visionos"][0]
    return env


def _run(argv: list[str]) -> tuple[int, str]:
    try:
        p = subprocess.run(argv, capture_output=True, text=True, timeout=300, env={**os.environ, "FASTLANE_SKIP_UPDATE_CHECK": "1"})
        return p.returncode, p.stdout + p.stderr
    except (OSError, subprocess.TimeoutExpired) as exc:
        return 127, str(exc)


def evaluate(checks: list[Check], runner=_run, environ=None) -> list[dict]:
    environ = os.environ if environ is None else environ
    results = []
    for c in checks:
        problems = []
        for k, v in c.env.items():
            if environ.get(k) != v:
                problems.append(f"ENV {k}={environ.get(k)!r}, lock says {v!r}")
        for f in c.files:
            if not os.path.exists(f):
                problems.append(f"missing {f}")
        for f in c.absent:
            if os.path.lexists(f):
                problems.append(f"{f} must not exist in the image")
        if c.sha256:
            path, want = c.sha256
            if not os.path.isfile(path) or sha256_file(Path(path)) != want:
                problems.append(f"{path} sha256 != {want}")
        for f, target in c.links.items():
            try:
                got = os.readlink(f)
            except OSError:
                got = None
            if got != target or not os.path.exists(f):
                problems.append(f"{f} must be a symlink to existing {target} (got {got!r})")
        for cmd, want in c.resolves.items():
            got = _which(cmd, environ.get("PATH", ""))
            if got != want:
                problems.append(f"{cmd} resolves to {got!r} on PATH, want {want}")
        if c.tree_manifest:
            problems += tree_manifest_problems(*c.tree_manifest)
        if c.same_inode:
            ids = set()
            for f in c.same_inode:
                try:
                    st = os.stat(f)
                    ids.add((st.st_dev, st.st_ino))
                except OSError:
                    problems.append(f"missing {f}")
            if len(ids) > 1:
                problems.append("not one inode: " + ", ".join(c.same_inode))
        out = ""
        if c.argv:
            rc, out = runner(c.argv)
            if rc != 0:
                problems.append(f"{shlex.join(c.argv)} exited {rc}")
            elif c.expect and c.expect not in out:
                problems.append(f"{shlex.join(c.argv)} output lacks {c.expect!r}")
        results.append({"name": c.name, "ok": not problems, "detail": "; ".join(problems) or out.strip()[:200]})
    return results


def receipt(lock: dict, lock_path: Path, recipe: str, results: list[dict]) -> dict:
    try:
        dpkg = subprocess.run(["dpkg-query", "-W", "-f", "${Package}=${Version}\n"],
                              capture_output=True, text=True, check=True).stdout.split()
    except (OSError, subprocess.CalledProcessError):
        dpkg = []
    sets = {}
    for sid in lock["godot_sets"]:
        sj = Path("/opt/godot/sets") / sid / "set.json"
        sets[sid] = sha256_file(sj) if sj.is_file() else None
    return {
        "schema": 1,
        "image": lock["image"]["name"],
        "recipe_digest": recipe,
        "lock_sha256": sha256_file(lock_path),
        "built_at": _dt.datetime.now(_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "inputs": {k: v["sha256"] for k, v in lock["inputs"].items()},
        "set_json_sha256": sets,
        "verify": results,
        "dpkg": dpkg,
        "android_packages": android_package_revisions(lock, os.environ.get("ANDROID_SDK_ROOT")),
    }


# --- CLI --------------------------------------------------------------------

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="lockctl")
    ap.add_argument("--lock", type=Path, default=DEFAULT_LOCK)
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("validate")
    g = sub.add_parser("get")
    g.add_argument("--raw", action="store_true")
    g.add_argument("path")
    sub.add_parser("build-args")
    sub.add_parser("plan")
    sub.add_parser("gradle-dist-dir")
    rd = sub.add_parser("recipe-digest")
    rd.add_argument("--root", type=Path)
    cd = sub.add_parser("check-dockerfile")
    cd.add_argument("dockerfile", type=Path)
    vf = sub.add_parser("verify-file")
    vf.add_argument("input")
    vf.add_argument("file", type=Path)
    sj = sub.add_parser("set-json")
    sj.add_argument("set")
    sj.add_argument("--root", type=Path, required=True)
    sj.add_argument("--out", type=Path)
    vi = sub.add_parser("verify-installed")
    vi.add_argument("--json", action="store_true")
    ct = sub.add_parser("check-tag")
    ct.add_argument("tag")
    dt = sub.add_parser("default-tag")
    dt.add_argument("--date", help="YYYYMMDD (default: today, UTC)")
    rc = sub.add_parser("receipt")
    rc.add_argument("--recipe-digest", required=True)
    rc.add_argument("--out", type=Path, default=Path("/opt/universal-export/receipt.json"))
    a = ap.parse_args(argv)

    try:
        lock = load_lock(a.lock)
    except (OSError, ValueError) as exc:
        print(f"lockctl: cannot read lock {a.lock}: {exc}", file=sys.stderr)
        return 2
    root = a.lock.resolve().parent
    try:
        if a.cmd == "validate":
            errs = validate(lock, a.lock)
            for err in errs:
                print(f"lock: {err}", file=sys.stderr)
            return 1 if errs else 0
        if a.cmd == "get":
            try:
                v = get_path(lock, a.path)
            except KeyError:
                print(f"lockctl: no such key {a.path}", file=sys.stderr)
                return 2
            if a.raw and isinstance(v, list):
                print("\n".join(str(x) for x in v))
            elif a.raw and not isinstance(v, dict):
                print("" if v is None else v)
            else:
                print(json.dumps(v))
            return 0
        if a.cmd == "build-args":
            for k, v in build_args(lock, recipe_digest(root)):
                print(f"{k}={v}")
            return 0
        if a.cmd == "plan":
            for row in plan_rows(lock):
                print("\t".join(row))
            return 0
        if a.cmd == "gradle-dist-dir":
            print(gradle_dist_dir(lock))
            return 0
        if a.cmd == "recipe-digest":
            print(recipe_digest(a.root or root))
            return 0
        if a.cmd == "check-dockerfile":
            errs = check_dockerfile(lock, a.dockerfile.read_text())
            for err in errs:
                print(f"{a.dockerfile}: {err}", file=sys.stderr)
            return 1 if errs else 0
        if a.cmd == "check-tag":
            if not DOCKER_TAG.match(a.tag):
                print(f"lockctl: {a.tag!r} is not a valid image tag", file=sys.stderr)
                return 1
            if a.tag in lock["image"]["protected_tags"]:
                print(f"lockctl: {a.tag} is a protected tag; build.sh only creates new candidate tags",
                      file=sys.stderr)
                return 1
            return 0
        if a.cmd == "default-tag":
            date = a.date or _dt.datetime.now(_dt.timezone.utc).strftime("%Y%m%d")
            if not re.fullmatch(r"\d{8}", date):
                print(f"lockctl: --date must be YYYYMMDD, got {date!r}", file=sys.stderr)
                return 2
            print(f"{lock['image']['tag_prefix']}-{date}-{recipe_digest(root)[:12]}")
            return 0
        if a.cmd == "verify-file":
            inp = lock["inputs"].get(a.input)
            if inp is None:
                print(f"lockctl: unknown input {a.input}", file=sys.stderr)
                return 2
            size = a.file.stat().st_size
            got = sha256_file(a.file)
            if got != inp["sha256"] or (inp.get("size") and size != inp["size"]):
                print(f"{a.file}: sha256/size mismatch for {a.input}: {got}/{size}, "
                      f"lock {inp['sha256']}/{inp.get('size')}", file=sys.stderr)
                return 1
            return 0
        if a.cmd == "set-json":
            if a.set not in lock["godot_sets"]:
                print(f"lockctl: unknown set {a.set}", file=sys.stderr)
                return 2
            data = set_json(lock, a.set, a.root)
            out = a.out or a.root / "set.json"
            out.write_text(json.dumps(data, indent=2) + "\n")
            return 0
        if a.cmd in ("verify-installed", "receipt"):
            results = evaluate(install_checks(lock))
            failed = [r for r in results if not r["ok"]]
            for r in results:
                print(f"{'ok  ' if r['ok'] else 'FAIL'} {r['name']}: {r['detail'] if not r['ok'] else ''}".rstrip(),
                      file=sys.stderr)
            if a.cmd == "verify-installed" and a.json:
                print(json.dumps(results, indent=2))
            if a.cmd == "receipt":
                if not failed:
                    a.out.write_text(json.dumps(receipt(lock, a.lock, a.recipe_digest, results), indent=2) + "\n")
            return 1 if failed else 0
    except Fail as exc:
        print(f"lockctl: {exc}", file=sys.stderr)
        return 1
    return 2


if __name__ == "__main__":
    sys.exit(main())
