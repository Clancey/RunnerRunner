"""Tests for bin/godot-set (set selection and in-image verification)."""
import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

UE = Path(__file__).resolve().parents[1]
HEX = lambda c: c * 64  # noqa: E731

FAKE_EDITOR = """#!/bin/sh
if [ "$1" = "--headless" ] && [ "$2" = "--version" ]; then echo "{version}"; exit 0; fi
echo "unexpected args: $*" >&2; exit 3
"""


def sha256(path):
    import hashlib
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


class Tree:
    """UE copy + synthetic lock + two built sets under <tmp>/sets."""

    def __init__(self, test, official_version_out="4.7.2.stable.official.ed1daf0bf"):
        self.tmp = Path(tempfile.mkdtemp())
        test.addCleanup(shutil.rmtree, self.tmp, True)
        self.ue = self.tmp / "ue"
        (self.ue / "bin").mkdir(parents=True)
        (self.ue / "lib").mkdir()
        shutil.copy2(UE / "bin" / "godot-set", self.ue / "bin" / "godot-set")
        shutil.copy2(UE / "lib" / "lockctl.py", self.ue / "lib" / "lockctl.py")
        self.sets = self.tmp / "sets"
        self.legacy = self.tmp / "legacy"
        self.ks = self.tmp / "debug.keystore"
        self.ks.write_bytes(b"keystore")
        self.editors = {}
        for sid, out in (("official-4.7.2", official_version_out), ("fork-c064", "4.8.dev.custom_build.c0643ef9d")):
            e = self.tmp / f"editor-{sid}"
            e.write_text(FAKE_EDITOR.format(version=out))
            e.chmod(0o755)
            self.editors[sid] = e
        self.vis = b"visionos-template"
        self.lock = self._lock()
        self.write_lock()
        for sid in self.lock["godot_sets"]:
            self.build(sid)

    def _set(self, sid, version, regex, fork):
        tmpl = ["version.txt", "ios.zip"] + (["visionos.zip"] if fork else [])
        leg = self.legacy / sid
        s = {
            "default": not fork, "version": version, "version_regex": regex, "commit": HEX("c"),
            "editor": {"input": "ed", "member": None, "sha256": sha256(self.editors[sid]),
                       "size": self.editors[sid].stat().st_size},
            "templates": {"input": "tp", "required": tmpl},
            "visionos": {"template": "visionos.zip", "sha256": sha256_bytes(self.vis), "size": len(self.vis)} if fork else None,
            "android": {"gradle": "8.11.1", "agp": "8.6.1", "kotlin": "2.1.21", "build_tools": "36.1.0", "compile_sdk": 36},
            "smoke_presets": ["linux-x86_64"] + (["visionos"] if fork else []),
            "legacy": {"editor": [str(leg / "godot")], "templates": [str(leg / "templates" / version)]},
        }
        if fork:
            s["fork_set_id"] = "c064"
            s["legacy"]["visionos"] = [str(leg / "visionos.zip")]
        return s

    def _lock(self):
        return {
            "schema": 1,
            "godot_sets": {
                "official-4.7.2": self._set("official-4.7.2", "4.7.2.stable", r"^4\.7\.2\.stable\.official\.ed1daf0bf", False),
                "fork-c064": self._set("fork-c064", "4.8.dev", r"^4\.8\.dev\.custom_build\.c0643ef9d", True),
            },
        }

    def write_lock(self):
        (self.ue / "toolchain.lock.json").write_text(json.dumps(self.lock, indent=1))

    def build(self, sid):
        s = self.lock["godot_sets"][sid]
        root = self.sets / sid
        tdir = root / "data" / "godot" / "export_templates" / s["version"]
        tdir.mkdir(parents=True)
        shutil.copy2(self.editors[sid], root / "godot")
        (tdir / "version.txt").write_text(s["version"] + "\n")
        (tdir / "ios.zip").write_bytes(b"ios-" + sid.encode())
        if s["visionos"]:
            (tdir / "visionos.zip").write_bytes(self.vis)
            os.link(tdir / "visionos.zip", root / "visionos.zip")
        kd = root / "data" / "godot" / "keystores"
        kd.mkdir(parents=True)
        os.link(self.ks, kd / "debug.keystore")
        r = subprocess.run(["python3", str(self.ue / "lib" / "lockctl.py"), "--lock", str(self.ue / "toolchain.lock.json"),
                            "set-json", sid, "--root", str(root)], capture_output=True, text=True)
        assert r.returncode == 0, r.stderr
        leg = s["legacy"]
        Path(leg["editor"][0]).parent.mkdir(parents=True, exist_ok=True)
        os.link(root / "godot", leg["editor"][0])
        lt = Path(leg["templates"][0])
        lt.mkdir(parents=True)
        for f in tdir.iterdir():
            os.link(f, lt / f.name)
        for v in leg.get("visionos", []):
            os.link(root / "visionos.zip", v)

    def run(self, *args, env=None, input=None):
        e = {k: v for k, v in os.environ.items() if not k.startswith(("GITHUB_", "GODOT", "VISIONOS"))}
        e["GODOT_SETS_ROOT"] = str(self.sets)
        e.update(env or {})
        return subprocess.run([str(self.ue / "bin" / "godot-set"), *args], capture_output=True, text=True, env=e, input=input)


