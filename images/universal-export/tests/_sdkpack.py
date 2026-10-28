"""Synthetic Apple SDK packs and throwaway apple-sdk-admit installs for tests.

The pack formats are re-implemented here from the contract on purpose, so the
tests check the tool against the spec rather than against itself.
"""
from __future__ import annotations

import dataclasses
import hashlib
import io
import json
import os
import shutil
import stat
import subprocess
import sys
import tarfile
from pathlib import Path

UE = Path(__file__).resolve().parents[1]
TOOL = UE / "bin" / "apple-sdk-admit"
PATCHES = UE / "lib" / "apple_sdk_patches.py"

SDKS = "Developer/Platforms/XROS.platform/Developer/SDKs"
INCLUDE = f"{SDKS}/XROS.sdk/usr/include"
CLANG = "Developer/Toolchains/XcodeDefault.xctoolchain/usr/lib/clang"
MODULEMAP = f"{INCLUDE}/DarwinFoundation1.modulemap"

OBSOLETE = (
    b"// Obsolete C Standard Library headers replaced by the compiler.\n"
    b"module _c_standard_library_obsolete [system] {\n"
    b"  requires found_incompatible_headers__check_search_paths\n"
    b"\n"
    b'  header "float.h"\n'
    b"  export *\n"
    b"}\n"
)
MODULEMAP_CLEAN = b'module Darwin_C [system] {\n  header "stdio.h"\n  export *\n}\n\n'
MODULEMAP_UNPATCHED = MODULEMAP_CLEAN + OBSOLETE
# The patch removes only the module block; the comment line above it stays.
MODULEMAP_PATCHED = MODULEMAP_CLEAN + OBSOLETE.split(b"\n", 1)[0] + b"\n"


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def base_tree(patched: bool = False) -> dict:
    """rel path -> ('d',) | ('f', bytes, mode) | ('l', target). Parents are implied."""
    return {
        "version.plist": ("f", b"<plist><dict><key>ProductBuildVersion</key><string>T1</string></dict></plist>\n", 0o644),
        "apple-sdk.info": ("f", b"XCODE_VERSION=99.1\nXCODE_BUILD=T1\n", 0o644),
        f"{SDKS}/XROS.sdk/SDKSettings.json": ("f", b'{"CanonicalName": "xros99.0"}\n', 0o644),
        f"{INCLUDE}/stdio.h": ("f", b"int printf(const char *, ...);\n", 0o644),
        MODULEMAP: ("f", MODULEMAP_PATCHED if patched else MODULEMAP_UNPATCHED, 0o644),
        f"{SDKS}/XROS.sdk/usr/bin/tool": ("f", b"#!/bin/sh\nexit 0\n", 0o755),
        f"{SDKS}/XROS.sdk/usr/share/empty": ("d",),
        f"{SDKS}/XROS99.0.sdk": ("l", "XROS.sdk"),
        f"{SDKS}/XROS.sdk/usr/lib/libz.tbd": ("l", "libz.1.tbd"),
        f"{SDKS}/XROS.sdk/usr/lib/libz.1.tbd": ("f", b"--- !tapi-tbd\n", 0o644),
        f"{CLANG}/21/lib/darwin/libclang_rt.xros.a": ("f", b"!<arch>\n", 0o644),
        f"{CLANG}/current": ("l", "21"),
    }


def with_dirs(tree: dict) -> dict:
    out = dict(tree)
    for rel in tree:
        parts = rel.split("/")
        for i in range(1, len(parts)):
            out.setdefault("/".join(parts[:i]), ("d",))
    return out


def manifest_for(tree: dict) -> dict:
    return {rel: sha(e[1]) for rel, e in tree.items() if e[0] == "f" and not rel.startswith("MANIFEST.")}


def manifest_bytes(manifest: dict) -> bytes:
    return "".join(f"{h}  ./{rel}\n" for rel, h in sorted(manifest.items())).encode()


