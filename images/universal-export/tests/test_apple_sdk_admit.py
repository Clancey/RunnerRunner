"""Tests for bin/apple-sdk-admit (run against a throwaway copy with synthetic pins)."""
import base64
import contextlib
import importlib.machinery
import importlib.util
import io
import json
import os
import re
import stat
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import _sdkpack as sp  # noqa: E402

BUILD = "T1"
STDIO = f"{sp.INCLUDE}/stdio.h"
VERSIONED = f"{sp.SDKS}/XROS99.0.sdk"
TOOLBIN = f"{sp.SDKS}/XROS.sdk/usr/bin/tool"


class AdmitCase(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name).resolve()
        self.packs = self.root / "packs"
        self.opt = self.root / "opt"
        self.scratch = self.root / "scratch"
        for d in (self.packs, self.opt, self.scratch):
            d.mkdir()
        self.dest = self.opt / "apple-sdk"
        self.sidecar = self.opt / ".apple-sdk-admission.json"
        self.ue = self.root / "ue"
        self.tool = None

    def tearDown(self):
        sp.make_writable(self.root)
        self._tmp.cleanup()

    def pack(self, name="good", **kw):
        return sp.build_pack(self.packs / f"{name}.tar.zst", **kw)

    def install(self, packs):
        if self.tool is None:
            self.tool = sp.install_tool(self.ue, packs)
        else:
            sp.write_lock(self.ue, packs)

    def run_tool(self, *args, env=None):
        return sp.run_tool(self.tool, *args, env={"TMPDIR": str(self.scratch), **(env or {})})

    def admit(self, pack, build=BUILD, dest=None, env=None):
        path = pack.path if isinstance(pack, sp.Pack) else pack
        return self.run_tool("admit", "--build", build, "--pack", path, "--dest", dest or self.dest, env=env)

    def admit_ok(self, fmt=1, **kw):
        pack = self.pack(fmt=fmt, **kw)
        self.install({BUILD: pack.pin()})
        self.assertOk(self.admit(pack))
        return pack

    def assertOk(self, proc):
        self.assertEqual(proc.returncode, 0, proc.stderr + proc.stdout)

    def assertFailed(self, proc, pattern):
        self.assertNotEqual(proc.returncode, 0, proc.stdout)
        self.assertRegex(proc.stderr, pattern)

    def assertRejected(self, proc, pattern):
        self.assertFailed(proc, pattern)
        self.assertClean()

    def assertClean(self):
        self.assertEqual([p.name for p in self.opt.iterdir() if ".stage-" in p.name], [])
        self.assertFalse(self.dest.exists() or self.dest.is_symlink(), "dest must stay absent")
        self.assertFalse(self.sidecar.exists(), "no sidecar after a failure")
        self.assertEqual(list(self.scratch.iterdir()), [], "private pack copy must be removed")

    def reject_pack(self, pattern, pin_over=None, **packkw):
        pack = self.pack(**packkw)
        self.install({BUILD: pack.pin(**(pin_over or {}))})
        self.assertRejected(self.admit(pack), pattern)

    def sidecar_json(self, dest=None):
        dest = dest or self.dest
        return json.loads((dest.parent / f".{dest.name}-admission.json").read_text())


class ToolShape(unittest.TestCase):
    def test_executable_with_env_shebang(self):
        self.assertTrue(os.access(sp.TOOL, os.X_OK))
        self.assertEqual(sp.TOOL.read_text().splitlines()[0], "#!/usr/bin/env python3")


