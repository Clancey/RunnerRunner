"""smoke/checks.py on synthetic ELF / PE / Mach-O bytes and zip fixtures."""
import io
import plistlib
import struct
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

SMOKE = Path(__file__).resolve().parents[1] / "smoke"
sys.path.insert(0, str(SMOKE))
import checks  # noqa: E402

PRODUCT = "UE Smoke Pr\u00f8duct"


def elf(machine, cls=2):
    ident = b"\x7fELF" + bytes([cls, 1, 1, 0]) + bytes(8)
    return (ident + struct.pack("<HHI", 2, machine, 1)).ljust(64, b"\0")


def pe(machine=0x8664, magic=0x20B, sections=((".text", b"\x90" * 16), (".rsrc", b""))):
    opt_size = 240 if magic == 0x20B else 224
    head = bytearray(b"MZ".ljust(0x80, b"\0"))
    struct.pack_into("<I", head, 0x3C, 0x80)
    head += b"PE\0\0" + struct.pack("<HHIIIHH", machine, len(sections), 0, 0, 0, opt_size, 0x22)
    head += struct.pack("<H", magic).ljust(opt_size, b"\0")
    table_end = len(head) + 40 * len(sections)
    offset = (table_end + 0x1FF) & ~0x1FF
    table, raw = b"", b""
    for name, data in sections:
        data = data.ljust((len(data) + 0x1FF) & ~0x1FF, b"\0") or bytes(0x200)
        table += struct.pack("<8sIIIIIIHHI", name.encode()[:8], len(data), 0x1000, len(data),
                             offset + len(raw), 0, 0, 0, 0, 0)
        raw += data
    return bytes(head + table).ljust(offset, b"\0") + raw


def ver(s):
    parts = [int(x) for x in s.split(".")] + [0, 0]
    return parts[0] << 16 | parts[1] << 8 | parts[2]


CPU = {"arm64": 0x0100000C, "x86_64": 0x01000007}


def macho(arch="arm64", platform=2, minos="16.0", legacy=None):
    if legacy:  # LC_VERSION_MIN_* instead of LC_BUILD_VERSION
        cmds = struct.pack("<IIII", legacy, 16, ver(minos), ver(minos))
    else:
        cmds = struct.pack("<IIIIII", 0x32, 24, platform, ver(minos), ver("27.0"), 0)
    cmds = struct.pack("<II", 0x19, 8) + cmds  # an unrelated command first
    return struct.pack("<IiiIIIII", 0xFEEDFACF, CPU[arch], 0, 2, 2, len(cmds), 0, 0) + cmds


def fat(*slices, fat64=False):
    entry = ">IIQQII" if fat64 else ">IIIII"
    header = struct.pack(">II", 0xCAFEBABF if fat64 else 0xCAFEBABE, len(slices))
    offset, entries, body = 0x1000, b"", b""
    for data in slices:
        cputype = struct.unpack_from("<i", data, 4)[0]
        fields = (cputype, 0, offset + len(body), len(data), 12) + ((0,) if fat64 else ())
        entries += struct.pack(entry, *fields)
        body += data.ljust(0x1000, b"\0")
    return (header + entries).ljust(offset, b"\0") + body


def zip_bytes(entries):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        for name, data in entries.items():
            z.writestr(name, data)
    return buf.getvalue()


GODOT_SYMBOL = b"__ZN4Main5startEv"


def _ar_member(name: bytes, data: bytes) -> bytes:
    if len(name) > 16:  # BSD long name: "#1/<len>", name prepended to the data
        data, name = name + data, b"#1/%d" % len(name)
    head = b"%-16s%-12d%-6d%-6d%-8s%-10d`\n" % (name, 0, 0, 0, b"100644", len(data))
    return head + data + (b"\n" if len(data) % 2 else b"")


