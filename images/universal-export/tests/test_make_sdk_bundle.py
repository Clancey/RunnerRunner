"""Tests for mac/make_sdk_bundle.sh, run against a synthetic Xcode.app tree.

The producer's output is admitted by a throwaway copy of apple-sdk-admit whose
lock gets the stanza the producer printed; nothing here touches a real Xcode.
"""
import io
import json
import os
import plistlib
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import _sdkpack as sp  # noqa: E402

SCRIPT = sp.UE / "mac" / "make_sdk_bundle.sh"
BUILD = "F9Z42"
PLATFORMS = {"XROS": ("xros27.0", "24M361"), "XRSimulator": ("xrsimulator27.0", "24M361"),
             "iPhoneOS": ("iphoneos27.1", "24A94232"), "iPhoneSimulator": ("iphonesimulator27.1", "24A94232")}
TC = "Developer/Toolchains/XcodeDefault.xctoolchain"
CLANG = f"{TC}/usr/lib/clang"
SWIFT = f"{TC}/usr/lib/swift"
RT = ("ios", "iossim", "xros", "xrossim")
SWIFT_DIRS = ("apinotes", "xros", "xrsimulator", "iphoneos", "iphonesimulator")
MODULEMAP = "usr/include/DarwinFoundation1.modulemap"


def sdk_dir(plat: str) -> str:
    return f"Developer/Platforms/{plat}.platform/Developer/SDKs/{plat}.sdk"


def write(path: Path, data: bytes, mode: int = 0o644) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    os.chmod(path, mode)


def make_fake_xcode(root: Path) -> Path:
    """A miniature Xcode.app with everything the pack takes, plus things it must leave out."""
    c = root / "Xcode.app" / "Contents"
    write(c / "version.plist", plistlib.dumps({"CFBundleShortVersionString": "27.1", "ProductBuildVersion": BUILD}))
    write(c / "Info.plist", b"<plist/>\n")
    write(c / "Developer/usr/bin/xcodebuild", b"#!/bin/sh\n", 0o755)
    for plat, (canon, build) in PLATFORMS.items():
        p = c / f"Developer/Platforms/{plat}.platform"
        write(p / "Info.plist", f"<plist>{plat}</plist>\n".encode())
        write(p / "version.plist", f"<plist>{plat} version</plist>\n".encode())
        write(p / "Developer/Library/Xcode/Templates/t.txt", b"not in the pack\n")
        write(p / "usr/bin/platform-tool", b"#!/bin/sh\n", 0o755)
        s = c / sdk_dir(plat)
        write(s / "SDKSettings.json", json.dumps({"CanonicalName": canon, "Version": canon[-4:]}).encode())
        write(s / "System/Library/CoreServices/SystemVersion.plist", plistlib.dumps({"ProductBuildVersion": build}))
        write(s / "usr/include/stdio.h", f"/* {plat} */ int printf(const char *, ...);\n".encode())
        write(s / MODULEMAP, sp.MODULEMAP_UNPATCHED)
        write(s / "usr/include/other.modulemap", b"module Other { }\n")
        write(s / "usr/lib/libz.1.tbd", b"--- !tapi-tbd\n")
        os.symlink("libz.1.tbd", s / "usr/lib/libz.tbd")
        write(s / "usr/bin/tool", b"#!/bin/sh\nexit 0\n", 0o755)
        write(s / "System/Library/Frameworks/Foundation.framework/prebuilt-modules/x.pcm", b"pcm\n")
        (s / "usr/share/empty").mkdir(parents=True)
        os.symlink(f"{plat}.sdk", s.parent / f"{plat}{canon[-4:]}.sdk")
        os.symlink("../../../../../Developer/usr", s.parent / "Unrelated.link")
    mac = c / "Developer/Platforms/MacOSX.platform"
    write(mac / "Info.plist", b"<plist>mac</plist>\n")
    write(mac / "Developer/SDKs/MacOSX.sdk/usr/include/stdio.h", b"/* mac */\n")
    t = c / TC
    for name in RT + ("osx",):
        write(t / f"usr/lib/clang/21/lib/darwin/libclang_rt.{name}.a", f"!<arch> {name}\n".encode())
    write(t / "usr/lib/clang/21/include/stddef.h", b"/* clang header */\n")
    os.symlink("21", t / "usr/lib/clang/21.0.0")
    write(t / "usr/bin/clang", b"#!/bin/sh\n", 0o755)
    for d in SWIFT_DIRS + ("macosx",):
        write(t / f"usr/lib/swift/{d}/Swift.swiftmodule/arm64.swiftinterface", f"// {d}\n".encode())
        if d != "apinotes":
            write(t / f"usr/lib/swift/{d}/prebuilt-modules/27.0/Swift.swiftmodule/arm64.swiftmodule", b"bin\n")
    write(t / "usr/lib/swift/apinotes/Foundation.apinotes", b"---\n")
    return root / "Xcode.app"