def sha256_bytes(b):
    import hashlib
    return hashlib.sha256(b).hexdigest()


class ListEnvTest(unittest.TestCase):
    def test_list(self):
        t = Tree(self)
        r = t.run("list", "--ids")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(r.stdout.split(), ["official-4.7.2", "fork-c064"])
        r = t.run("list")
        line = [ln for ln in r.stdout.splitlines() if ln.startswith("official-4.7.2")][0]
        self.assertIn("default", line)
        self.assertIn("4.7.2.stable", line)

    def test_env_official_evaluates_in_bash(self):
        t = Tree(self)
        root = t.sets / "official-4.7.2"
        script = 'eval "$(%s env official-4.7.2)" && echo "$GODOT|$XDG_DATA_HOME|$GODOT_TEMPLATES_DIR|$GODOT_SET|$GODOT_SET_VERSION|$GODOT_ANDROID_KEYSTORE_DEBUG_PATH|$(command -v godot)|${PATH##*:}|${VISIONOS_TEMPLATE-unset}"' % (t.ue / "bin" / "godot-set")
        r = subprocess.run(["bash", "-c", script], capture_output=True, text=True,
                           env={"PATH": "/usr/bin:/bin:/tail dir", "GODOT_SETS_ROOT": str(t.sets)})
        self.assertEqual(r.returncode, 0, r.stderr)
        f = r.stdout.strip().split("|")
        self.assertEqual(f[0], str(root / "godot"))
        self.assertEqual(f[1], str(root / "data"))
        self.assertEqual(f[2], str(root / "data/godot/export_templates/4.7.2.stable"))
        self.assertEqual(f[3:5], ["official-4.7.2", "4.7.2.stable"])
        self.assertEqual(f[5], str(root / "data/godot/keystores/debug.keystore"))
        self.assertEqual(f[6], str(root / "godot"))
        self.assertEqual(f[7], "/tail dir", "existing PATH must be preserved after the set root")
        self.assertEqual(f[8], "unset")

    def test_env_fork_has_visionos(self):
        t = Tree(self)
        r = t.run("env", "fork-c064")
        self.assertEqual(r.returncode, 0, r.stderr)
        root = t.sets / "fork-c064"
        self.assertIn(f"export VISIONOS_TEMPLATE={root / 'visionos.zip'}", r.stdout)
        self.assertIn(f"export VISIONOS_GODOT={root / 'godot'}", r.stdout)
        self.assertIn(f"export GODOT_FORK={root / 'godot'}", r.stdout)

    def test_default_alias(self):
        t = Tree(self)
        self.assertEqual(t.run("env", "default").stdout, t.run("env", "official-4.7.2").stdout)

    def test_unknown_set_is_usage_error(self):
        t = Tree(self)
        for args in (("env", "nope"), ("verify", "nope"), ("exec", "nope", "--", "true"), ("github-env", "nope")):
            r = t.run(*args, env={"GITHUB_ENV": str(t.tmp / "ge"), "GITHUB_PATH": str(t.tmp / "gp")})
            self.assertEqual(r.returncode, 2, (args, r.stderr))
            self.assertIn("nope", r.stderr)

    def test_set_missing_on_disk(self):
        t = Tree(self)
        shutil.rmtree(t.sets / "fork-c064")
        r = t.run("env", "fork-c064")
        self.assertEqual(r.returncode, 1)
        self.assertIn("set.json", r.stderr)

    def test_github_env(self):
        t = Tree(self)
        ge, gp = t.tmp / "ge", t.tmp / "gp"
        ge.write_text("PRE=1\n")
        r = t.run("github-env", "fork-c064", env={"GITHUB_ENV": str(ge), "GITHUB_PATH": str(gp)})
        self.assertEqual(r.returncode, 0, r.stderr)
        lines = ge.read_text().splitlines()
        self.assertEqual(lines[0], "PRE=1")
        self.assertIn(f"GODOT={t.sets / 'fork-c064' / 'godot'}", lines)
        self.assertIn("GODOT_SET=fork-c064", lines)
        self.assertFalse(any(ln.startswith("PATH=") for ln in lines))
        self.assertEqual(gp.read_text().splitlines(), [str(t.sets / "fork-c064")])

    def test_github_env_requires_runner_files(self):
        t = Tree(self)
        r = t.run("github-env", "fork-c064")
        self.assertEqual(r.returncode, 2)
        self.assertIn("GITHUB_ENV", r.stderr)

    def test_exec(self):
        t = Tree(self)
        r = t.run("exec", "fork-c064", "--", "sh", "-c", 'echo "$GODOT_SET $(command -v godot)"; exit 7')
        self.assertEqual(r.returncode, 7)
        self.assertEqual(r.stdout.strip(), f"fork-c064 {t.sets / 'fork-c064' / 'godot'}")