class PinTests(AdmitCase):
    def test_unknown_build(self):
        pack = self.pack()
        self.install({BUILD: pack.pin()})
        self.assertRejected(self.admit(pack, build="NOPE9"), r"unknown")

    def test_tarball_sha_mismatch(self):
        self.reject_pack(r"sha256 mismatch", {"tarball_sha256": "0" * 64})

    def test_garbage_rejected_before_extraction(self):
        bad = self.packs / "garbage.tar.zst"
        bad.write_bytes(b"\x00not zstd" * 512)
        good = self.pack()
        self.install({BUILD: good.pin(tarball_size=bad.stat().st_size)})
        proc = self.admit(bad)
        self.assertRejected(proc, r"sha256 mismatch")
        self.assertNotRegex(proc.stderr, r"(?i)extract|zstd|tar stream")

    def test_size_mismatch(self):
        pack = self.pack()
        for size in (pack.tarball_size + 1, pack.tarball_size - 1):
            with self.subTest(size=size):
                self.install({BUILD: pack.pin(tarball_size=size)})
                self.assertRejected(self.admit(pack), r"size")

    def test_content_sha_mismatch(self):
        self.reject_pack(r"MANIFEST\.sha256", {"content_sha256": "1" * 64})

    def test_count_mismatch(self):
        pack = self.pack()
        for key in ("regular_files", "directories", "symlinks", "unpacked_size"):
            with self.subTest(key=key):
                self.install({BUILD: pack.pin(**{key: pack.stats[key] + 1})})
                self.assertRejected(self.admit(pack), key)

    def test_malformed_pins_fail_closed(self):
        pack = self.pack()
        for over, pat in (({"format": 3}, r"format"),
                          ({"tarball_sha256": "XYZ"}, r"tarball_sha256"),
                          ({"format": 2, "tree_sha256": "2" * 64}, r"patches_required"),
                          ({"format": 2, "patches_required": []}, r"tree_sha256"),
                          ({"patches_required": ["no-such-patch"]}, r"no-such-patch"),
                          ({"admitted_tree_sha256": None}, r"admitted_tree_sha256")):
            with self.subTest(over=over):
                self.install({BUILD: pack.pin(**over)})
                self.assertRejected(self.admit(pack), pat)

    def test_format1_admitted_tree_digest_pinned(self):
        self.reject_pack(r"tree structure mismatch", {"admitted_tree_sha256": "4" * 64})

    def test_env_vars_never_supply_pins(self):
        good = self.pack("good")
        bad = self.pack("bad", tree={**sp.base_tree(), "evil.h": ("f", b"evil", 0o644)})
        self.install({BUILD: good.pin()})
        decoy = self.root / "decoy-lock.json"
        decoy.write_text(json.dumps({"apple_sdk": {"known_packs": {BUILD: bad.pin()}}}))
        env = {
            "APPLE_SDK_TARBALL_SHA256": bad.tarball_sha256,
            "APPLE_SDK_TARBALL_SIZE": str(bad.tarball_size),
            "APPLE_SDK_CONTENT_SHA256": bad.content_sha256,
            "APPLE_SDK_TREE_SHA256": "0" * 64,
            "APPLE_SDK_LOCK": str(decoy),
            "UE_HOME": str(self.root),
            "TOOLCHAIN_LOCK": str(decoy),
        }
        self.assertRejected(self.admit(bad, env=env), r"(sha256|size) mismatch")
        self.assertOk(self.admit(good, env=env))

    def test_no_lock_override_flag(self):
        pack = self.pack()
        self.install({BUILD: pack.pin()})
        proc = self.run_tool("admit", "--build", BUILD, "--pack", pack.path, "--dest", self.dest,
                             "--lock", self.ue / "toolchain.lock.json")
        self.assertEqual(proc.returncode, 2)
        self.assertClean()

    def test_lock_resolved_from_script_realpath(self):
        good = self.pack("good")
        bad = self.pack("bad", tree={**sp.base_tree(), "evil.h": ("f", b"evil", 0o644)})
        self.install({BUILD: good.pin()})
        elsewhere = self.root / "elsewhere"
        (elsewhere / "bin").mkdir(parents=True)
        sp.write_lock(elsewhere, {BUILD: bad.pin()})
        link = elsewhere / "bin" / "apple-sdk-admit"
        link.symlink_to(self.tool)
        self.tool = link
        self.assertRejected(self.admit(bad), r"(sha256|size) mismatch")
        self.assertOk(self.admit(good))


