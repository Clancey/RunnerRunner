#!/usr/bin/env python3
"""Artifact checks for the universal-export smoke run (stdlib only).

  checks.py elf FILE --machine x86_64|arm64
  checks.py pe FILE --product-name NAME          PE32+ x86_64 with NAME (UTF-16LE) in .rsrc
  checks.py macho FILE [--platform P] [--arch A] LC_BUILD_VERSION platform of every slice
  checks.py macos-zip ZIP                        prints the .app's main executable member
  checks.py web-zip ZIP | aab-zip AAB | zip-has ZIP MEMBER...  (MEMBER/ = any entry under it)
  checks.py xcode-export DIR --platform ios|visionos   prints the project name
  checks.py sha256 FILE

Exit 0 ok, 1 check failed, 64 usage.
"""
from __future__ import annotations

import argparse
import fnmatch
import hashlib
import plistlib
import struct
import sys
import zipfile
from pathlib import Path


class CheckError(Exception):
    pass


ELF_MACHINES = {"x86_64": 62, "arm64": 183}
PLATFORMS = {"macos": 1, "ios": 2, "tvos": 3, "watchos": 4, "xros": 11, "visionos": 11}
SLICE_PLATFORMS = {"ios": "ios", "visionos": "xros", "xros": "xros", "macos": "macos"}
CPU_TYPES = {0x01000007: "x86_64", 0x0100000C: "arm64", 7: "i386", 12: "arm"}
LC_BUILD_VERSION = 0x32
LC_VERSION_MIN = {0x24: 1, 0x25: 2, 0x2F: 3, 0x30: 4}


def _read(path) -> bytes:
    try:
        return Path(path).read_bytes()
    except OSError as e:
        raise CheckError(f"{path}: {e.strerror or e}") from None


def _unpack(fmt: str, data: bytes, off: int, what: str):
    if off < 0 or off + struct.calcsize(fmt) > len(data):
        raise CheckError(f"truncated {what}")
    return struct.unpack_from(fmt, data, off)


