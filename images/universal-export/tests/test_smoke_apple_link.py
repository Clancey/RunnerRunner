"""smoke/apple_link_smoke.py: pure link-plan construction, plus a run against fake tools."""
import json
import os
import plistlib
import stat
import subprocess
import sys
import tempfile
import textwrap
import unittest
from unittest import mock
from pathlib import Path

HERE = Path(__file__).resolve().parent
SMOKE = HERE.parent / "smoke"
sys.path[:0] = [str(SMOKE), str(HERE)]
import apple_link_smoke as als  # noqa: E402
import checks  # noqa: E402
from test_smoke_checks import GODOT_SYMBOL, static_archive, xcframework  # noqa: E402

PBX = """// !$*UTF8*$!
{{
	objects = {{
		D0 /* Release */ = {{
			isa = XCBuildConfiguration;
			buildSettings = {{
{settings}
			}};
			name = Release;
		}};
	}};
}}
"""
IOS_SETTINGS = {"IPHONEOS_DEPLOYMENT_TARGET": "16.0", "PRODUCT_BUNDLE_IDENTIFIER": "org.example.smoke",
                "TARGETED_DEVICE_FAMILY": '"1,2"', "MARKETING_VERSION": "1.2", "CURRENT_PROJECT_VERSION": "7",
                "OTHER_LDFLAGS": '"$(LD_CLASSIC_$(XCODE_VERSION_ACTUAL)) -Wl,-U,_smoke_init"'}
XROS_SETTINGS = {"XROS_DEPLOYMENT_TARGET": "26.0", "PRODUCT_BUNDLE_IDENTIFIER": "org.example.smoke.xr",
                 "TARGETED_DEVICE_FAMILY": "7", "OTHER_LDFLAGS": '(\n\t"$(inherited)",\n\t"-ObjC",\n)'}


def make_export(root, platform="ios", name="Smoke", settings=None, swift=True, header=True, pck=True,
                godot_slices=None, mvk=True):
    plat = "ios" if platform == "ios" else "xros"
    settings = IOS_SETTINGS if settings is None and platform == "ios" else XROS_SETTINGS if settings is None else settings
    (root / f"{name}.xcodeproj").mkdir(parents=True)
    body = "\n".join(f"\t\t\t\t{k} = {v};" for k, v in settings.items())
    (root / f"{name}.xcodeproj" / "project.pbxproj").write_text(PBX.format(settings=body))
    src = root / name
    src.mkdir()
    (src / "dummy.cpp").write_text("extern \"C\" void smoke() {}\n")
    if swift:
        (src / "dummy.swift").write_text("import Foundation\n")
    if header:
        (src / "dummy.h").write_text("\n")
    with open(src / f"{name}-Info.plist", "wb") as f:
        plistlib.dump({"CFBundleExecutable": "$(EXECUTABLE_NAME)"}, f)
    if pck:
        (root / f"{name}.pck").write_bytes(b"GDPC")
    slices = godot_slices or [(f"{plat}-arm64_x86_64-simulator", "libgodot.a", plat, "simulator"),
                              (f"{plat}-arm64", "libgodot.a", plat, None)]
    fw = xcframework(root, name, slices)
    if mvk:
        xcframework(root, "MoltenVK", [("ios-arm64", "libMoltenVK.a", "ios", None),
                                       ("ios-arm64_x86_64-simulator", "libMoltenVK.a", "ios", "simulator")])
    return fw


def arg_after(argv, flag):
    return argv[argv.index(flag) + 1]