class MemberTests(AdmitCase):
    def test_absolute_member(self):
        self.reject_pack(r"absolute", extra=[sp.raw("/abs.h")])

    def test_dotdot_member(self):
        self.reject_pack(r"\.\.", extra=[sp.raw("./Developer/../../evil.h")])
        self.assertFalse((self.root / "evil.h").exists())

    def test_hardlink_member(self):
        self.reject_pack(r"hardlink", extra=[sp.raw("./Developer/hl", "h", linkname="./version.plist")])

    def test_device_and_fifo_members(self):
        for kind, pat in (("c", r"device"), ("b", r"device"), ("p", r"fifo")):
            with self.subTest(kind=kind):
                self.reject_pack(pat, extra=[sp.raw("./Developer/node", kind)])

    def test_duplicate_member(self):
        for name in ("./version.plist", "version.plist"):
            with self.subTest(name=name):
                self.reject_pack(r"duplicate", extra=[sp.raw(name, data=b"evil")])

    def test_duplicate_root(self):
        self.reject_pack(r"duplicate", extra=[sp.raw("./", "d", mode=0o755)])

    def test_member_through_symlink_parent(self):
        tree = {**sp.base_tree(), "Developer/lnk": ("l", "Platforms")}
        self.reject_pack(r"not a directory", tree=tree, extra=[sp.raw("./Developer/lnk/evil.h")])

    def test_symlink_escape_direct(self):
        self.reject_pack(r"escapes", tree={**sp.base_tree(), "Developer/esc": ("l", "../../outside")})

    def test_symlink_escape_via_chain(self):
        # Each link looks inside under naive normalisation; only following the chain escapes.
        tree = {**sp.base_tree(), "Developer/x/y/up": ("l", ".."), "Developer/esc": ("l", "x/y/up/../../..")}
        self.reject_pack(r"escapes", tree=tree)

    def test_symlink_escape_multi_hop(self):
        tree = {**sp.base_tree(), "l1": ("l", "Developer/l2"), "Developer/l2": ("l", "l3"),
                "Developer/l3": ("l", "../..")}
        self.reject_pack(r"escapes", tree=tree)

    def test_symlink_loop(self):
        tree = {**sp.base_tree(), "Developer/a": ("l", "b"), "Developer/b": ("l", "a")}
        self.reject_pack(r"loop", tree=tree)

    def test_absolute_symlink_target(self):
        self.reject_pack(r"absolute", tree={**sp.base_tree(), "Developer/abs": ("l", "/etc/passwd")})

    def test_inside_symlinks_accepted(self):
        tree = {**sp.base_tree(), "Developer/x/y/up": ("l", ".."), "Developer/top": ("l", "x/y/up/../.."),
                "Developer/dangling": ("l", "nope")}
        self.admit_ok(tree=tree)
        self.assertEqual(os.readlink(self.dest / "Developer/top"), "x/y/up/../..")

    def test_modes_normalised_and_special_bits_dropped(self):
        tree = {**sp.base_tree(), "Developer/suid": ("f", b"x", 0o4755), "Developer/sgid": ("f", b"y", 0o2664),
                "Developer/wide": ("f", b"z", 0o777)}
        self.admit_ok(tree=tree, extra=[sp.raw("./Developer/sticky", "d", mode=0o1777)])
        modes = sp.kinds(self.dest)
        self.assertEqual(modes["Developer/suid"], ("-", 0o555))
        self.assertEqual(modes["Developer/sgid"], ("-", 0o444))
        self.assertEqual(modes["Developer/wide"], ("-", 0o555))
        self.assertEqual(modes["Developer/sticky"], ("d", 0o555))
        self.assertEqual(modes[TOOLBIN], ("-", 0o555))
        self.assertEqual(modes[STDIO], ("-", 0o444))
        for rel, (kind, mode) in modes.items():
            if kind != "l":
                self.assertIn(mode, (0o444, 0o555), rel)
                self.assertEqual((self.dest / rel).lstat().st_uid, os.getuid())
        self.assertEqual(stat.S_IMODE(self.dest.stat().st_mode), 0o555)


