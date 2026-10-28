#!/usr/bin/env python3
"""B5 link smoke: build a signed .app from a Godot iOS/visionOS Xcode-project export on Linux.

  apple_link_smoke.py --platform ios|visionos --export DIR --work DIR [--json FILE]

Follows LogicLand tools/linux-apple (Xcode's SwiftDriver / CompileC / Ld lines): compile the
export's dummy.swift + dummy.cpp with `xcrun --sdk iphoneos|xros`, link the libgodot DEVICE
slice (+ MoltenVK on iOS) with ld64.lld through xcrun, assemble the bundle with
apple-bundle-util, ad-hoc sign with rcodesign, verify, and check LC_BUILD_VERSION.
Exit 0 pass, 1 fail, 64 usage. `plan()` is pure (no file system writes).
"""
from __future__ import annotations

import argparse
import json
import re
import shutil
import struct
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import checks  # noqa: E402
from checks import CheckError  # noqa: E402

PLATFORMS = {
    # platform: (sdk, target os, min-os key, min-os fallback, device family fallback, LC platform)
    "ios": ("iphoneos", "ios", "IPHONEOS_DEPLOYMENT_TARGET", "16.0", "1,2", 2),
    "visionos": ("xros", "xros", "XROS_DEPLOYMENT_TARGET", "26.0", "7", 11),
}
DEFAULT_BUNDLE_ID = "org.universalexport.smoke"
SWIFT_STUB = "import Foundation\n"
# Main::start(), exported by every Godot 4 libgodot.
GODOT_SYMBOL = b"__ZN4Main5startEv"


@dataclass
class Plan:
    platform: str
    export: Path
    work: Path
    name: str
    sdk: str
    target: str
    min_os: str
    bundle_id: str
    device_family: str
    expected_platform: int
    app: Path
    exe: Path
    files: dict = field(default_factory=dict)
    copies: list = field(default_factory=list)
    steps: list = field(default_factory=list)


def pbx_settings(text: str) -> dict:
    """Build settings, Release configurations first; values containing $( are unusable here."""
    release = re.findall(r'buildSettings = \{((?:(?!buildSettings = \{).)*?)\};\s*name = "?Release"?;', text, re.S)
    out = {}
    for block in release + [text]:
        for key, value in re.findall(r'\b([A-Z_][A-Z0-9_]*) = "?([^";\n]+)"?;', block):
            out.setdefault(key, value.strip())
        for key, body in re.findall(r'\b(OTHER_LDFLAGS) = \((.*?)\n\s*\);', block, re.S):
            out.setdefault(key + "[]", re.findall(r'"((?:[^"\\]|\\.)*)"|([^\s,"]+)', body))
    return out


def other_ldflags(s: dict) -> list[str]:
    if "OTHER_LDFLAGS[]" in s:
        tokens = [a or b for a, b in s["OTHER_LDFLAGS[]"]]
    else:
        tokens = s.get("OTHER_LDFLAGS", "").split()
    return [t for t in tokens if t and "$(" not in t and "${" not in t]


def _setting(s, key, default):
    v = s.get(key, "")
    return default if not v or "$(" in v or "${" in v else v