class PlanCase(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.export = self.tmp / "export"
        self.work = self.tmp / "work"

    def tearDown(self):
        self._tmp.cleanup()

    def steps(self, plan):
        return {name: (argv, cwd) for name, argv, cwd in plan.steps}


class IosPlanTest(PlanCase):
    def test_ios(self):
        fw = make_export(self.export)
        p = als.plan(self.export, "ios", self.work)
        self.assertEqual((p.sdk, p.target, p.expected_platform), ("iphoneos", "arm64-apple-ios16.0", 2))
        self.assertEqual((p.name, p.bundle_id, p.device_family, p.min_os), ("Smoke", "org.example.smoke", "1,2", "16.0"))
        self.assertEqual(p.app, self.work / "Smoke.app")
        self.assertEqual(p.exe, p.app / "Smoke")
        self.assertEqual([s[0] for s in p.steps], ["swiftc", "clang", "link", "bundle", "sign"])
        for name, argv, _ in p.steps:
            self.assertTrue(all(isinstance(a, str) for a in argv), name)
            self.assertFalse([a for a in argv if "simulator" in a or "$(" in a], name)
            if argv[0] == "xcrun":
                self.assertEqual(argv[1:3], ["--sdk", "iphoneos"])
                self.assertEqual(arg_after(argv, "-target"), "arm64-apple-ios16.0")
        steps = self.steps(p)
        link, _ = steps["link"]
        mvk = self.export / "MoltenVK.xcframework"
        godot_a, mvk_a = str(fw / "ios-arm64" / "libgodot.a"), str(mvk / "ios-arm64" / "libMoltenVK.a")
        for flag in (f"-L{fw / 'ios-arm64'}", godot_a, f"-L{mvk / 'ios-arm64'}", mvk_a,
                     "-fobjc-link-runtime", "-stdlib=libc++", "-Wl,-U,_smoke_init", "/usr/lib/swift",
                     "@executable_path/Frameworks", "-dead_strip"):
            self.assertIn(flag, link)
        for lib in (godot_a, mvk_a):
            self.assertEqual(link[link.index(lib) - 3:link.index(lib)], ["-Xlinker", "-force_load", "-Xlinker"])
        self.assertFalse([a for a in link if a in ("-lgodot", "-lMoltenVK")])
        self.assertLess(link.index(mvk_a), link.index(godot_a))
        self.assertEqual(link[3], "clang++")
        self.assertEqual(link[-2:], ["-o", str(p.exe)])
        filelist = Path(arg_after(link, "-filelist"))
        self.assertEqual(p.files[filelist].split(), [str(self.work / "obj" / "dummy_cpp.o"),
                                                     str(self.work / "obj" / "dummy.o")])
        self.assertEqual(arg_after(link, "-add_ast_path"), "-Xlinker")
        swift, cwd = steps["swiftc"]
        self.assertEqual(cwd, self.work / "obj")
        self.assertEqual(arg_after(swift, "-import-objc-header"), str(self.export / "Smoke" / "dummy.h"))
        self.assertEqual(arg_after(swift, "-module-name"), "Smoke")
        self.assertIn(str(self.export / "Smoke" / "dummy.swift"), swift)
        self.assertEqual(arg_after(swift, "-emit-module-path"), str(self.work / "obj" / "Smoke.swiftmodule"))
        clang, _ = steps["clang"]
        self.assertEqual(clang[3:6], ["clang", "-x", "c++"])
        self.assertEqual(arg_after(clang, "-c"), str(self.export / "Smoke" / "dummy.cpp"))
        self.assertEqual(arg_after(clang, "-o"), str(self.work / "obj" / "dummy_cpp.o"))
        bundle, _ = steps["bundle"]
        self.assertEqual(bundle[:2], ["apple-bundle-util", "info-plist"])
        self.assertEqual(arg_after(bundle, "--src"), str(self.export / "Smoke" / "Smoke-Info.plist"))
        self.assertEqual(arg_after(bundle, "--out"), str(p.app / "Info.plist"))
        self.assertEqual((arg_after(bundle, "--min-os"), arg_after(bundle, "--device-family")), ("16.0", "1,2"))
        for setting in ("EXECUTABLE_NAME=Smoke", "PRODUCT_NAME=Smoke", "PRODUCT_BUNDLE_IDENTIFIER=org.example.smoke",
                        "MARKETING_VERSION=1.2", "CURRENT_PROJECT_VERSION=7"):
            self.assertIn(setting, bundle)
        self.assertEqual(steps["sign"][0], ["rcodesign", "sign", str(p.app)])
        self.assertIn((self.export / "Smoke.pck", p.app / "Smoke.pck"), p.copies)

    def test_framework_slice(self):
        make_export(self.export, mvk=False, godot_slices=[("ios-arm64", "Smoke.framework", "ios", None)])
        link = self.steps(als.plan(self.export, "ios", self.work))["link"][0]
        self.assertIn(f"-F{self.export / 'Smoke.xcframework' / 'ios-arm64'}", link)
        self.assertEqual(arg_after(link, "-framework"), "Smoke")
        self.assertEqual(arg_after(link, "-u"), "-Xlinker")
        self.assertIn(GODOT_SYMBOL.decode(), link)
        self.assertNotIn("-force_load", link)


class VisionosPlanTest(PlanCase):
    def test_visionos(self):
        fw = make_export(self.export, "visionos")
        p = als.plan(self.export, "visionos", self.work)
        self.assertEqual((p.sdk, p.target, p.expected_platform), ("xros", "arm64-apple-xros26.0", 11))
        self.assertEqual((p.bundle_id, p.device_family), ("org.example.smoke.xr", "7"))
        link = self.steps(p)["link"][0]
        self.assertEqual(link[1:3], ["--sdk", "xros"])
        self.assertIn(f"-L{fw / 'xros-arm64'}", link)
        self.assertIn("-ObjC", link)
        self.assertFalse([a for a in link if "MoltenVK" in a], "MoltenVK is iOS only")
        self.assertIn(str(fw / "xros-arm64" / "libgodot.a"), link)
        self.assertNotIn("/usr/lib/swift", link)
        self.assertNotIn("$(inherited)", link)

    def test_defaults_when_pbxproj_is_sparse(self):
        make_export(self.export, "visionos", settings={})
        p = als.plan(self.export, "visionos", self.work)
        self.assertEqual((p.min_os, p.device_family, p.bundle_id), ("26.0", "7", "org.universalexport.smoke"))
        make_export(self.tmp / "ios", "ios", settings={})
        p = als.plan(self.tmp / "ios", "ios", self.work)
        self.assertEqual((p.target, p.device_family), ("arm64-apple-ios16.0", "1,2"))


class PlanErrorTest(PlanCase):
    def test_no_device_slice(self):
        make_export(self.export, godot_slices=[("ios-arm64-simulator", "libgodot.a", "ios", "simulator")])
        with self.assertRaisesRegex(checks.CheckError, "device"):
            als.plan(self.export, "ios", self.work)

    def test_ios_export_for_visionos(self):
        make_export(self.export, "ios")
        with self.assertRaisesRegex(checks.CheckError, "xros"):
            als.plan(self.export, "visionos", self.work)

    def test_missing_dummy_cpp(self):
        make_export(self.export)
        (self.export / "Smoke" / "dummy.cpp").unlink()
        with self.assertRaisesRegex(checks.CheckError, "dummy.cpp"):
            als.plan(self.export, "ios", self.work)

    def test_swift_stub_without_header_or_pck(self):
        make_export(self.export, swift=False, header=False, pck=False)
        p = als.plan(self.export, "ios", self.work)
        swift = self.steps(p)["swiftc"][0]
        stub = self.work / "gen" / "dummy.swift"
        self.assertIn(stub, p.files)
        self.assertIn(str(stub), swift)
        self.assertNotIn("-import-objc-header", swift)
        self.assertEqual(p.copies, [])

    def test_empty_or_foreign_archive_rejected(self):
        for name, data, err in (("empty", b"!<arch>\n", "no members"),
                                ("no-symdef", b"!<arch>\n" + b"obj.o/          0           0     0     100644  2         `\nxx",
                                 "no symbol table"),
                                ("not-godot", static_archive((b"_other",)), "does not define"),
                                ("not-ar", b"\0" * 64, "not a static archive")):
            with self.subTest(name):
                root = self.tmp / name
                make_export(root)
                (root / "Smoke.xcframework" / "ios-arm64" / "libgodot.a").write_bytes(data)
                with self.assertRaisesRegex(checks.CheckError, err):
                    als.plan(root, "ios", self.work)

    def test_empty_moltenvk_rejected(self):
        make_export(self.export)
        (self.export / "MoltenVK.xcframework" / "ios-arm64" / "libMoltenVK.a").write_bytes(b"!<arch>\n")
        with self.assertRaisesRegex(checks.CheckError, "libMoltenVK.a: archive has no members"):
            als.plan(self.export, "ios", self.work)

    def test_plan_is_pure(self):
        make_export(self.export)
        als.plan(self.export, "ios", self.work)
        self.assertFalse(self.work.exists())


FAKE_XCRUN = """\
import os, struct, sys
from pathlib import Path
a = sys.argv[1:]
assert a[0] == "--sdk", a
tool, rest = a[2], a[3:]
after = lambda f: rest[rest.index(f) + 1]
if tool == "swiftc":
    Path("dummy.o").write_bytes(b"o")
    Path(after("-emit-module-path")).write_bytes(b"m")
elif tool == "clang":
    Path(after("-o")).write_bytes(b"o")
elif tool == "clang++":
    for obj in Path(after("-filelist")).read_text().split():
        assert Path(obj).is_file(), obj
    for i, arg in enumerate(rest):
        if arg == "-force_load":  # like ld64: a force-loaded archive must contribute members
            lib = Path(rest[i + 2])
            if lib.read_bytes().count(b"`\\n") < 2:
                sys.exit(f"ld: {lib}: archive has no object members")
    plat = int(os.environ.get("FAKE_PLATFORM") or (11 if "xros" in after("-target") else 2))
    cmds = struct.pack("<IIIIII", 0x32, 24, plat, 0x100000, 0x1A0000, 0)
    Path(after("-o")).write_bytes(struct.pack("<IiiIIIII", 0xFEEDFACF, 0x0100000C, 0, 2, 1, len(cmds), 0, 0) + cmds)
else:
    sys.exit("unexpected " + tool)
"""
FAKE_BUNDLE_UTIL = """\
import plistlib, sys
from pathlib import Path
a = sys.argv[1:]
assert a[0] == "info-plist", a
Path(a[a.index("--out") + 1]).write_bytes(plistlib.dumps({"CFBundleIdentifier": "x"}))
Path(a[a.index("--pkginfo") + 1]).write_text("APPL????")
"""
FAKE_RCODESIGN = """\
import os, sys
from pathlib import Path
cmd = sys.argv[1]
if cmd == "sign":
    (Path(sys.argv[-1]) / "_CodeSignature").mkdir()
    (Path(sys.argv[-1]) / "_CodeSignature" / "CodeResources").write_text("x")
elif cmd == "verify":
    print("signature has no CMS data", file=sys.stderr)
    sys.exit(1)
elif cmd == "print-signature-info":
    print("identifier: " + os.environ.get("FAKE_IDENT", "org.example.smoke"))
"""


class RunTest(PlanCase):
    def setUp(self):
        super().setUp()
        self.bin = self.tmp / "bin"
        self.bin.mkdir()
        for name, body in (("xcrun", FAKE_XCRUN), ("apple-bundle-util", FAKE_BUNDLE_UTIL),
                           ("rcodesign", FAKE_RCODESIGN)):
            path = self.bin / name
            path.write_text(f"#!{sys.executable}\n" + textwrap.dedent(body))
            path.chmod(path.stat().st_mode | stat.S_IXUSR)
        make_export(self.export)

    def cli(self, *extra, **env):
        report = self.tmp / "apple.json"
        r = subprocess.run([sys.executable, str(SMOKE / "apple_link_smoke.py"), "--platform", "ios",
                            "--export", str(self.export), "--work", str(self.work), "--json", str(report), *extra],
                           capture_output=True, text=True,
                           env=dict(os.environ, PATH=f"{self.bin}{os.pathsep}{os.environ['PATH']}", **env))
        return r, (json.loads(report.read_text()) if report.exists() else None)

    def test_pass_with_signature_info_fallback(self):
        r, rep = self.cli()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual((rep["status"], rep["verify"], rep["platform"]), ("pass", "print-signature-info", "ios"))
        self.assertEqual(rep["lc_build_version"]["platform"], 2)
        self.assertTrue((self.work / "Smoke.app" / "Smoke.pck").is_file())
        self.assertTrue((self.work / "link.log").is_file())
        self.assertRegex(rep["exe_sha256"], r"^[0-9a-f]{64}$")

    def test_wrong_platform_fails(self):
        r, rep = self.cli(FAKE_PLATFORM="11")
        self.assertEqual(r.returncode, 1)
        self.assertEqual(rep["status"], "fail")
        self.assertIn("platform", rep["detail"])

    def test_signature_identifier_mismatch_fails(self):
        r, rep = self.cli(FAKE_IDENT="com.other")
        self.assertEqual((r.returncode, rep["status"]), (1, "fail"))

    def test_empty_libgodot_fails(self):
        (self.export / "Smoke.xcframework" / "ios-arm64" / "libgodot.a").write_bytes(b"!<arch>\n")
        r, rep = self.cli()
        self.assertEqual((r.returncode, rep["status"]), (1, "fail"), r.stdout + r.stderr)
        self.assertIn("libgodot.a", rep["detail"])
        self.assertFalse((self.work / "Smoke.app" / "Smoke").exists())

    def test_link_fails_when_force_loaded_archive_is_emptied(self):
        p = als.plan(self.export, "ios", self.work)
        (self.export / "Smoke.xcframework" / "ios-arm64" / "libgodot.a").write_bytes(b"!<arch>\n")
        with mock.patch.dict(os.environ, PATH=f"{self.bin}{os.pathsep}{os.environ['PATH']}"):
            rep = als.execute(p)
        self.assertEqual(rep["status"], "fail")
        self.assertIn("link failed", rep["detail"])

    def test_usage(self):
        r = subprocess.run([sys.executable, str(SMOKE / "apple_link_smoke.py"), "--platform", "tvos"],
                           capture_output=True, text=True)
        self.assertEqual(r.returncode, 64)


if __name__ == "__main__":
    unittest.main()