def snapshot(root: Path) -> dict:
    out = {}
    for dirpath, dirnames, filenames in os.walk(root):
        for name in dirnames + filenames:
            p = Path(dirpath, name)
            st = p.lstat()
            data = os.readlink(p) if p.is_symlink() else p.read_bytes() if p.is_file() else None
            out[str(p.relative_to(root))] = (stat.S_IFMT(st.st_mode), stat.S_IMODE(st.st_mode), st.st_mtime_ns, data)
    return out


class BundleCase(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name).resolve()
        self.xcode = make_fake_xcode(self.root / "apps")
        self.scratch = self.root / "scratch"
        self.scratch.mkdir()

    def tearDown(self):
        sp.make_writable(self.root)
        self._tmp.cleanup()

    def bundle(self, out: Path, *extra, env=None) -> subprocess.CompletedProcess:
        full = {k: v for k, v in os.environ.items() if k != "SOURCE_DATE_EPOCH"}
        full.update({"TMPDIR": str(self.scratch), **(env or {})})
        return subprocess.run(["bash", str(SCRIPT), "--xcode", str(self.xcode), "--out", str(out), *extra],
                              capture_output=True, text=True, env=full)

    def bundle_ok(self, out: Path, **kw):
        proc = self.bundle(out, **kw)
        self.assertEqual(proc.returncode, 0, proc.stderr)
        stanza = json.loads(proc.stdout)
        pack = out / f"apple-sdk-{BUILD}.tar.zst"
        return stanza, pack, proc

    def extract(self, pack: Path) -> Path:
        dest = self.root / "extracted"
        sp.extract_plain(pack, dest)
        return dest


class ScriptShape(unittest.TestCase):
    def test_executable_bash(self):
        self.assertTrue(os.access(SCRIPT, os.X_OK))
        self.assertEqual(SCRIPT.read_text().splitlines()[0], "#!/usr/bin/env bash")

    @unittest.skipUnless(shutil.which("shellcheck"), "shellcheck not installed")
    def test_shellcheck_clean(self):
        proc = subprocess.run(["shellcheck", "-x", str(SCRIPT)], capture_output=True, text=True,
                              cwd=SCRIPT.parent)
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)

    def test_usage_error(self):
        proc = subprocess.run(["bash", str(SCRIPT), "--bogus"], capture_output=True, text=True)
        self.assertEqual(proc.returncode, 64, proc.stderr)
        self.assertIn("usage", proc.stderr.lower())