def archive_symbols(path, arch: str = "arm64") -> set[bytes]:
    """Defined symbols from a static archive's BSD symbol table (thin or fat; empty -> CheckError)."""
    data = checks._read(path)
    off = 0
    if data[:4] in (b"\xca\xfe\xba\xbe", b"\xca\xfe\xba\xbf"):
        fmt = ">IIQQII" if data[:4] == b"\xca\xfe\xba\xbf" else ">IIIII"
        (n,) = checks._unpack(">I", data, 4, "fat header")
        want = {v: k for k, v in checks.CPU_TYPES.items()}[arch]
        for i in range(min(n, 64)):
            cpu, _, foff, size = checks._unpack(fmt, data, 8 + i * struct.calcsize(fmt), "fat arch")[:4]
            if cpu == want:
                data, off = data[:foff + size], foff
                break
        else:
            raise CheckError(f"{path}: fat archive has no {arch} slice")
    if data[off:off + 8] != b"!<arch>\n":
        raise CheckError(f"{path}: not a static archive")
    pos = off + 8
    if pos + 60 > len(data):
        raise CheckError(f"{path}: archive has no members")
    head = data[pos:pos + 60]
    name, size = head[:16].decode("ascii", "replace").strip(), head[48:58].decode("ascii", "replace").strip()
    if not size.isdigit() or head[58:60] != b"`\n":
        raise CheckError(f"{path}: malformed archive member header")
    body = data[pos + 60:pos + 60 + int(size)]
    if name.startswith("#1/"):
        n = int(name[3:]) if name[3:].isdigit() else 0
        name, body = body[:n].rstrip(b"\0").decode("ascii", "replace"), body[n:]
    if not name.startswith("__.SYMDEF"):
        raise CheckError(f"{path}: archive has no symbol table (run ranlib)")
    is64 = name.startswith("__.SYMDEF_64")
    word = "<Q" if is64 else "<I"
    step = struct.calcsize(word)
    (ranlib_size,) = checks._unpack(word, body, 0, "symbol table")
    strings_at = step + ranlib_size
    (str_size,) = checks._unpack(word, body, strings_at, "symbol table")
    strtab = body[strings_at + step:strings_at + step + str_size]
    out = set()
    for i in range(0, ranlib_size, 2 * step):
        (sx,) = checks._unpack(word, body, step + i, "ranlib entry")
        end = strtab.find(b"\0", sx)
        out.add(strtab[sx:end if end >= 0 else len(strtab)])
    if not out:
        raise CheckError(f"{path}: archive defines no symbols")
    return out