class ManifestTests(AdmitCase):
    def manifest(self, tree=None):
        return sp.manifest_for(sp.with_dirs(tree or sp.base_tree()))

    def test_file_hash_mismatch(self):
        m = self.manifest()
        m[STDIO] = sp.sha(b"something else")
        self.reject_pack(r"stdio\.h", manifest=m)

    def test_extra_unlisted_file(self):
        self.reject_pack(r"extra\.h", unlisted={"Developer/extra.h": ("f", b"x", 0o644)})

    def test_missing_listed_file(self):
        m = self.manifest()
        m["Developer/missing.h"] = sp.sha(b"x")
        self.reject_pack(r"missing\.h", manifest=m)

    def test_missing_manifest(self):
        self.reject_pack(r"MANIFEST\.sha256", omit_manifest=True)

    def test_format1_must_not_carry_unlisted_tree_manifest(self):
        self.reject_pack(r"MANIFEST\.tree", unlisted={"MANIFEST.tree": ("f", b"d\t./Developer\n", 0o644)})


class Format1PatchTests(AdmitCase):
    def test_patch_applied_after_verification_and_recorded(self):
        pack = self.admit_ok(fmt=1)
        self.assertEqual((self.dest / sp.MODULEMAP).read_bytes(), sp.MODULEMAP_PATCHED)
        side = self.sidecar_json()
        self.assertEqual(side["build"], BUILD)
        self.assertEqual(side["format"], 1)
        self.assertEqual(side["tarball_sha256"], pack.tarball_sha256)
        self.assertEqual(side["content_sha256"], pack.content_sha256)
        self.assertIsNone(side["tree_sha256"])
        self.assertTrue(side["admitted_at"])
        self.assertEqual(side["patched"], [{"path": sp.MODULEMAP,
                                            "before_sha256": sp.sha(sp.MODULEMAP_UNPATCHED),
                                            "after_sha256": sp.sha(sp.MODULEMAP_PATCHED),
                                            "original_b64": base64.b64encode(sp.MODULEMAP_UNPATCHED).decode()}])
        self.assertOk(self.run_tool("verify", "--build", BUILD, "--dest", self.dest))

    def test_manifest_checked_against_unpatched_content(self):
        m = sp.manifest_for(sp.with_dirs(sp.base_tree()))
        m[sp.MODULEMAP] = sp.sha(sp.MODULEMAP_PATCHED)
        self.reject_pack(r"DarwinFoundation1\.modulemap", manifest=m)

    def test_admit_prints_env_lines(self):
        pack = self.pack()
        self.install({BUILD: pack.pin()})
        proc = self.admit(pack)
        self.assertOk(proc)
        self.assertIn(f"APPLE_SDK={self.dest}", proc.stdout.splitlines())
        self.assertIn(f"APPLE_SDK_BUILD={BUILD}", proc.stdout.splitlines())


class PatchLibTests(unittest.TestCase):
    def load(self):
        spec = importlib.util.spec_from_file_location("apple_sdk_patches_under_test", sp.PATCHES)
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        return mod

    def test_apply_is_idempotent(self):
        lib = self.load()
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            mm = root / "a" / "module.modulemap"
            mm.parent.mkdir()
            mm.write_bytes(sp.MODULEMAP_UNPATCHED)
            (root / "a" / "keep.h").write_bytes(sp.OBSOLETE)
            (root / "a" / "link.modulemap").symlink_to("module.modulemap")
            first = lib.apply(root, ["modulemap-obsolete-v1"])
            self.assertEqual(first, [{"path": "a/module.modulemap",
                                      "before_sha256": sp.sha(sp.MODULEMAP_UNPATCHED),
                                      "after_sha256": sp.sha(sp.MODULEMAP_PATCHED)}])
            self.assertEqual(mm.read_bytes(), sp.MODULEMAP_PATCHED)
            self.assertEqual((root / "a" / "keep.h").read_bytes(), sp.OBSOLETE)
            self.assertTrue((root / "a" / "link.modulemap").is_symlink())
            self.assertEqual(lib.apply(root, ["modulemap-obsolete-v1"]), [])
            self.assertEqual(mm.read_bytes(), sp.MODULEMAP_PATCHED)

    def test_unknown_patch_rejected(self):
        with self.assertRaises(KeyError):
            self.load().apply(Path("."), ["nope"])