class RoundTrip(BundleCase):
    def test_pack_admits_with_printed_stanza(self):
        out = self.root / "out"
        stanza, pack, _ = self.bundle_ok(out)
        self.assertEqual(list(stanza), [BUILD])
        pin = stanza[BUILD]
        self.assertEqual(pin["format"], 2)
        self.assertEqual(pin["patches_required"], [])
        self.assertEqual(pin["xcode_version"], "27.1")
        self.assertEqual(pin["clang_resource_version"], "21")
        self.assertEqual(pin["sdks"], {p: f"{c} {b}" for p, (c, b) in PLATFORMS.items()})
        data = pack.read_bytes()
        self.assertEqual(pin["tarball_sha256"], sp.sha(data))
        self.assertEqual(pin["tarball_size"], len(data))
        self.assertEqual((out / f"{pack.name}.sha256").read_text(), f"{sp.sha(data)}  {pack.name}\n")

        ue = self.root / "ue"
        tool = sp.install_tool(ue, stanza)
        dest = self.root / "opt" / "apple-sdk"
        dest.parent.mkdir()
        env = {"TMPDIR": str(self.scratch)}
        proc = sp.run_tool(tool, "admit", "--build", BUILD, "--pack", pack, "--dest", dest, env=env)
        self.assertEqual(proc.returncode, 0, proc.stderr)
        side = json.loads((dest.parent / ".apple-sdk-admission.json").read_text())
        self.assertEqual(side["format"], 2)
        self.assertEqual(side["patched"], [])
        self.assertEqual(side["tree_sha256"], pin["tree_sha256"])
        for cmd in ("verify", "status"):
            proc = sp.run_tool(tool, *([cmd, "--build", BUILD] if cmd == "verify" else [cmd]), "--dest", dest, env=env)
            self.assertEqual(proc.returncode, 0, f"{cmd}: {proc.stderr}")
        if os.geteuid() != 0:
            proc = sp.run_tool(tool, "verify-mounted", "--build", BUILD, "--dest", dest, env=env)
            self.assertEqual(proc.returncode, 0, proc.stderr)

    def test_counts_match_extracted_tree(self):
        stanza, pack, _ = self.bundle_ok(self.root / "out")
        pin = stanza[BUILD]
        tree = self.extract(pack)
        files = dirs = links = size = 0
        for dirpath, dirnames, filenames in os.walk(tree):
            for n in dirnames + filenames:
                p = Path(dirpath, n)
                if p.is_symlink():
                    links += 1
                elif p.is_dir():
                    dirs += 1
                else:
                    files += 1
                    size += p.stat().st_size
        self.assertEqual((pin["regular_files"], pin["directories"], pin["symlinks"], pin["unpacked_size"]),
                         (files, dirs + 1, links, size))
        self.assertEqual(pin["content_sha256"], sp.sha((tree / "MANIFEST.sha256").read_bytes()))
        self.assertEqual(pin["tree_sha256"], sp.sha((tree / "MANIFEST.tree").read_bytes()))