def plan(export, platform: str, work) -> Plan:
    if platform not in PLATFORMS:
        raise CheckError(f"unknown platform {platform!r}")
    sdk, os_name, min_key, min_default, family_default, lc_platform = PLATFORMS[platform]
    export, work = Path(export).absolute(), Path(work).absolute()
    info = checks.check_xcode_export(export, platform)
    name = info["name"]
    s = pbx_settings(info["pbxproj"].read_text(errors="replace"))
    min_os = _setting(s, min_key, min_default)
    target = f"arm64-apple-{os_name}{min_os}"
    src = export / name
    cpp = src / "dummy.cpp"
    if not cpp.is_file():
        raise CheckError(f"{cpp} is missing (expected dummy.cpp in the export)")
    obj, gen = work / "obj", work / "gen"
    app = work / f"{name}.app"
    p = Plan(platform=platform, export=export, work=work, name=name, sdk=sdk, target=target, min_os=min_os,
             bundle_id=_setting(s, "PRODUCT_BUNDLE_IDENTIFIER", DEFAULT_BUNDLE_ID),
             device_family=_setting(s, "TARGETED_DEVICE_FAMILY", family_default),
             expected_platform=lc_platform, app=app, exe=app / name)
    xcrun = ["xcrun", "--sdk", sdk]

    # Libraries first, then frameworks (Xcode's order: -lMoltenVK -lgodot -framework X). Static
    # archives are force-loaded and libgodot must define GODOT_SYMBOL, so an empty or foreign
    # archive cannot produce a passing link.
    search, libs, frameworks = [], [], []
    slices = [(info["slice"], info["library"], True)]
    mvk = export / "MoltenVK.xcframework"
    if platform == "ios" and mvk.is_dir():
        slices.insert(0, (*checks.device_slice(mvk, platform), False))
    for slice_dir, lib, is_godot in slices:
        if lib.endswith(".framework"):
            search.append(f"-F{slice_dir}")
            frameworks += ["-framework", lib[:-len(".framework")]]
        elif lib.endswith(".a") and lib.startswith("lib"):
            archive = slice_dir / lib
            symbols = archive_symbols(archive)
            if is_godot and GODOT_SYMBOL not in symbols:
                raise CheckError(f"{archive}: does not define {GODOT_SYMBOL.decode()} (not a libgodot archive)")
            search.append(f"-L{slice_dir}")
            libs += ["-Xlinker", "-force_load", "-Xlinker", str(archive)]
        else:
            raise CheckError(f"{slice_dir / lib}: unsupported xcframework library type")
    if any(lib.endswith(".framework") for _, lib, g in slices if g):
        libs += ["-Xlinker", "-u", "-Xlinker", GODOT_SYMBOL.decode()]
    fw_search = [f for f in search if f.startswith("-F")]

    swift = src / "dummy.swift"
    if not swift.is_file():
        swift = gen / "dummy.swift"
        p.files[swift] = SWIFT_STUB
    header = src / "dummy.h"
    swiftmodule = obj / f"{name}.swiftmodule"
    # -parse-as-library: Xcode compiles dummy.swift with GeneratedAssetSymbols.swift, so no Swift main.
    swiftc = [*xcrun, "swiftc", "-target", target, "-module-name", name, "-O", "-g", "-swift-version", "5",
              "-parse-as-library", "-module-cache-path", str(work / "ModuleCache"), *fw_search]
    if header.is_file():
        swiftc += ["-import-objc-header", str(header)]
    swiftc += ["-c", str(swift), "-emit-module", "-emit-module-path", str(swiftmodule)]
    p.steps.append(("swiftc", swiftc, obj))

    cpp_o = obj / "dummy_cpp.o"
    p.steps.append(("clang", [*xcrun, "clang", "-x", "c++", "-target", target, "-std=gnu++17", "-stdlib=libc++",
                              "-Os", "-g", "-fvisibility=hidden", "-fvisibility-inlines-hidden", *fw_search,
                              "-c", str(cpp), "-o", str(cpp_o)], obj))

    filelist = obj / f"{name}.LinkFileList"
    p.files[filelist] = f"{cpp_o}\n{obj / (swift.stem + '.o')}\n"
    rpaths = (["/usr/lib/swift"] if platform == "ios" else []) + ["@executable_path/Frameworks"]
    link = [*xcrun, "clang++", "-Xlinker", "-reproducible", "-target", target, "-Os",
            "-Xlinker", "-no_warn_duplicate_libraries", *search, "-filelist", str(filelist)]
    for rp in rpaths:
        link += ["-Xlinker", "-rpath", "-Xlinker", rp]
    link += ["-Xlinker", "-dead_strip", "-stdlib=libc++", "-fobjc-link-runtime",
             "-Xlinker", "-add_ast_path", "-Xlinker", str(swiftmodule),
             *other_ldflags(s), *libs, *frameworks, "-o", str(p.exe)]
    p.steps.append(("link", link, obj))

    bundle = ["apple-bundle-util", "info-plist", "--sdk", sdk, "--src", str(src / f"{name}-Info.plist"),
              "--out", str(app / "Info.plist"), "--pkginfo", str(app / "PkgInfo"),
              "--min-os", min_os, "--device-family", p.device_family]
    for key, value in (("EXECUTABLE_NAME", name), ("PRODUCT_NAME", name),
                       ("PRODUCT_BUNDLE_IDENTIFIER", p.bundle_id),
                       ("MARKETING_VERSION", _setting(s, "MARKETING_VERSION", "1.0")),
                       ("CURRENT_PROJECT_VERSION", _setting(s, "CURRENT_PROJECT_VERSION", "1"))):
        bundle += ["--setting", f"{key}={value}"]
    p.steps.append(("bundle", bundle, work))
    p.steps.append(("sign", ["rcodesign", "sign", str(app)], work))

    for rel in (f"{name}.pck", "PrivacyInfo.xcprivacy"):
        if (export / rel).is_file():
            p.copies.append((export / rel, app / rel))
    return p


def _tail(path: Path, n: int = 15) -> str:
    try:
        return " | ".join(path.read_text(errors="replace").splitlines()[-n:])
    except OSError:
        return ""