class Format2Tests(AdmitCase):
    def edit(self, old, new):
        return lambda lines: sorted(new if line == old else line for line in lines)

    def test_format2_admitted_without_patch_step(self):
        pack = self.admit_ok(fmt=2, tree=sp.base_tree(patched=False))
        self.assertEqual((self.dest / sp.MODULEMAP).read_bytes(), sp.MODULEMAP_UNPATCHED)
        side = self.sidecar_json()
        self.assertEqual((side["format"], side["tree_sha256"], side["patched"]), (2, pack.tree_sha256, []))
        self.assertOk(self.run_tool("verify", "--build", BUILD, "--dest", self.dest))

    def test_tree_extra_entry(self):
        self.reject_pack(r"MANIFEST\.tree", fmt=2, mtree=lambda ls: sorted(ls + ["d\t./Developer/ghost"]))

    def test_tree_missing_entry(self):
        self.reject_pack(r"MANIFEST\.tree", fmt=2, mtree=lambda ls: [x for x in ls if not x.endswith("stdio.h")])

    def test_tree_wrong_symlink_target(self):
        self.reject_pack(r"MANIFEST\.tree", fmt=2,
                         mtree=self.edit(f"l\t./{VERSIONED}\tXROS.sdk", f"l\t./{VERSIONED}\tXROS.sdk/usr"))

    def test_tree_wrong_exec_bit(self):
        self.reject_pack(r"MANIFEST\.tree", fmt=2, mtree=self.edit(f"x\t./{TOOLBIN}", f"f\t./{TOOLBIN}"))

    def test_tree_sha_mismatch(self):
        self.reject_pack(r"MANIFEST\.tree", {"tree_sha256": "2" * 64}, fmt=2)

    def test_tree_manifest_required(self):
        pack = self.pack(fmt=1, tree=sp.base_tree(patched=True))
        self.install({BUILD: pack.pin(format=2, tree_sha256="3" * 64, patches_required=[])})
        self.assertRejected(self.admit(pack), r"MANIFEST\.tree")


class DestTests(AdmitCase):
    def load_tool(self):
        loader = importlib.machinery.SourceFileLoader("apple_sdk_admit_under_test", str(self.tool))
        spec = importlib.util.spec_from_loader(loader.name, loader)
        mod = importlib.util.module_from_spec(spec)
        loader.exec_module(mod)
        return mod

    def test_dest_created_by_single_rename_of_stage(self):
        pack = self.pack()
        self.install({BUILD: pack.pin()})
        mod = self.load_tool()
        with mock.patch("os.rename", wraps=os.rename) as ren, \
                contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            rc = mod.main(["admit", "--build", BUILD, "--pack", str(pack.path), "--dest", str(self.dest)])
        self.assertEqual(rc, 0)
        ren.assert_called_once()
        src, dst = (Path(a) for a in ren.call_args.args)
        self.assertEqual(src.parent, self.opt)
        self.assertRegex(src.name, r"^\.apple-sdk\.stage-\d+$")
        self.assertEqual(dst, self.dest)
        self.assertFalse(src.exists())
        self.assertTrue(self.dest.is_dir())

    def test_readmit_same_build_is_noop(self):
        pack = self.admit_ok()
        before = (self.sidecar.read_bytes(), self.dest.stat().st_ino, (self.dest / STDIO).stat().st_ino)
        proc = self.admit(pack)
        self.assertOk(proc)
        self.assertRegex(proc.stderr, r"already admitted")
        self.assertEqual(before, (self.sidecar.read_bytes(), self.dest.stat().st_ino,
                                  (self.dest / STDIO).stat().st_ino))
        self.assertEqual([p.name for p in self.opt.iterdir() if ".stage-" in p.name], [])

    def test_dest_with_other_admission_fails_untouched(self):
        a = self.pack("a")
        b = self.pack("b", tree={**sp.base_tree(), "other.h": ("f", b"b", 0o644)})
        self.install({BUILD: a.pin(), "T2": b.pin()})
        self.assertOk(self.admit(a))
        before = (self.sidecar.read_bytes(), self.dest.stat().st_ino, sorted(sp.kinds(self.dest).items()))
        self.assertFailed(self.admit(b, build="T2"), r"already holds|different")
        self.assertEqual(before, (self.sidecar.read_bytes(), self.dest.stat().st_ino,
                                  sorted(sp.kinds(self.dest).items())))
        self.assertEqual([p.name for p in self.opt.iterdir() if ".stage-" in p.name], [])

    def test_dest_with_changed_pin_fails(self):
        a = self.admit_ok()
        self.install({BUILD: a.pin(content_sha256="4" * 64)})
        self.assertFailed(self.admit(a), r"already holds|different")

    def test_unknown_dest_fails_untouched(self):
        pack = self.pack()
        self.install({BUILD: pack.pin()})
        self.dest.mkdir()
        (self.dest / "junk").write_text("x")
        self.assertFailed(self.admit(pack), r"not an admission|already holds")
        self.assertEqual((self.dest / "junk").read_text(), "x")
        self.assertFalse(self.sidecar.exists())
        self.assertEqual([p.name for p in self.opt.iterdir() if ".stage-" in p.name], [])

    def test_symlink_dest_fails(self):
        pack = self.pack()
        self.install({BUILD: pack.pin()})
        target = self.root / "elsewhere"
        target.mkdir()
        self.dest.symlink_to(target)
        self.assertFailed(self.admit(pack), r"symlink")
        self.assertEqual(list(target.iterdir()), [])

    def test_empty_dest_dir_is_replaced(self):
        pack = self.pack()
        self.install({BUILD: pack.pin()})
        self.dest.mkdir()
        self.assertOk(self.admit(pack))
        self.assertTrue((self.dest / "MANIFEST.sha256").is_file())