def tree_lines(tree: dict) -> list:
    lines = []
    for rel, e in sorted(tree.items()):
        if rel == "MANIFEST.tree":
            continue
        if e[0] == "d":
            lines.append(f"d\t./{rel}")
        elif e[0] == "f":
            lines.append(f"{'x' if e[2] & 0o111 else 'f'}\t./{rel}")
        else:
            lines.append(f"l\t./{rel}\t{e[1]}")
    return lines


@dataclasses.dataclass
class Pack:
    path: Path
    tarball_sha256: str
    tarball_size: int
    content_sha256: str
    tree_sha256: str | None
    fmt: int
    stats: dict
    admitted_tree_sha256: str | None = None

    def pin(self, **over) -> dict:
        p = {
            "format": self.fmt,
            "xcode_version": "99.1",
            "clang_resource_version": "21",
            "tarball_sha256": self.tarball_sha256,
            "tarball_size": self.tarball_size,
            "content_sha256": self.content_sha256,
            "patches_required": ["modulemap-obsolete-v1"] if self.fmt == 1 else [],
            "sdks": {"XROS": "xros99.0 T1SDK"},
            **self.stats,
        }
        if self.fmt == 2:
            p["tree_sha256"] = self.tree_sha256
        else:
            p["admitted_tree_sha256"] = self.admitted_tree_sha256
        p.update(over)
        return p


def _info(name: str, entry: tuple) -> tarfile.TarInfo:
    ti = tarfile.TarInfo(name)
    ti.uid = ti.gid = 0
    ti.uname = ti.gname = "root"
    ti.mtime = 1_700_000_000
    if entry[0] == "d":
        ti.type, ti.mode = tarfile.DIRTYPE, 0o755
    elif entry[0] == "f":
        ti.type, ti.mode, ti.size = tarfile.REGTYPE, entry[2], len(entry[1])
    else:
        ti.type, ti.mode, ti.linkname = tarfile.SYMTYPE, 0o755, entry[1]
    return ti


def raw(name: str, kind: str = "f", data: bytes = b"x", linkname: str = "", mode: int = 0o644):
    """An arbitrary extra tar member (for hostile-pack tests)."""
    ti = tarfile.TarInfo(name)
    ti.type = {"f": tarfile.REGTYPE, "d": tarfile.DIRTYPE, "l": tarfile.SYMTYPE, "h": tarfile.LNKTYPE,
               "c": tarfile.CHRTYPE, "b": tarfile.BLKTYPE, "p": tarfile.FIFOTYPE}[kind]
    ti.mode = mode
    ti.linkname = linkname
    if kind == "f":
        ti.size = len(data)
        return ti, data
    return ti, None


def build_pack(path: Path, tree: dict | None = None, *, fmt: int = 1, manifest: dict | None = None,
               mtree=None, unlisted: dict | None = None, extra: list = (),
               omit_manifest: bool = False) -> Pack:
    """Write a .tar.zst pack. `mtree(lines) -> lines` edits the MANIFEST.tree lines;
    `unlisted` entries are archived but kept out of both manifests; `extra` are raw
    (TarInfo, data) members appended verbatim."""
    tree = with_dirs(dict(tree if tree is not None else base_tree(patched=fmt == 2)))
    man = manifest if manifest is not None else manifest_for(tree)
    full = dict(tree)
    mbytes = manifest_bytes(man)
    if not omit_manifest:
        full["MANIFEST.sha256"] = ("f", mbytes, 0o644)
    tree_sha = None
    if fmt == 2:
        lines = tree_lines(full)
        lines = mtree(lines) if mtree else lines
        tbytes = "".join(line + "\n" for line in lines).encode()
        full["MANIFEST.tree"] = ("f", tbytes, 0o644)
        tree_sha = sha(tbytes)
    for rel, e in (unlisted or {}).items():
        full[rel] = e
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w", format=tarfile.PAX_FORMAT) as tf:
        tf.addfile(_info("./", ("d",)))
        for rel, e in sorted(full.items()):
            ti = _info("./" + rel, e)
            tf.addfile(ti, io.BytesIO(e[1]) if e[0] == "f" else None)
        for ti, data in extra:
            tf.addfile(ti, io.BytesIO(data) if data is not None else None)
    path.write_bytes(subprocess.run(["zstd", "-q", "-c", "-3"], input=buf.getvalue(),
                                    stdout=subprocess.PIPE, check=True).stdout)
    blob = path.read_bytes()
    members = list(full.values()) + [("f", d) if ti.isreg() else ("d",) if ti.isdir() else ("l",)
                                     for ti, d in extra]
    stats = {
        "unpacked_size": sum(len(e[1]) for e in members if e[0] == "f"),
        "regular_files": sum(1 for e in members if e[0] == "f"),
        "directories": 1 + sum(1 for e in members if e[0] == "d"),
        "symlinks": sum(1 for e in members if e[0] == "l"),
    }
    # Format 1 pins the digest of the whole extracted tree's listing (no MANIFEST.tree).
    listed = dict(full)
    for ti, data in extra:
        rel = ti.name[2:] if ti.name.startswith("./") else ti.name
        listed[rel] = (("d",) if ti.isdir() else ("f", data or b"", ti.mode) if ti.isreg()
                       else ("l", ti.linkname))
    admitted_tree = sha("".join(line + "\n" for line in tree_lines(with_dirs(listed))).encode())
    return Pack(path, sha(blob), len(blob), sha(mbytes), tree_sha, fmt, stats, admitted_tree)