def execute(p: Plan) -> dict:
    """Run a plan; returns the JSON report (status pass|fail)."""
    rep = {"status": "fail", "platform": p.platform, "name": p.name, "sdk": p.sdk, "target": p.target,
           "verify": None, "lc_build_version": None, "exe_sha256": None, "detail": ""}
    for d in (p.work / "obj", p.work / "gen", p.app):
        d.mkdir(parents=True, exist_ok=True)
    for path, text in p.files.items():
        path.write_text(text)
    for name, argv, cwd in p.steps:
        if name == "sign":
            for src, dst in p.copies:
                shutil.copy2(src, dst)
        log = p.work / f"{name}.log"
        with open(log, "w") as f:
            try:
                rc = subprocess.run(argv, cwd=cwd, stdout=f, stderr=subprocess.STDOUT,
                                    stdin=subprocess.DEVNULL).returncode
            except OSError as e:
                print(e, file=f)
                rc = 127
        if rc != 0:
            rep["detail"] = f"{name} failed (exit {rc}): {_tail(log)}"
            return rep

    verify = subprocess.run(["rcodesign", "verify", str(p.exe)], capture_output=True, text=True,
                            stdin=subprocess.DEVNULL)
    (p.work / "verify.log").write_text(verify.stdout + verify.stderr)
    if verify.returncode == 0:
        rep["verify"] = "verify"
    else:
        # rcodesign verify wants CMS data, which an ad-hoc signature does not have.
        info = subprocess.run(["rcodesign", "print-signature-info", str(p.exe)], capture_output=True,
                              text=True, stdin=subprocess.DEVNULL)
        (p.work / "print-signature-info.log").write_text(info.stdout + info.stderr)
        if info.returncode != 0 or p.bundle_id not in info.stdout:
            rep["detail"] = (f"rcodesign verify failed and print-signature-info does not show {p.bundle_id}: "
                             f"{_tail(p.work / 'verify.log', 5)}")
            return rep
        rep["verify"] = "print-signature-info"
    if not (p.app / "_CodeSignature" / "CodeResources").is_file():
        rep["detail"] = "signed bundle has no _CodeSignature/CodeResources"
        return rep
    try:
        (sl,) = checks.check_macho(p.exe, p.expected_platform, "arm64")
    except (CheckError, ValueError) as e:
        rep["detail"] = str(e)
        return rep
    rep.update(status="pass", lc_build_version=sl, exe_sha256=checks.sha256(p.exe),
               detail=f"linked {p.name}.app arm64 platform={sl['platform']} minos={sl['minos']}, "
                      f"ad-hoc signed ({rep['verify']})")
    return rep


class _Parser(argparse.ArgumentParser):
    def error(self, message):
        self.print_usage(sys.stderr)
        print(f"{self.prog}: {message}", file=sys.stderr)
        sys.exit(64)


def main(argv=None) -> int:
    ap = _Parser(prog="apple_link_smoke.py", description=__doc__.split("\n\n")[0])
    ap.add_argument("--platform", required=True, choices=sorted(PLATFORMS))
    ap.add_argument("--export", required=True)
    ap.add_argument("--work", required=True)
    ap.add_argument("--json")
    a = ap.parse_args(argv)
    work = Path(a.work)
    if work.exists() and (not work.is_dir() or any(work.iterdir())):
        ap.error(f"--work {work} must be absent or empty")
    try:
        rep = execute(plan(a.export, a.platform, work))
    except CheckError as e:
        rep = {"status": "fail", "platform": a.platform, "verify": None, "lc_build_version": None,
               "exe_sha256": None, "detail": str(e)}
    if a.json:
        Path(a.json).write_text(json.dumps(rep, indent=1, default=str) + "\n")
    print(f"apple_link_smoke {a.platform}: {rep['status']}: {rep['detail']}")
    return 0 if rep["status"] == "pass" else 1


if __name__ == "__main__":
    sys.exit(main())