def static_archive(symbols=(GODOT_SYMBOL,)) -> bytes:
    """A BSD ar archive with a __.SYMDEF table defining `symbols` and one object member."""
    strtab = b"".join(s + b"\0" for s in symbols)
    entries, at = b"", 0
    for s in symbols:
        entries += struct.pack("<II", at, 0)
        at += len(s) + 1
    table = struct.pack("<I", len(entries)) + entries + struct.pack("<I", len(strtab)) + strtab
    return b"!<arch>\n" + _ar_member(b"__.SYMDEF SORTED\0\0\0\0", table) + _ar_member(b"obj.o", b"\xcf\xfa\xed\xfe")


def xcframework(root, name, libs, archive=None):
    """libs: (identifier, library path, platform, variant or None). Static libraries are BSD
    archives defining the Godot entry symbol (libgodot*) or _<stem>; `archive` overrides the bytes."""
    fw = root / f"{name}.xcframework"
    avail = []
    for ident, path, plat, variant in libs:
        (fw / ident).mkdir(parents=True, exist_ok=True)
        if path.endswith(".framework"):
            (fw / ident / path).mkdir(parents=True, exist_ok=True)
        else:
            data = archive if archive is not None else static_archive(
                (GODOT_SYMBOL,) if path.startswith("libgodot") else (b"_" + path.split(".")[0].encode(),))
            (fw / ident / path).write_bytes(data)
        lib = {"LibraryIdentifier": ident, "LibraryPath": path, "SupportedArchitectures": ["arm64"],
               "SupportedPlatform": plat}
        if variant:
            lib["SupportedPlatformVariant"] = variant
        avail.append(lib)
    with open(fw / "Info.plist", "wb") as f:
        plistlib.dump({"AvailableLibraries": avail, "CFBundlePackageType": "XFWK"}, f)
    return fw