def extract_plain(pack: Path, dest: Path) -> None:
    """Unpack like a host would before bind-mounting (no admission checks)."""
    data = subprocess.run(["zstd", "-dc", str(pack)], stdout=subprocess.PIPE, check=True).stdout
    dest.mkdir(parents=True)
    with tarfile.open(fileobj=io.BytesIO(data)) as tf:
        tf.extractall(dest, filter="tar")


def install_tool(root: Path, known_packs: dict, mount: str = "/opt/apple-sdk") -> Path:
    """Copy apple-sdk-admit + its lib into root/{bin,lib} with a synthetic lock."""
    (root / "bin").mkdir(parents=True)
    (root / "lib").mkdir()
    tool = root / "bin" / "apple-sdk-admit"
    shutil.copy2(TOOL, tool)
    shutil.copy2(PATCHES, root / "lib" / "apple_sdk_patches.py")
    write_lock(root, known_packs, mount)
    return tool


def write_lock(root: Path, known_packs: dict, mount: str = "/opt/apple-sdk") -> None:
    lock = {"schema": 1, "apple_sdk": {"mount": mount, "known_packs": known_packs}}
    (root / "toolchain.lock.json").write_text(json.dumps(lock, indent=2))


def run_tool(tool: Path, *args, env: dict | None = None) -> subprocess.CompletedProcess:
    return subprocess.run([sys.executable, str(tool), *map(str, args)], capture_output=True, text=True,
                          env={**os.environ, **(env or {})})


def make_writable(root: Path) -> None:
    if root.is_symlink() or not root.is_dir():
        return
    for dirpath, _, filenames in os.walk(root):
        os.chmod(dirpath, 0o755)
        for n in filenames:
            p = Path(dirpath, n)
            if not p.is_symlink():
                os.chmod(p, stat.S_IMODE(p.lstat().st_mode) | 0o200)


def make_readonly(root: Path) -> None:
    for dirpath, dirnames, filenames in os.walk(root, topdown=False):
        for n in filenames:
            p = Path(dirpath, n)
            if not p.is_symlink():
                os.chmod(p, stat.S_IMODE(p.lstat().st_mode) & ~0o222)
        os.chmod(dirpath, 0o555)


def kinds(root: Path) -> dict:
    """rel -> (type char, mode) for every entry under root (no symlink follow)."""
    out = {}
    for dirpath, dirnames, filenames in os.walk(root):
        for n in dirnames + filenames:
            p = Path(dirpath, n)
            st = p.lstat()
            out[str(p.relative_to(root))] = (stat.filemode(st.st_mode)[0], stat.S_IMODE(st.st_mode))
    return out