class Content(BundleCase):
    def setUp(self):
        super().setUp()
        self.before = snapshot(self.xcode)
        self.stanza, self.pack, self.proc = self.bundle_ok(self.root / "out")
        self.tree = self.extract(self.pack)
        self.rels = {str(p.relative_to(self.tree)) for p in self.tree.rglob("*")}

    def test_source_xcode_untouched(self):
        self.assertEqual(snapshot(self.xcode), self.before)

    def test_prebuilt_modules_dropped(self):
        self.assertEqual([r for r in self.rels if "prebuilt-modules" in r.split("/")], [])
        self.assertIn(f"{SWIFT}/iphoneos/Swift.swiftmodule/arm64.swiftinterface", self.rels)

    def test_subset_matches_format1_layout(self):
        want = {"version.plist", "apple-sdk.info", "MANIFEST.sha256", "MANIFEST.tree",
                f"{CLANG}/current", f"{SWIFT}/apinotes/Foundation.apinotes"}
        want |= {f"{CLANG}/21/lib/darwin/libclang_rt.{n}.a" for n in RT}
        want |= {f"{SWIFT}/{d}/Swift.swiftmodule/arm64.swiftinterface" for d in SWIFT_DIRS}
        for plat, (canon, _) in PLATFORMS.items():
            pdir = f"Developer/Platforms/{plat}.platform"
            want |= {f"{pdir}/Info.plist", f"{pdir}/version.plist", f"{sdk_dir(plat)}/usr/include/stdio.h",
                     f"{sdk_dir(plat)}/usr/lib/libz.tbd", f"{sdk_dir(plat)}/usr/bin/tool",
                     f"{pdir}/Developer/SDKs/{plat}{canon[-4:]}.sdk", f"{sdk_dir(plat)}/usr/share/empty"}
        self.assertEqual(want - self.rels, set())
        allowed = want | {"/".join(r.split("/")[:i]) for r in want for i in range(1, r.count("/") + 1)}
        trees = tuple(f"{sdk_dir(p)}/" for p in PLATFORMS) + tuple(f"{SWIFT}/{d}/" for d in SWIFT_DIRS)
        self.assertEqual(sorted(r for r in self.rels if r not in allowed and not r.startswith(trees)), [])
        self.assertEqual(os.readlink(self.tree / CLANG / "current"), "21")
        self.assertEqual(os.readlink(self.tree / "Developer/Platforms/XROS.platform/Developer/SDKs/XROS27.0.sdk"),
                         "XROS.sdk")
        self.assertTrue((self.tree / sdk_dir("XROS")).is_dir() and not (self.tree / sdk_dir("XROS")).is_symlink())

    def test_modulemap_prepatched(self):
        for plat in PLATFORMS:
            self.assertEqual((self.tree / sdk_dir(plat) / MODULEMAP).read_bytes(), sp.MODULEMAP_PATCHED)
            self.assertEqual((self.xcode / "Contents" / sdk_dir(plat) / MODULEMAP).read_bytes(),
                             sp.MODULEMAP_UNPATCHED)
        manifest = (self.tree / "MANIFEST.sha256").read_text()
        self.assertIn(f"{sp.sha(sp.MODULEMAP_PATCHED)}  ./{sdk_dir('XROS')}/{MODULEMAP}\n", manifest)

    def test_manifests_sorted_and_complete(self):
        lines = (self.tree / "MANIFEST.sha256").read_text().splitlines()
        paths = [ln.split("  ./", 1)[1] for ln in lines]
        self.assertEqual(paths, sorted(paths))
        files = {r for r in self.rels if (self.tree / r).is_file() and not (self.tree / r).is_symlink()}
        self.assertEqual(set(paths), files - {"MANIFEST.sha256", "MANIFEST.tree"})
        for ln in lines:
            h, rel = ln.split("  ./", 1)
            self.assertEqual(h, sp.sha((self.tree / rel).read_bytes()), rel)
        entries = {r: (("l", os.readlink(self.tree / r)) if (self.tree / r).is_symlink() else
                       ("d",) if (self.tree / r).is_dir() else
                       ("f", b"", 0o755 if os.access(self.tree / r, os.X_OK) else 0o644))
                   for r in self.rels if r != "MANIFEST.tree"}
        self.assertEqual((self.tree / "MANIFEST.tree").read_text().splitlines(), sp.tree_lines(entries))

    def test_info_file(self):
        info = (self.tree / "apple-sdk.info").read_text().splitlines()
        keys = [ln.split("=", 1)[0] for ln in info]
        self.assertEqual(keys[:5], ["XCODE_VERSION", "XCODE_BUILD", "CLANG_RESOURCE_VERSION",
                                    "BUILD_MACHINE_OS_BUILD", "PACKED_AT"])
        kv = dict(ln.split("=", 1) for ln in info)
        self.assertEqual((kv["XCODE_VERSION"], kv["XCODE_BUILD"], kv["CLANG_RESOURCE_VERSION"]), ("27.1", BUILD, "21"))
        self.assertEqual(kv["FORMAT"], "2")
        for plat, (canon, build) in PLATFORMS.items():
            self.assertEqual(kv[f"SDK_{plat}"], f"{canon} {build}")
        self.assertRegex(kv["PACKED_AT"], r"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$")

    def test_tar_members_normalised(self):
        with self._open() as tf:
            members = tf.getmembers()
        self.assertIn(members[0].name, (".", "./"))
        self.assertTrue(members[0].isdir())
        names = [m.name for m in members[1:]]
        self.assertTrue(all(n.startswith("./") for n in names))
        self.assertEqual(names, sorted(names))
        self.assertEqual({(m.uid, m.gid, m.uname, m.gname) for m in members}, {(0, 0, "root", "root")})
        self.assertEqual(len({m.mtime for m in members}), 1)
        for m in members:
            if m.isdir():
                self.assertEqual(m.mode, 0o755, m.name)
            elif m.isfile():
                self.assertIn(m.mode, (0o644, 0o755), m.name)
        self.assertEqual(next(m for m in members if m.name.endswith("usr/bin/tool")).mode, 0o755)

    def _open(self):
        data = subprocess.run(["zstd", "-dcq", str(self.pack)], capture_output=True, check=True).stdout
        return tarfile.open(fileobj=io.BytesIO(data), mode="r:")

    def test_no_stage_left_behind(self):
        out = self.root / "out"
        self.assertEqual(sorted(p.name for p in out.iterdir()),
                         [f"apple-sdk-{BUILD}.tar.zst", f"apple-sdk-{BUILD}.tar.zst.sha256"])