def sha256(path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# --- ELF ---------------------------------------------------------------------

def check_elf(path, machine: str) -> int:
    data = _read(path)[:64]
    if data[:4] != b"\x7fELF":
        raise CheckError(f"{path}: not an ELF file")
    if len(data) < 20:
        raise CheckError(f"{path}: truncated ELF header")
    if data[4] != 2:
        raise CheckError(f"{path}: not ELFCLASS64 (EI_CLASS={data[4]})")
    (e_machine,) = struct.unpack_from("<H" if data[5] == 1 else ">H", data, 18)
    if e_machine != ELF_MACHINES[machine]:
        raise CheckError(f"{path}: e_machine {e_machine}, expected {ELF_MACHINES[machine]} ({machine})")
    return e_machine


# --- PE ----------------------------------------------------------------------

def pe_info(data: bytes) -> dict:
    if data[:2] != b"MZ":
        raise CheckError("not a PE file (no MZ header)")
    (pe_off,) = _unpack("<I", data, 0x3C, "DOS header")
    if _unpack("4s", data, pe_off, "PE signature")[0] != b"PE\0\0":
        raise CheckError("no PE signature")
    machine, nsections, _, _, _, opt_size, _ = _unpack("<HHIIIHH", data, pe_off + 4, "COFF header")
    (magic,) = _unpack("<H", data, pe_off + 24, "optional header")
    sections = {}
    table = pe_off + 24 + opt_size
    for i in range(nsections):
        name, _, _, raw_size, raw_ptr = _unpack("<8sIIII", data, table + 40 * i, "section table")
        sections[name.rstrip(b"\0").decode("ascii", "replace")] = data[raw_ptr:raw_ptr + raw_size]
    return {"machine": machine, "magic": magic, "sections": sections}


def check_pe(path, product_name: str) -> dict:
    info = pe_info(_read(path))
    if info["magic"] != 0x20B:
        raise CheckError(f"{path}: optional header magic {info['magic']:#x}, expected PE32+ (0x20b)")
    if info["machine"] != 0x8664:
        raise CheckError(f"{path}: machine {info['machine']:#x}, expected 0x8664 (x86_64)")
    rsrc = info["sections"].get(".rsrc")
    if rsrc is None:
        raise CheckError(f"{path}: no .rsrc section (application/modify_resources did not run)")
    if product_name.encode("utf-16-le") not in rsrc:
        raise CheckError(f"{path}: .rsrc does not contain the UTF-16LE product name {product_name!r}")
    return info


# --- Mach-O ------------------------------------------------------------------

def _version(v: int) -> str:
    s = f"{v >> 16}.{(v >> 8) & 0xFF}"
    return s + (f".{v & 0xFF}" if v & 0xFF else "")


def _thin(data: bytes, off: int = 0) -> dict:
    (magic,) = _unpack("<I", data, off, "Mach-O header")
    if magic not in (0xFEEDFACF, 0xFEEDFACE):
        raise CheckError(f"not a little-endian Mach-O (magic {magic:#x})")
    cputype, subtype, _, ncmds, sizeofcmds, _ = _unpack("<iiIIII", data, off + 4, "Mach-O header")
    pos = off + (32 if magic == 0xFEEDFACF else 28)
    end = pos + sizeofcmds
    if end > len(data):
        raise CheckError("truncated load commands")
    arch = CPU_TYPES.get(cputype & 0xFFFFFFFF, f"cpu{cputype:#x}")
    if arch == "arm64" and subtype & 0xFF == 2:
        arch = "arm64e"
    found = {"arch": arch, "platform": None, "minos": None, "sdk": None}
    for _ in range(ncmds):
        cmd, size = _unpack("<II", data, pos, "load command")
        if size < 8 or pos + size > end:
            raise CheckError("malformed load command")
        cmd &= ~0x80000000
        if cmd == LC_BUILD_VERSION:
            plat, minos, sdk = _unpack("<III", data, pos + 8, "LC_BUILD_VERSION")
            found.update(platform=plat, minos=_version(minos), sdk=_version(sdk))
        elif cmd in LC_VERSION_MIN and found["platform"] is None:
            minos, sdk = _unpack("<II", data, pos + 8, "LC_VERSION_MIN")
            found.update(platform=LC_VERSION_MIN[cmd], minos=_version(minos), sdk=_version(sdk))
        pos += size
    return found


def macho_slices(data: bytes) -> list[dict]:
    if len(data) < 8:
        raise CheckError("truncated Mach-O")
    (magic,) = struct.unpack_from(">I", data, 0)
    if magic in (0xCAFEBABE, 0xCAFEBABF):
        (n,) = _unpack(">I", data, 4, "fat header")
        if not 0 < n < 64:
            raise CheckError(f"implausible fat arch count {n}")
        fmt = ">IIQQII" if magic == 0xCAFEBABF else ">IIIII"
        out = []
        for i in range(n):
            _, _, off, size = _unpack(fmt, data, 8 + i * struct.calcsize(fmt), "fat arch")[:4]
            if off + size > len(data):
                raise CheckError("fat slice past end of file")
            out.append(_thin(data[:off + size], off))
        return out
    return [_thin(data)]


def check_macho(path, platform=None, arch=None) -> list[dict]:
    try:
        slices = macho_slices(_read(path))
    except CheckError as e:
        raise CheckError(f"{path}: {e}") from None
    if arch and arch not in [s["arch"] for s in slices]:
        raise CheckError(f"{path}: no {arch} slice (have {[s['arch'] for s in slices]})")
    if arch:
        slices = [s for s in slices if s["arch"] == arch]
    if platform is not None:
        want = platform if isinstance(platform, int) else PLATFORMS.get(str(platform).lower())
        if want is None:
            if not str(platform).isdigit():
                raise CheckError(f"unknown platform {platform!r}")
            want = int(platform)
        for s in slices:
            if s["platform"] != want:
                raise CheckError(f"{path}: {s['arch']} slice platform {s['platform']}, expected {want} ({platform})")
    return slices


# --- zips --------------------------------------------------------------------

def _names(path) -> list[str]:
    try:
        with zipfile.ZipFile(path) as z:
            return z.namelist()
    except (OSError, zipfile.BadZipFile) as e:
        raise CheckError(f"{path}: not a readable zip ({e})") from None


def check_zip_has(path, members) -> None:
    names = _names(path)
    missing = [m for m in members
               if not (any(n.startswith(m) for n in names) if m.endswith("/") else m in names)]
    if missing:
        raise CheckError(f"{path}: missing {', '.join(missing)}")


def check_web_zip(path) -> None:
    base = {Path(n).name for n in _names(path)}
    missing = [ext for ext in (".html", ".js", ".wasm", ".pck") if not any(n.endswith(ext) for n in base)]
    if "index.html" not in base:
        missing.insert(0, "index.html")
    if missing:
        raise CheckError(f"{path}: web export lacks {', '.join(dict.fromkeys(missing))}")


AAB_MEMBERS = ("BundleConfig.pb", "base/manifest/AndroidManifest.xml", "base/dex/classes.dex")
AAB_GODOT_LIB = "base/lib/arm64-v8a/*godot*.so"


def check_aab(path) -> None:
    names = _names(path)
    missing = [m for m in AAB_MEMBERS if m not in names]
    if not any(fnmatch.fnmatchcase(n, AAB_GODOT_LIB) and n.count("/") == 3 for n in names):
        missing.append(AAB_GODOT_LIB)
    if missing:
        raise CheckError(f"{path}: missing {', '.join(missing)}")


MACOS_ARCHS = ("x86_64", "arm64")


def check_macos_zip(path) -> dict:
    names = _names(path)
    plists = sorted(n for n in names if n.count("/") == 2 and n.endswith(".app/Contents/Info.plist"))
    if not plists:
        raise CheckError(f"{path}: no top-level .app bundle with Contents/Info.plist")
    with zipfile.ZipFile(path) as z:
        try:
            info = plistlib.loads(z.read(plists[0]))
        except Exception as e:
            raise CheckError(f"{path}: bad {plists[0]}: {e}") from None
        app = plists[0].split("/")[0]
        exe = f"{app}/Contents/MacOS/{info.get('CFBundleExecutable', '')}"
        if exe not in names:
            raise CheckError(f"{path}: CFBundleExecutable {exe} is not in the zip")
        try:
            slices = macho_slices(z.read(exe))
        except CheckError as e:
            raise CheckError(f"{path}: {exe} is not a Mach-O executable ({e})") from None
    archs = [s["arch"] for s in slices]
    lacking = [a for a in MACOS_ARCHS if a not in archs]
    if lacking:
        raise CheckError(f"{path}: {exe} is not universal: no {', '.join(lacking)} slice (has {', '.join(archs)})")
    if not any(n.startswith(f"{app}/Contents/Resources/") and n.endswith(".pck") for n in names):
        raise CheckError(f"{path}: no .pck under {app}/Contents/Resources/")
    return {"app": app, "executable": exe, "archs": archs}


# --- Xcode exports -----------------------------------------------------------

def xcframework_slices(xcfw) -> list[dict]:
    try:
        with open(Path(xcfw) / "Info.plist", "rb") as f:
            return plistlib.load(f).get("AvailableLibraries", [])
    except Exception as e:
        raise CheckError(f"{xcfw}: unreadable Info.plist ({e})") from None


def device_slice(xcfw, platform: str, arch: str = "arm64") -> tuple[Path, str]:
    plat = SLICE_PLATFORMS[platform]
    for lib in xcframework_slices(xcfw):
        if (lib.get("SupportedPlatform") == plat and "SupportedPlatformVariant" not in lib
                and arch in lib.get("SupportedArchitectures", [])):
            return Path(xcfw) / lib["LibraryIdentifier"], lib["LibraryPath"]
    raise CheckError(f"{xcfw}: no {plat} {arch} device slice")


def check_xcode_export(export, platform: str) -> dict:
    export = Path(export)
    projects = sorted(export.glob("*.xcodeproj/project.pbxproj"))
    if len(projects) != 1:
        raise CheckError(f"{export}: expected one *.xcodeproj/project.pbxproj, found {len(projects)}")
    name = projects[0].parent.stem
    candidates = [export / f"{name}.xcframework"] + sorted(
        p for p in export.glob("*.xcframework") if p.name not in (f"{name}.xcframework", "MoltenVK.xcframework"))
    errors = []
    for fw in candidates:
        if not fw.is_dir():
            continue
        try:
            slice_dir, lib = device_slice(fw, platform)
        except CheckError as e:
            errors.append(str(e))
            continue
        if fw.name == f"{name}.xcframework" or lib.startswith("libgodot"):
            if not (slice_dir / lib).exists():
                raise CheckError(f"{slice_dir / lib} is listed in Info.plist but missing")
            return {"name": name, "pbxproj": projects[0], "xcframework": fw, "slice": slice_dir,
                    "library": lib, "libgodot": slice_dir / lib}
    raise CheckError(f"{export}: no libgodot xcframework with a device slice"
                     + (f" ({'; '.join(errors)})" if errors else ""))


# --- CLI ---------------------------------------------------------------------

class _Parser(argparse.ArgumentParser):
    def error(self, message):
        self.print_usage(sys.stderr)
        print(f"{self.prog}: {message}", file=sys.stderr)
        sys.exit(64)


def main(argv=None) -> int:
    ap = _Parser(prog="checks.py", description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True, parser_class=_Parser)
    p = sub.add_parser("elf")
    p.add_argument("file")
    p.add_argument("--machine", required=True, choices=sorted(ELF_MACHINES))
    p = sub.add_parser("pe")
    p.add_argument("file")
    p.add_argument("--product-name", required=True)
    p = sub.add_parser("macho")
    p.add_argument("file")
    p.add_argument("--platform")
    p.add_argument("--arch")
    for name in ("macos-zip", "web-zip", "aab-zip", "sha256"):
        sub.add_parser(name).add_argument("file")
    p = sub.add_parser("zip-has")
    p.add_argument("file")
    p.add_argument("members", nargs="+")
    p = sub.add_parser("xcode-export")
    p.add_argument("dir")
    p.add_argument("--platform", required=True, choices=["ios", "visionos"])
    a = ap.parse_args(argv)
    try:
        if a.cmd == "elf":
            check_elf(a.file, a.machine)
        elif a.cmd == "pe":
            check_pe(a.file, a.product_name)
        elif a.cmd == "macho":
            for s in check_macho(a.file, a.platform, a.arch):
                print(f"{s['arch']} platform={s['platform']} minos={s['minos']} sdk={s['sdk']}")
        elif a.cmd == "macos-zip":
            print(check_macos_zip(a.file)["executable"])
        elif a.cmd == "web-zip":
            check_web_zip(a.file)
        elif a.cmd == "aab-zip":
            check_aab(a.file)
        elif a.cmd == "zip-has":
            check_zip_has(a.file, a.members)
        elif a.cmd == "xcode-export":
            print(check_xcode_export(a.dir, a.platform)["name"])
        elif a.cmd == "sha256":
            print(sha256(a.file))
    except CheckError as e:
        print(f"check failed: {e}", file=sys.stderr)
        return 1
    except OSError as e:
        print(f"check failed: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