class TmpCase(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def write(self, name, data):
        path = self.tmp / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        return path


class ElfTest(TmpCase):
    def test_x86_64_and_arm64(self):
        checks.check_elf(self.write("a", elf(62)), "x86_64")
        checks.check_elf(self.write("b", elf(183)), "arm64")

    def test_wrong_machine(self):
        with self.assertRaisesRegex(checks.CheckError, "e_machine"):
            checks.check_elf(self.write("a", elf(62)), "arm64")

    def test_not_elf_or_32_bit(self):
        with self.assertRaises(checks.CheckError):
            checks.check_elf(self.write("a", b"MZ" + bytes(62)), "x86_64")
        with self.assertRaises(checks.CheckError):
            checks.check_elf(self.write("b", elf(62, cls=1)), "x86_64")
        with self.assertRaises(checks.CheckError):
            checks.check_elf(self.write("c", b"\x7fELF"), "x86_64")


class PeTest(TmpCase):
    def rsrc(self, payload):
        return pe(sections=((".text", b"\x90" * 32), (".rsrc", b"\0" * 7 + payload + b"\0" * 9)))

    def test_product_name_in_rsrc(self):
        info = checks.check_pe(self.write("a.exe", self.rsrc(PRODUCT.encode("utf-16-le"))), PRODUCT)
        self.assertEqual(info["machine"], 0x8664)
        self.assertIn(".rsrc", info["sections"])

    def test_name_outside_rsrc_or_not_utf16(self):
        only_text = pe(sections=((".text", PRODUCT.encode("utf-16-le")), (".rsrc", b"\0" * 64)))
        with self.assertRaisesRegex(checks.CheckError, "UTF-16"):
            checks.check_pe(self.write("a.exe", only_text), PRODUCT)
        with self.assertRaisesRegex(checks.CheckError, "UTF-16"):
            checks.check_pe(self.write("b.exe", self.rsrc(PRODUCT.encode())), PRODUCT)

    def test_no_rsrc(self):
        with self.assertRaisesRegex(checks.CheckError, r"\.rsrc"):
            checks.check_pe(self.write("a.exe", pe(sections=((".text", b"\x90"),))), PRODUCT)

    def test_machine_and_pe32(self):
        name = PRODUCT.encode("utf-16-le")
        with self.assertRaisesRegex(checks.CheckError, "machine"):
            checks.check_pe(self.write("a.exe", pe(machine=0x14C, sections=((".rsrc", name),))), PRODUCT)
        with self.assertRaisesRegex(checks.CheckError, "PE32"):
            checks.check_pe(self.write("b.exe", pe(magic=0x10B, sections=((".rsrc", name),))), PRODUCT)

    def test_garbage(self):
        for data in (b"", b"MZ" + bytes(10), b"MZ".ljust(0x40, b"\0") + b"\xff\xff\xff\x7f"):
            with self.assertRaises(checks.CheckError):
                checks.check_pe(self.write("g.exe", data), PRODUCT)


class MachOTest(TmpCase):
    def test_thin_platforms(self):
        for plat, num in (("ios", 2), ("xros", 11), ("visionos", 11), ("macos", 1)):
            path = self.write(f"{plat}.bin", macho(platform=num, minos="26.0"))
            (s,) = checks.check_macho(path, platform=plat, arch="arm64")
            self.assertEqual((s["arch"], s["platform"], s["minos"]), ("arm64", num, "26.0"))

    def test_platform_or_arch_mismatch(self):
        path = self.write("a", macho(platform=2))
        with self.assertRaisesRegex(checks.CheckError, "platform"):
            checks.check_macho(path, platform="xros")
        with self.assertRaisesRegex(checks.CheckError, "x86_64"):
            checks.check_macho(path, arch="x86_64")

    def test_fat_universal_with_legacy_version_min(self):
        data = fat(macho("x86_64", legacy=0x24, minos="10.12"), macho("arm64", platform=1, minos="11.0"))
        path = self.write("u", data)
        slices = checks.check_macho(path, platform="macos")
        self.assertEqual(sorted(s["arch"] for s in slices), ["arm64", "x86_64"])
        self.assertEqual({s["platform"] for s in slices}, {1})
        self.assertEqual(checks.check_macho(path, arch="x86_64")[0]["minos"], "10.12")

    def test_fat64(self):
        path = self.write("u64", fat(macho("arm64", platform=11), fat64=True))
        self.assertEqual(checks.check_macho(path, platform=11)[0]["arch"], "arm64")

    def test_no_build_version(self):
        bare = struct.pack("<IiiIIIII", 0xFEEDFACF, CPU["arm64"], 0, 2, 1, 8, 0, 0) + struct.pack("<II", 0x19, 8)
        with self.assertRaisesRegex(checks.CheckError, "platform"):
            checks.check_macho(self.write("a", bare), platform="ios")

    def test_garbage(self):
        for data in (b"", b"\x7fELF" + bytes(60), struct.pack("<I", 0xFEEDFACF) + bytes(4),
                     struct.pack(">II", 0xCAFEBABE, 1) + bytes(4)):
            with self.assertRaises(checks.CheckError):
                checks.check_macho(self.write("g", data))


class ZipTest(TmpCase):
    WEB = {"index.html": b"<html>", "index.js": b"", "index.wasm": b"\0asm", "index.pck": b"GDPC",
           "index.audio.worklet.js": b""}

    def test_web(self):
        checks.check_web_zip(self.write("w.zip", zip_bytes(self.WEB)))
        nested = {f"web/{k}": v for k, v in self.WEB.items()}
        checks.check_web_zip(self.write("n.zip", zip_bytes(nested)))
        for missing in ("index.html", "index.wasm", "index.pck"):
            entries = {k: v for k, v in self.WEB.items() if k != missing}
            with self.assertRaisesRegex(checks.CheckError, missing.rsplit(".", 1)[1]):
                checks.check_web_zip(self.write("m.zip", zip_bytes(entries)))

    AAB = {"base/manifest/AndroidManifest.xml": b"\0", "BundleConfig.pb": b"", "base/dex/classes.dex": b"dex",
           "base/lib/arm64-v8a/libgodot_android.so": b"\x7fELF"}

    def test_aab_and_zip_has(self):
        aab = self.write("a.aab", zip_bytes(self.AAB))
        checks.check_aab(aab)
        checks.check_zip_has(aab, ["BundleConfig.pb", "base/"])
        for missing, why in (("base/manifest/AndroidManifest.xml", "AndroidManifest"),
                             ("BundleConfig.pb", "BundleConfig"), ("base/dex/classes.dex", "classes.dex"),
                             ("base/lib/arm64-v8a/libgodot_android.so", r"arm64-v8a/\*godot")):
            with self.subTest(missing=missing), self.assertRaisesRegex(checks.CheckError, why):
                checks.check_aab(self.write("m.aab", zip_bytes({k: v for k, v in self.AAB.items() if k != missing})))
        for lib in ("base/lib/armeabi-v7a/libgodot_android.so", "base/lib/arm64-v8a/libc++_shared.so",
                    "base/lib/arm64-v8a/sub/libgodot_android.so", "lib/arm64-v8a/libgodot_android.so"):
            entries = {k: v for k, v in self.AAB.items() if "/lib/" not in k}
            entries[lib] = b"\x7fELF"
            with self.subTest(lib=lib), self.assertRaisesRegex(checks.CheckError, "godot"):
                checks.check_aab(self.write("l.aab", zip_bytes(entries)))
        with self.assertRaisesRegex(checks.CheckError, "lib/x86/"):
            checks.check_zip_has(aab, ["lib/x86/"])
        with self.assertRaises(checks.CheckError):
            checks.check_aab(self.write("c.aab", b"not a zip"))

    def test_macos_zip(self):
        info = plistlib.dumps({"CFBundleExecutable": "Smoke Game", "CFBundleIdentifier": "x.y"})
        good = {"Smoke Game.app/Contents/Info.plist": info,
                "Smoke Game.app/Contents/MacOS/Smoke Game": fat(macho("x86_64", 1), macho("arm64", 1)),
                "Smoke Game.app/Contents/Resources/Smoke Game.pck": b"GDPC"}
        found = checks.check_macos_zip(self.write("m.zip", zip_bytes(good)))
        self.assertEqual(found["executable"], "Smoke Game.app/Contents/MacOS/Smoke Game")
        self.assertEqual(found["app"], "Smoke Game.app")
        self.assertEqual(sorted(found["archs"]), ["arm64", "x86_64"])
        bad = dict(good)
        bad["Smoke Game.app/Contents/MacOS/Smoke Game"] = b"#!/bin/sh\n"
        with self.assertRaisesRegex(checks.CheckError, "Mach-O"):
            checks.check_macos_zip(self.write("b.zip", zip_bytes(bad)))
        with self.assertRaisesRegex(checks.CheckError, r"\.app"):
            checks.check_macos_zip(self.write("c.zip", zip_bytes({"readme.txt": b""})))
        for arch in ("x86_64", "arm64"):
            thin = dict(good)
            thin["Smoke Game.app/Contents/MacOS/Smoke Game"] = macho(arch, 1)
            with self.subTest(thin=arch), self.assertRaisesRegex(checks.CheckError, "not universal"):
                checks.check_macos_zip(self.write("t.zip", zip_bytes(thin)))
        for pck in (None, "Smoke Game.app/Contents/MacOS/Smoke Game.pck", "Smoke Game.app/Contents/Resources/x.txt",
                    "Other.app/Contents/Resources/Smoke Game.pck"):
            nopck = {k: v for k, v in good.items() if not k.endswith(".pck")}
            if pck:
                nopck[pck] = b"GDPC"
            with self.subTest(pck=pck), self.assertRaisesRegex(checks.CheckError, r"\.pck"):
                checks.check_macos_zip(self.write("p.zip", zip_bytes(nopck)))


class XcodeExportTest(TmpCase):
    def export(self, platform_id, ident):
        (self.tmp / "Smoke.xcodeproj").mkdir()
        (self.tmp / "Smoke.xcodeproj" / "project.pbxproj").write_text("// !$*UTF8*$!\n{}\n")
        xcframework(self.tmp, "Smoke", [(f"{ident}-simulator", "libgodot.a", platform_id, "simulator"),
                                        (ident, "libgodot.a", platform_id, None)])

    def test_ios_and_visionos(self):
        self.export("ios", "ios-arm64")
        found = checks.check_xcode_export(self.tmp, "ios")
        self.assertEqual(found["name"], "Smoke")
        self.assertEqual(Path(found["libgodot"]), self.tmp / "Smoke.xcframework" / "ios-arm64" / "libgodot.a")
        with self.assertRaisesRegex(checks.CheckError, "xros"):
            checks.check_xcode_export(self.tmp, "visionos")

    def test_visionos(self):
        self.export("xros", "xros-arm64")
        self.assertEqual(checks.check_xcode_export(self.tmp, "visionos")["name"], "Smoke")

    def test_missing_pieces(self):
        with self.assertRaisesRegex(checks.CheckError, "xcodeproj"):
            checks.check_xcode_export(self.tmp, "ios")
        (self.tmp / "Smoke.xcodeproj").mkdir()
        (self.tmp / "Smoke.xcodeproj" / "project.pbxproj").write_text("{}")
        with self.assertRaisesRegex(checks.CheckError, "xcframework"):
            checks.check_xcode_export(self.tmp, "ios")

    def test_device_slice_selection(self):
        fw = xcframework(self.tmp, "Lib", [("ios-arm64_x86_64-simulator", "libgodot.a", "ios", "simulator"),
                                           ("ios-arm64", "libgodot.a", "ios", None),
                                           ("xros-arm64", "libgodot.a", "xros", None)])
        self.assertEqual(checks.device_slice(fw, "ios"), (fw / "ios-arm64", "libgodot.a"))
        self.assertEqual(checks.device_slice(fw, "visionos"), (fw / "xros-arm64", "libgodot.a"))
        with self.assertRaisesRegex(checks.CheckError, "x86_64"):
            checks.device_slice(fw, "ios", arch="x86_64")


class CliTest(TmpCase):
    def cli(self, *args):
        return subprocess.run([sys.executable, str(SMOKE / "checks.py"), *map(str, args)],
                              capture_output=True, text=True)

    def test_exit_codes(self):
        x86 = self.write("a", elf(62))
        self.assertEqual(self.cli("elf", x86, "--machine", "x86_64").returncode, 0)
        r = self.cli("elf", x86, "--machine", "arm64")
        self.assertEqual(r.returncode, 1)
        self.assertIn("e_machine", r.stderr)
        exe = self.write("a.exe", pe(sections=((".rsrc", PRODUCT.encode("utf-16-le")),)))
        self.assertEqual(self.cli("pe", exe, "--product-name", PRODUCT).returncode, 0)
        ios = self.write("ios", macho(platform=2))
        self.assertEqual(self.cli("macho", ios, "--platform", "ios", "--arch", "arm64").returncode, 0)
        self.assertEqual(self.cli("macho", ios, "--platform", "11").returncode, 1)
        self.assertEqual(self.cli("bogus").returncode, 64)

    def test_macos_zip_prints_executable(self):
        info = plistlib.dumps({"CFBundleExecutable": "Smoke"})
        z = self.write("m.zip", zip_bytes({"Smoke.app/Contents/Info.plist": info,
                                           "Smoke.app/Contents/MacOS/Smoke": fat(macho("x86_64", 1), macho("arm64", 1)),
                                           "Smoke.app/Contents/Resources/Smoke.pck": b"GDPC"}))
        r = self.cli("macos-zip", z)
        self.assertEqual((r.returncode, r.stdout.strip()), (0, "Smoke.app/Contents/MacOS/Smoke"))

    def test_xcode_export_prints_name(self):
        (self.tmp / "exp" / "Game.xcodeproj").mkdir(parents=True)
        (self.tmp / "exp" / "Game.xcodeproj" / "project.pbxproj").write_text("{}")
        xcframework(self.tmp / "exp", "Game", [("ios-arm64", "libgodot.a", "ios", None)])
        r = self.cli("xcode-export", self.tmp / "exp", "--platform", "ios")
        self.assertEqual((r.returncode, r.stdout.strip()), (0, "Game"))


if __name__ == "__main__":
    unittest.main()