class Behaviour(BundleCase):
    def test_deterministic_across_runs_and_cp_fallback(self):
        _, a, _ = self.bundle_ok(self.root / "a")
        _, b, _ = self.bundle_ok(self.root / "b")
        fake = self.root / "fakebin"
        fake.mkdir()
        write(fake / "cp", b'#!/bin/sh\nfor a in "$@"; do [ "$a" = -c ] && { echo "cp: illegal option -- c" >&2; '
                           b'exit 64; }; done\nexec /bin/cp "$@"\n', 0o755)
        _, c, proc = self.bundle_ok(self.root / "c", env={"PATH": f"{fake}:{os.environ['PATH']}"})
        self.assertRegex(proc.stderr, r"(?i)fall(ing)? ?back")
        self.assertEqual({sp.sha(p.read_bytes()) for p in (a, b, c)}, {sp.sha(a.read_bytes())})

    def test_source_date_epoch(self):
        _, pack, _ = self.bundle_ok(self.root / "out", env={"SOURCE_DATE_EPOCH": "1700000000"})
        tree = self.extract(pack)
        self.assertIn("PACKED_AT=2023-11-14T22:13:20Z\n", (tree / "apple-sdk.info").read_text())
        self.assertEqual(int((tree / "version.plist").stat().st_mtime), 1700000000)

    def test_refuses_to_overwrite(self):
        out = self.root / "out"
        _, pack, _ = self.bundle_ok(out)
        before = (pack.stat().st_ino, pack.stat().st_mtime_ns, pack.read_bytes())
        proc = self.bundle(out)
        self.assertNotEqual(proc.returncode, 0)
        self.assertIn("exists", proc.stderr)
        self.assertEqual(proc.stdout, "")
        self.assertEqual((pack.stat().st_ino, pack.stat().st_mtime_ns, pack.read_bytes()), before)
        pack.unlink()
        proc = self.bundle(out)
        self.assertNotEqual(proc.returncode, 0, "a stray .sha256 must also block")
        self.assertFalse(pack.exists())

    def assertFailsClean(self, pattern):
        out = self.root / "out"
        proc = self.bundle(out)
        self.assertNotEqual(proc.returncode, 0)
        self.assertRegex(proc.stderr, pattern)
        self.assertEqual(proc.stdout, "")
        self.assertEqual(list(out.iterdir()) if out.exists() else [], [])
        self.assertEqual(list(self.scratch.iterdir()), [])

    def test_missing_platform_fails_clean(self):
        shutil.rmtree(self.xcode / "Contents" / sdk_dir("XRSimulator"))
        self.assertFailsClean(r"XRSimulator")

    def test_sdk_symlink_rejected(self):
        s = self.xcode / "Contents" / sdk_dir("iPhoneOS")
        s.rename(s.with_name("iPhoneOS.real.sdk"))
        os.symlink("iPhoneOS.real.sdk", s)
        self.assertFailsClean(r"iPhoneOS")

    def test_ambiguous_clang_version_fails(self):
        (self.xcode / "Contents" / CLANG / "22").mkdir()
        self.assertFailsClean(r"clang")

    def test_missing_runtime_fails(self):
        (self.xcode / "Contents" / CLANG / "21/lib/darwin/libclang_rt.xrossim.a").unlink()
        self.assertFailsClean(r"libclang_rt\.xrossim\.a")

    def test_not_an_xcode(self):
        (self.xcode / "Contents" / "version.plist").unlink()
        self.assertFailsClean(r"version\.plist")


if __name__ == "__main__":
    unittest.main()