class VerifyTests(AdmitCase):
    TAMPERS = {
        "content": lambda d: (d / STDIO).write_bytes(b"int evil;\n"),
        "extra file": lambda d: (d / "Developer" / "new.h").write_bytes(b""),
        "extra dir": lambda d: (d / "Developer" / "newdir").mkdir(),
        "removed file": lambda d: (d / "version.plist").unlink(),
        "retargeted symlink": lambda d: ((d / VERSIONED).unlink(), (d / VERSIONED).symlink_to("XROS.sdk/usr")),
        "exec bit": lambda d: os.chmod(d / STDIO, 0o755),
        "setuid": lambda d: os.chmod(d / TOOLBIN, 0o4755),
    }

    def test_verify_detects_post_admission_tamper(self):
        pack = self.pack()
        self.install({BUILD: pack.pin()})
        tampers = {**self.TAMPERS, "unpatched": lambda d: (d / sp.MODULEMAP).write_bytes(sp.MODULEMAP_UNPATCHED)}
        for name, tamper in tampers.items():
            with self.subTest(tamper=name):
                dest = self.opt / f"sdk-{len(list(self.opt.iterdir()))}"
                self.assertOk(self.admit(pack, dest=dest))
                self.assertOk(self.run_tool("verify", "--build", BUILD, "--dest", dest))
                sp.make_writable(dest)
                self.assertOk(self.run_tool("verify", "--build", BUILD, "--dest", dest))
                tamper(dest)
                self.assertFailed(self.run_tool("verify", "--build", BUILD, "--dest", dest), r".")

    def rewrite_sidecar(self, dest, edit):
        side = dest.parent / f".{dest.name}-admission.json"
        rec = json.loads(side.read_text())
        edit(rec)
        side.write_text(json.dumps(rec))

    def test_readmit_after_file_tamper_fails(self):
        pack = self.admit_ok()
        sp.make_writable(self.dest)
        for name, tamper in self.TAMPERS.items():
            with self.subTest(tamper=name):
                dest = self.opt / f"sdk-{name.replace(' ', '-')}"
                self.assertOk(self.admit(pack, dest=dest))
                sp.make_writable(dest)
                tamper(dest)
                self.assertFailed(self.admit(pack, dest=dest), r"fails verification")
                self.assertFailed(self.run_tool("verify", "--build", BUILD, "--dest", dest), r".")

    def test_readmit_after_file_and_sidecar_tamper_fails(self):
        pack = self.pack()
        self.install({BUILD: pack.pin()})
        evil = sp.MODULEMAP_UNPATCHED + b"module evil {}\n"

        def forged_after(d):
            (d / sp.MODULEMAP).write_bytes(evil)
            self.rewrite_sidecar(d, lambda r: r["patched"][0].update(after_sha256=sp.sha(evil)))

        def forged_original(d):
            (d / sp.MODULEMAP).write_bytes(evil)
            self.rewrite_sidecar(d, lambda r: r["patched"][0].update(
                before_sha256=sp.sha(evil), after_sha256=sp.sha(evil),
                original_b64=base64.b64encode(evil).decode()))

        def dropped_record(d):
            (d / sp.MODULEMAP).write_bytes(sp.MODULEMAP_UNPATCHED)
            self.rewrite_sidecar(d, lambda r: r.update(patched=[]))

        def forged_tree(d):
            (d / STDIO).write_bytes(b"int evil;\n")
            self.rewrite_sidecar(d, lambda r: r["patched"].append(
                {"path": STDIO, "before_sha256": sp.sha(b"x"), "after_sha256": sp.sha(b"int evil;\n"),
                 "original_b64": base64.b64encode(b"int evil;\n").decode()}))

        for name, tamper in (("forged after", forged_after), ("forged original", forged_original),
                             ("dropped record", dropped_record), ("forged extra record", forged_tree)):
            with self.subTest(tamper=name):
                dest = self.opt / f"sdk-{name.replace(' ', '-')}"
                self.assertOk(self.admit(pack, dest=dest))
                sp.make_writable(dest)
                tamper(dest)
                self.assertFailed(self.admit(pack, dest=dest), r"fails verification")
                self.assertFailed(self.run_tool("verify", "--build", BUILD, "--dest", dest), r".")

    @staticmethod
    def listing_digest(root):
        """The admitted tree listing of an on-disk tree, re-implemented from the contract."""
        lines = []
        for dirpath, dirnames, filenames in os.walk(root):
            for name in dirnames + filenames:
                path = os.path.join(dirpath, name)
                rel = os.path.relpath(path, root)
                st = os.lstat(path)
                if os.path.islink(path):
                    lines.append((rel, f"l\t./{rel}\t{os.readlink(path)}"))
                elif os.path.isdir(path):
                    lines.append((rel, f"d\t./{rel}"))
                else:
                    lines.append((rel, f"{'x' if st.st_mode & 0o111 else 'f'}\t./{rel}"))
        return sp.sha("".join(line + "\n" for _, line in sorted(lines)).encode())

    def test_format1_structure_tamper_with_forged_sidecar_digest_fails(self):
        pack = self.pack()
        self.install({BUILD: pack.pin()})
        structural = {
            "extra dir": self.TAMPERS["extra dir"],
            "retargeted symlink": self.TAMPERS["retargeted symlink"],
            "exec bit": self.TAMPERS["exec bit"],
            "removed empty dir": lambda d: (d / sp.SDKS / "XROS.sdk/usr/share/empty").rmdir(),
            "dir symlink swapped for dir": lambda d: ((d / VERSIONED).unlink(), (d / VERSIONED).mkdir()),
        }
        for name, tamper in structural.items():
            with self.subTest(tamper=name):
                dest = self.opt / f"sdk-{name.replace(' ', '-')}"
                self.assertOk(self.admit(pack, dest=dest))
                self.assertEqual(self.listing_digest(dest), pack.admitted_tree_sha256)
                sp.make_writable(dest)
                tamper(dest)
                forged = self.listing_digest(dest)
                self.assertNotEqual(forged, pack.admitted_tree_sha256)
                self.rewrite_sidecar(dest, lambda r: r.update(admitted_tree_sha256=forged))
                self.assertFailed(self.run_tool("verify", "--build", BUILD, "--dest", dest), r".")
                self.assertFailed(self.admit(pack, dest=dest), r".")
                # Restoring the lock's digest in the sidecar must not help either.
                self.rewrite_sidecar(dest, lambda r: r.update(admitted_tree_sha256=pack.admitted_tree_sha256))
                self.assertFailed(self.run_tool("verify", "--build", BUILD, "--dest", dest), r"tree structure mismatch")
                self.assertFailed(self.admit(pack, dest=dest), r"fails verification")

    def test_format2_record_must_have_no_patches(self):
        self.admit_ok(fmt=2, tree=sp.base_tree(patched=False))
        sp.make_writable(self.dest)
        evil, orig = b"module evil {}\n", (self.dest / sp.MODULEMAP).read_bytes()
        (self.dest / sp.MODULEMAP).write_bytes(evil)
        self.rewrite_sidecar(self.dest, lambda r: r.update(patched=[
            {"path": sp.MODULEMAP, "before_sha256": sp.sha(orig), "after_sha256": sp.sha(evil),
             "original_b64": base64.b64encode(orig).decode()}]))
        self.assertFailed(self.run_tool("verify", "--build", BUILD, "--dest", self.dest), r"patched must be empty")

    def test_verify_needs_matching_admission(self):
        pack = self.pack()
        self.install({BUILD: pack.pin(), "T2": pack.pin()})
        self.assertFailed(self.run_tool("verify", "--build", BUILD, "--dest", self.dest), r"no admission")
        self.assertOk(self.admit(pack))
        self.assertFailed(self.run_tool("verify", "--build", "T2", "--dest", self.dest), r"T2")

    def mounted(self, name="mnt"):
        pack = self.pack(fmt=2)
        self.install({BUILD: pack.pin()})
        tree = self.root / name / "apple-sdk"
        sp.extract_plain(pack.path, tree)
        return tree

    def verify_mounted(self, tree):
        return self.run_tool("verify-mounted", "--build", BUILD, "--dest", tree)

    @unittest.skipIf(os.geteuid() == 0, "root can write a chmod a-w tree; only a read-only mount stops it")
    def test_verify_mounted_good_tree_writes_nothing(self):
        tree = self.mounted()
        sp.make_readonly(tree)
        before = sorted(sp.kinds(tree.parent).items())
        self.assertOk(self.verify_mounted(tree))
        self.assertEqual(before, sorted(sp.kinds(tree.parent).items()))
        self.assertEqual(list(self.scratch.iterdir()), [])

    @unittest.skipIf(os.geteuid() == 0, "needs a non-root caller for a read-only tree")
    def test_verify_mounted_tampered_tree(self):
        for i, (name, tamper) in enumerate(self.TAMPERS.items()):
            with self.subTest(tamper=name):
                tree = self.mounted(f"mnt{i}")
                tamper(tree)
                sp.make_readonly(tree)
                self.assertFailed(self.verify_mounted(tree), r".")

    def test_verify_mounted_refuses_writable_tree(self):
        self.assertFailed(self.verify_mounted(self.mounted()), r"writable")

    def test_verify_mounted_requires_format2(self):
        pack = self.pack(fmt=1)
        self.install({BUILD: pack.pin()})
        tree = self.root / "mnt" / "apple-sdk"
        sp.extract_plain(pack.path, tree)
        sp.make_readonly(tree)
        self.assertFailed(self.verify_mounted(tree), r"format 2")


class StatusTests(AdmitCase):
    def test_status_without_admission(self):
        self.install({})
        self.assertEqual(self.run_tool("status", "--dest", self.dest).returncode, 1)

    def test_status_after_admission(self):
        pack = self.admit_ok()
        proc = self.run_tool("status", "--dest", self.dest)
        self.assertOk(proc)
        lines = proc.stdout.splitlines()
        for want in (f"APPLE_SDK={self.dest}", f"APPLE_SDK_BUILD={BUILD}", "APPLE_SDK_FORMAT=1",
                     f"APPLE_SDK_CONTENT_SHA256={pack.content_sha256}"):
            self.assertIn(want, lines)


if __name__ == "__main__":
    unittest.main()