class VerifyTest(unittest.TestCase):
    def assertVerifyFails(self, t, needle, *args):
        r = t.run("verify", *(args or ("--all",)))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn(needle, r.stderr)
        return r

    def test_good(self):
        t = Tree(self)
        r = t.run("verify", "--all")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("OK official-4.7.2", r.stdout)
        self.assertIn("OK fork-c064", r.stdout)
        self.assertEqual(t.run("verify", "fork-c064").returncode, 0)

    def test_tampered_editor(self):
        t = Tree(self)
        with open(t.sets / "official-4.7.2" / "godot", "a") as f:
            f.write("# x\n")
        self.assertVerifyFails(t, "editor", "official-4.7.2")

    def test_set_json_cannot_override_lock(self):
        t = Tree(self)
        ed = t.sets / "official-4.7.2" / "godot"
        with open(ed, "a") as f:
            f.write("# x\n")
        sj = t.sets / "official-4.7.2" / "set.json"
        d = json.loads(sj.read_text())
        d["editor"]["sha256"] = sha256(ed)
        sj.write_text(json.dumps(d))
        self.assertVerifyFails(t, "lock", "official-4.7.2")

    def test_extra_template_file(self):
        t = Tree(self)
        (t.sets / "official-4.7.2/data/godot/export_templates/4.7.2.stable/rogue.zip").write_bytes(b"x")
        self.assertVerifyFails(t, "unexpected", "official-4.7.2")

    def test_missing_template_file(self):
        t = Tree(self)
        (t.sets / "official-4.7.2/data/godot/export_templates/4.7.2.stable/ios.zip").unlink()
        self.assertVerifyFails(t, "ios.zip", "official-4.7.2")

    def test_tampered_template(self):
        t = Tree(self)
        p = t.sets / "fork-c064/data/godot/export_templates/4.8.dev/ios.zip"
        p.unlink()
        p.write_bytes(b"other")
        self.assertVerifyFails(t, "ios.zip", "fork-c064")

    def test_legacy_copy_is_not_a_hardlink(self):
        t = Tree(self)
        leg = Path(t.lock["godot_sets"]["official-4.7.2"]["legacy"]["editor"][0])
        leg.unlink()
        shutil.copy2(t.sets / "official-4.7.2" / "godot", leg)
        self.assertVerifyFails(t, "inode", "official-4.7.2")

    def test_legacy_templates_extra_file(self):
        t = Tree(self)
        leg = Path(t.lock["godot_sets"]["fork-c064"]["legacy"]["templates"][0])
        (leg / "extra.apk").write_bytes(b"x")
        self.assertVerifyFails(t, "extra.apk", "fork-c064")

    def test_visionos_must_be_the_template_member(self):
        t = Tree(self)
        v = t.sets / "fork-c064" / "visionos.zip"
        v.unlink()
        v.write_bytes(t.vis)
        for p in t.lock["godot_sets"]["fork-c064"]["legacy"]["visionos"]:
            Path(p).unlink()
            os.link(v, p)
        self.assertVerifyFails(t, "inode", "fork-c064")

    def test_keystore_tampered(self):
        t = Tree(self)
        t.ks.write_bytes(b"another keystore")
        self.assertVerifyFails(t, "keystore", "official-4.7.2")

    def test_version_mismatch_and_no_exec(self):
        t = Tree(self, official_version_out="4.7.1.stable.official.aaaaaaaaa")
        self.assertVerifyFails(t, "version", "official-4.7.2")
        r = t.run("verify", "--no-exec", "official-4.7.2")
        self.assertEqual(r.returncode, 0, r.stderr)

    def test_unpinned_set_dir(self):
        t = Tree(self)
        (t.sets / "rogue-set").mkdir()
        self.assertVerifyFails(t, "rogue-set")

    def test_missing_set_dir(self):
        t = Tree(self)
        shutil.rmtree(t.sets / "fork-c064")
        self.assertVerifyFails(t, "fork-c064")


if __name__ == "__main__":
    unittest.main()
