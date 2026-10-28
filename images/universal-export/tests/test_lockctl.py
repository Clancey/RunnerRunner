import copy
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

UE = Path(__file__).resolve().parents[1]
LOCKCTL = UE / "lib" / "lockctl.py"
REAL_LOCK = json.loads((UE / "toolchain.lock.json").read_text())
H = lambda b: hashlib.sha256(b).hexdigest()  # noqa: E731


def run(*args, lock=None, env=None, cwd=None):
    cmd = [sys.executable, str(LOCKCTL)]
    if lock is not None:
        cmd += ["--lock", str(lock)]
    e = dict(os.environ)
    e.update(env or {})
    return subprocess.run(cmd + list(args), capture_output=True, text=True, env=e, cwd=cwd)


GOOD_DOCKERFILE = """\
# syntax=docker/dockerfile:1.7
ARG BASE_IMAGE
FROM ${BASE_IMAGE}
ARG AAA_SHA256
ARG BBB_SHA256
ARG ANDROID_SDK_PACKAGES
ARG ANDROID_BUILD_TOOLS_VERSION
ARG APT_PACKAGES
ARG APT_I386_PACKAGES
ARG RECIPE_DIGEST
SHELL ["/bin/bash", "-o", "pipefail", "-c"]
RUN echo "${APT_PACKAGES:?} ${APT_I386_PACKAGES:?}" > /tmp/apt
RUN echo "${ANDROID_SDK_PACKAGES:?} ${ANDROID_BUILD_TOOLS_VERSION:?}" > /tmp/android
RUN --mount=type=bind,source=inputs,target=/inputs \\
    echo "${AAA_SHA256:?}  /inputs/${AAA_SHA256:?}" | sha256sum -c --quiet - \\
 && tar -xzf "/inputs/${AAA_SHA256:?}" -C /opt
RUN --mount=type=bind,source=inputs,target=/inputs \\
    echo "${BBB_SHA256:?}  /inputs/${BBB_SHA256:?}" | sha256sum -c --quiet - \\
 && cp "/inputs/${BBB_SHA256:?}" /opt/bbb
COPY lib/ /opt/universal-export/lib/
RUN python3 /opt/universal-export/lib/lockctl.py receipt --recipe-digest "${RECIPE_DIGEST:?}"
"""


class LockTree:
    """A temp UE tree with a synthetic two-input lock."""

    def __init__(self, test):
        self.dir = Path(tempfile.mkdtemp())
        test.addCleanup(shutil.rmtree, self.dir, True)
        (self.dir / "ruby").mkdir()
        (self.dir / "ruby" / "Gemfile.lock").write_text("GEM\n")
        self.lock = copy.deepcopy(REAL_LOCK)
        self.lock["inputs"] = {
            "aaa": {"arg": "AAA", "version": "1", "filename": "aaa.tgz", "url": "https://x/aaa.tgz",
                    "sha256": "a" * 64, "size": 10},
            "bbb": {"arg": "BBB", "version": "2", "filename": "bbb", "url": None, "rolling": True,
                    "sha256": "b" * 64, "size": None,
                    "seeds": ["file:/mnt/x/bbb", "image:<retained tag>:/opt/bbb", "image:old:1.0:/opt/bbb"]},
        }
        for s in self.lock["godot_sets"].values():
            s["editor"]["input"] = "aaa"
            s["templates"]["input"] = "bbb"
        self.lock["ruby"]["bundler"]["input"] = "aaa"
        self.lock["ruby"]["gemfile_lock_sha256"] = H(b"GEM\n")
        self.write()

    @property
    def path(self):
        return self.dir / "toolchain.lock.json"

    def write(self):
        self.path.write_text(json.dumps(self.lock, indent=2) + "\n")


class ValidateTest(unittest.TestCase):
    def test_real_lock_validates(self):
        r = run("validate")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def assertInvalid(self, t, needle):
        t.write()
        r = run("validate", lock=t.path)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn(needle, r.stderr)

    def test_synthetic_lock_validates(self):
        t = LockTree(self)
        r = run("validate", lock=t.path)
        self.assertEqual(r.returncode, 0, r.stderr)

    def test_bad_sha(self):
        t = LockTree(self)
        t.lock["inputs"]["aaa"]["sha256"] = "A" * 64
        self.assertInvalid(t, "inputs.aaa.sha256")

    def test_duplicate_arg(self):
        t = LockTree(self)
        t.lock["inputs"]["bbb"]["arg"] = "AAA"
        self.assertInvalid(t, "duplicate arg AAA")

    def test_arg_naming(self):
        t = LockTree(self)
        t.lock["inputs"]["bbb"]["arg"] = "bbb-x"
        self.assertInvalid(t, "inputs.bbb.arg")

    def test_apt_package_names(self):
        for key, bad in (("packages", ["git", "evil; rm -rf /"]), ("packages", []),
                         ("i386_packages", ["lib32gcc-s1", "a b"]), ("i386_packages", [])):
            with self.subTest(key=key, bad=bad):
                t = LockTree(self)
                t.lock["apt"][key] = bad
                self.assertInvalid(t, f"apt.{key}")

    def test_android_package_names(self):
        t = LockTree(self)
        t.lock["android"]["packages"].append("platforms;android-36 $(id)")
        self.assertInvalid(t, "android.packages")

    def test_format1_pack_needs_admitted_tree_digest(self):
        t = LockTree(self)
        del t.lock["apple_sdk"]["known_packs"]["27A9275"]["admitted_tree_sha256"]
        self.assertInvalid(t, "admitted_tree_sha256 required for format 1")

    def test_two_defaults(self):
        t = LockTree(self)
        for s in t.lock["godot_sets"].values():
            s["default"] = True
        self.assertInvalid(t, "exactly one default")

    def test_no_source(self):
        t = LockTree(self)
        t.lock["inputs"]["bbb"]["seeds"] = ["image:<retained tag>:/opt/bbb"]
        self.assertInvalid(t, "no url and no concrete seed")

    def test_unknown_input_reference(self):
        t = LockTree(self)
        t.lock["godot_sets"]["fork-c064"]["templates"]["input"] = "nope"
        self.assertInvalid(t, "unknown input nope")

    def test_visionos_preset_needs_visionos(self):
        t = LockTree(self)
        t.lock["godot_sets"]["official-4.7.2"]["smoke_presets"].append("visionos")
        self.assertInvalid(t, "visionos")

    def test_visionos_template_must_be_required(self):
        t = LockTree(self)
        t.lock["godot_sets"]["fork-c064"]["templates"]["required"].remove("visionos.zip")
        self.assertInvalid(t, "visionos")

    def test_gemfile_lock_digest(self):
        t = LockTree(self)
        (t.dir / "ruby" / "Gemfile.lock").write_text("GEM\nchanged\n")
        self.assertInvalid(t, "gemfile_lock_sha256")

    def test_unpinned_base(self):
        t = LockTree(self)
        t.lock["image"]["base"] = "myoung34/github-runner:ubuntu-noble"
        self.assertInvalid(t, "image.base")

    def test_build_tools_package_consistency(self):
        t = LockTree(self)
        t.lock["android"]["build_tools"] = "35.0.0"
        self.assertInvalid(t, "build-tools;35.0.0")

    def test_bad_set_id(self):
        t = LockTree(self)
        t.lock["godot_sets"]["Bad Set"] = t.lock["godot_sets"].pop("fork-c064")
        self.assertInvalid(t, "Bad Set")

    def test_legacy_paths_absolute(self):
        t = LockTree(self)
        t.lock["godot_sets"]["fork-c064"]["legacy"]["editor"].append("relative/godot")
        self.assertInvalid(t, "relative/godot")


class GetTest(unittest.TestCase):
    def test_get_dotted_keys_containing_dots(self):
        r = run("get", "godot_sets.official-4.7.2.smoke_presets")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(json.loads(r.stdout), REAL_LOCK["godot_sets"]["official-4.7.2"]["smoke_presets"])

    def test_get_raw(self):
        r = run("get", "--raw", "image.name")
        self.assertEqual(r.stdout, "rr-universal-export\n")
        r = run("get", "--raw", "android.packages")
        self.assertEqual(r.stdout.splitlines(), REAL_LOCK["android"]["packages"])

    def test_get_missing(self):
        r = run("get", "image.nope")
        self.assertEqual(r.returncode, 2)


class CheckTagTest(unittest.TestCase):
    def test_good_tags(self):
        for t in ("ue-20261028-8cc3849e1bc0", "ue-20261028-8cc3849e1bc0-r2", "candidate.1"):
            r = run("check-tag", t)
            self.assertEqual(r.returncode, 0, (t, r.stderr))

    def test_protected_tags_refused(self):
        for t in ("current", "latest", "previous", "stable", "canary"):
            r = run("check-tag", t)
            self.assertEqual(r.returncode, 1, t)
            self.assertIn("protected", r.stderr)

    def test_malformed_tags_refused(self):
        for t in ("", "-x", ".x", "a b", "a;id", "x" * 129, "ue/1", "UE:1"):
            r = run("check-tag", "--", t)
            self.assertEqual(r.returncode, 1, repr(t))

    def test_default_tag(self):
        r = run("default-tag", "--date", "20261028")
        self.assertEqual(r.returncode, 0, r.stderr)
        digest = run("recipe-digest").stdout.strip()
        self.assertEqual(r.stdout.strip(), f"ue-20261028-{digest[:12]}")
        self.assertEqual(run("default-tag", "--date", "2026-10-28").returncode, 2)


class GradleTest(unittest.TestCase):
    def test_dist_dir_matches_gradle_wrapper_hash(self):
        # Test vector: ~/.gradle/wrapper/dists/gradle-8.11.1-bin/<dir> created
        # by a real Gradle wrapper for this exact distributionUrl.
        r = run("gradle-dist-dir", env={"GRADLE_USER_HOME": "/g"})
        self.assertEqual(r.stdout.strip(), "/g/wrapper/dists/gradle-8.11.1-bin/bpt9gzteqjrbo1mjrsomdt32c")


class BuildArgsPlanTest(unittest.TestCase):
    def test_build_args(self):
        t = LockTree(self)
        r = run("build-args", lock=t.path)
        self.assertEqual(r.returncode, 0, r.stderr)
        kv = dict(line.split("=", 1) for line in r.stdout.splitlines())
        self.assertEqual(kv["BASE_IMAGE"], t.lock["image"]["base"])
        self.assertEqual(kv["AAA_SHA256"], "a" * 64)
        self.assertEqual(kv["BBB_SHA256"], "b" * 64)
        self.assertRegex(kv["RECIPE_DIGEST"], "^[0-9a-f]{64}$")
        self.assertEqual(kv["ANDROID_SDK_PACKAGES"], " ".join(t.lock["android"]["packages"]))
        self.assertEqual(kv["ANDROID_BUILD_TOOLS_VERSION"], t.lock["android"]["build_tools"])
        self.assertEqual(kv["APT_PACKAGES"], " ".join(t.lock["apt"]["packages"]))
        self.assertEqual(kv["APT_I386_PACKAGES"], " ".join(t.lock["apt"]["i386_packages"]))
        self.assertEqual(list(kv)[-1], "RECIPE_DIGEST")

    def test_real_build_args_gradle_dist_dir_ignores_caller_env(self):
        r = run("build-args", env={"GRADLE_USER_HOME": "/elsewhere"})
        kv = dict(line.split("=", 1) for line in r.stdout.splitlines())
        self.assertEqual(kv["GRADLE_DIST_DIR"],
                         "/opt/gradle-home/wrapper/dists/gradle-8.11.1-bin/bpt9gzteqjrbo1mjrsomdt32c")

    def test_real_build_args_have_no_visionos_input(self):
        r = run("build-args")
        self.assertNotIn("GODOT_FORK_C064_VISIONOS", r.stdout)
        self.assertIn("GODOT_FORK_C064_TEMPLATES_SHA256=", r.stdout)

    def test_plan(self):
        t = LockTree(self)
        r = run("plan", lock=t.path)
        rows = [line.split("\t") for line in r.stdout.splitlines()]
        self.assertEqual([row[0] for row in rows], ["aaa", "bbb"])
        aaa, bbb = rows
        self.assertEqual(aaa, ["aaa", "a" * 64, "10", "aaa.tgz", "https://x/aaa.tgz", "0", "-"])
        # Placeholder seeds (with <...>) are documentation only and dropped.
        self.assertEqual(bbb, ["bbb", "b" * 64, "-", "bbb", "-", "1", "file:/mnt/x/bbb image:old:1.0:/opt/bbb"])

    def test_verify_file(self):
        t = LockTree(self)
        f = t.dir / "x"
        f.write_bytes(b"0123456789")
        t.lock["inputs"]["aaa"]["sha256"] = H(b"0123456789")
        t.write()
        self.assertEqual(run("verify-file", "aaa", str(f), lock=t.path).returncode, 0)
        f.write_bytes(b"012345678")
        r = run("verify-file", "aaa", str(f), lock=t.path)
        self.assertEqual(r.returncode, 1)
        self.assertIn("mismatch", r.stderr)


class CheckDockerfileTest(unittest.TestCase):
    def setUp(self):
        self.t = LockTree(self)
        self.df = self.t.dir / "Dockerfile"

    def check(self, text):
        self.df.write_text(text)
        return run("check-dockerfile", str(self.df), lock=self.t.path)

    def assertRejects(self, text, needle):
        r = self.check(text)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn(needle, r.stderr)

    def test_good(self):
        r = self.check(GOOD_DOCKERFILE)
        self.assertEqual(r.returncode, 0, r.stderr)

    def test_literal_digest(self):
        self.assertRejects(GOOD_DOCKERFILE + "RUN echo " + "c" * 64 + "\n", "64-hex literal")

    def test_steamcmd_run_at_build_time(self):
        self.assertRejects(GOOD_DOCKERFILE + "RUN /opt/steamcmd/steamcmd.sh +quit\n", "steamcmd must not run")
        self.assertRejects(GOOD_DOCKERFILE + "RUN steamcmd +login anonymous +quit\n", "steamcmd must not run")

    def test_arg_default(self):
        self.assertRejects(GOOD_DOCKERFILE.replace("ARG BBB_SHA256", "ARG BBB_SHA256=" + "b" * 3), "default")

    def test_missing_arg(self):
        text = GOOD_DOCKERFILE.replace("ARG BBB_SHA256\n", "")
        self.assertRejects(text, "BBB_SHA256")

    def test_extra_arg(self):
        self.assertRejects(GOOD_DOCKERFILE.replace("ARG RECIPE_DIGEST", "ARG RECIPE_DIGEST\nARG EXTRA_THING"), "EXTRA_THING")

    def test_unguarded_reference(self):
        text = GOOD_DOCKERFILE.replace('cp "/inputs/${BBB_SHA256:?}"', 'cp "/inputs/$BBB_SHA256"')
        self.assertRejects(text, "unguarded")

    def test_input_not_verified(self):
        text = GOOD_DOCKERFILE.replace(
            'echo "${BBB_SHA256:?}  /inputs/${BBB_SHA256:?}" | sha256sum -c --quiet - \\\n && ', "")
        self.assertRejects(text, "BBB_SHA256 is never verified")

    def test_input_without_bind_mount(self):
        text = GOOD_DOCKERFILE.replace(
            "RUN --mount=type=bind,source=inputs,target=/inputs \\\n    echo \"${BBB", "RUN echo \"${BBB")
        self.assertRejects(text, "without the inputs bind mount")

    def test_writable_bind_mount(self):
        text = GOOD_DOCKERFILE.replace("target=/inputs \\\n    echo \"${BBB", "target=/inputs,rw \\\n    echo \"${BBB")
        self.assertRejects(text, "rw")

    def test_add_forbidden(self):
        self.assertRejects(GOOD_DOCKERFILE + "ADD x /x\n", "ADD")

    def test_copy_inputs_forbidden(self):
        self.assertRejects(GOOD_DOCKERFILE + "COPY inputs/ /inputs/\n", "COPY of inputs")

    def test_from_must_be_base_arg(self):
        self.assertRejects(GOOD_DOCKERFILE.replace("FROM ${BASE_IMAGE}", "FROM ubuntu:24.04"), "FROM")

    def test_single_stage(self):
        self.assertRejects(GOOD_DOCKERFILE + "FROM ${BASE_IMAGE}\n", "exactly one FROM")

    def test_real_dockerfile(self):
        if not (UE / "Dockerfile").exists():
            self.skipTest("Dockerfile not written yet")
        r = run("check-dockerfile", str(UE / "Dockerfile"))
        self.assertEqual(r.returncode, 0, r.stderr)


class RecipeDigestTest(unittest.TestCase):
    def setUp(self):
        self.t = LockTree(self)
        d = self.t.dir
        for p in ("bin", "lib", "smoke", "tests", "mac", "lib/__pycache__"):
            (d / p).mkdir(parents=True, exist_ok=True)
        (d / "Dockerfile").write_text("FROM x\n")
        (d / "bin" / "tool").write_text("#!/bin/sh\n")
        (d / "smoke" / "s.py").write_text("print(1)\n")
        (d / "lib" / "lockctl.py").write_text("x\n")

    def digest(self):
        r = run("recipe-digest", "--root", str(self.t.dir), lock=self.t.path)
        self.assertEqual(r.returncode, 0, r.stderr)
        return r.stdout.strip()

    def test_stable_and_scoped(self):
        d0 = self.digest()
        self.assertRegex(d0, "^[0-9a-f]{64}$")
        self.assertEqual(self.digest(), d0)
        (self.t.dir / "tests" / "t.py").write_text("x")
        (self.t.dir / "mac" / "m.sh").write_text("x")
        (self.t.dir / "lib" / "__pycache__" / "a.pyc").write_bytes(b"x")
        (self.t.dir / "README.md").write_text("docs")
        self.assertEqual(self.digest(), d0)

    def test_content_and_mode_changes(self):
        d0 = self.digest()
        (self.t.dir / "smoke" / "s.py").write_text("print(2)\n")
        d1 = self.digest()
        self.assertNotEqual(d1, d0)
        os.chmod(self.t.dir / "bin" / "tool", 0o755)
        self.assertNotEqual(self.digest(), d1)


class SetJsonTest(unittest.TestCase):
    def build_set(self, set_id, files=None):
        self.t = LockTree(self)
        s = self.t.lock["godot_sets"][set_id]
        root = self.t.dir / "sets" / set_id
        tdir = root / "data" / "godot" / "export_templates" / s["version"]
        tdir.mkdir(parents=True)
        (root / "data" / "godot" / "keystores").mkdir(parents=True)
        (root / "data" / "godot" / "keystores" / "debug.keystore").write_bytes(b"ks")
        editor = b"editor-" + set_id.encode()
        (root / "godot").write_bytes(editor)
        s["editor"]["sha256"], s["editor"]["size"] = H(editor), len(editor)
        files = files or {name: name.encode() for name in s["templates"]["required"]}
        files["version.txt"] = s["version"].encode() + b"\n"
        for name, data in files.items():
            (tdir / name).write_bytes(data)
        if s.get("visionos"):
            os.link(tdir / "visionos.zip", root / "visionos.zip")
            s["visionos"]["sha256"] = H(files["visionos.zip"])
            s["visionos"]["size"] = len(files["visionos.zip"])
        self.t.write()
        return s, root, tdir

    def set_json(self, set_id, root):
        return run("set-json", set_id, "--root", str(root), lock=self.t.path)

    def test_fork_set_json(self):
        s, root, tdir = self.build_set("fork-c064")
        r = self.set_json("fork-c064", root)
        self.assertEqual(r.returncode, 0, r.stderr)
        j = json.loads((root / "set.json").read_text())
        self.assertEqual(j["schema"], 1)
        self.assertEqual(j["id"], "fork-c064")
        self.assertFalse(j["default"])
        self.assertEqual(j["root"], str(root))
        self.assertEqual(j["editor"], {"path": str(root / "godot"), "sha256": s["editor"]["sha256"]})
        self.assertEqual(j["data_dir"], str(root / "data"))
        self.assertEqual(j["templates"]["dir"], str(tdir))
        self.assertEqual(j["templates"]["files"]["android_debug.apk"], H(b"android_debug.apk"))
        self.assertIn("version.txt", j["templates"]["files"])
        self.assertEqual(j["visionos"]["path"], str(root / "visionos.zip"))
        self.assertEqual(j["keystore"]["sha256"], H(b"ks"))
        self.assertEqual(j["fork_set_id"], "c064")
        self.assertEqual(j["smoke_presets"], s["smoke_presets"])
        self.assertEqual(j["legacy"], s["legacy"])
        self.assertEqual(j["android"], s["android"])

    def test_official_set_json(self):
        s, root, _ = self.build_set("official-4.7.2")
        r = self.set_json("official-4.7.2", root)
        self.assertEqual(r.returncode, 0, r.stderr)
        j = json.loads((root / "set.json").read_text())
        self.assertIsNone(j["visionos"])
        self.assertIsNone(j["fork_set_id"])
        self.assertTrue(j["default"])

    def test_editor_mismatch(self):
        s, root, _ = self.build_set("official-4.7.2")
        (root / "godot").write_bytes(b"tampered")
        r = self.set_json("official-4.7.2", root)
        self.assertEqual(r.returncode, 1)
        self.assertIn("editor", r.stderr)
        self.assertFalse((root / "set.json").exists())

    def test_missing_required_template(self):
        s, root, tdir = self.build_set("official-4.7.2")
        (tdir / "ios.zip").unlink()
        r = self.set_json("official-4.7.2", root)
        self.assertEqual(r.returncode, 1)
        self.assertIn("ios.zip", r.stderr)

    def test_version_txt_mismatch(self):
        s, root, tdir = self.build_set("official-4.7.2")
        (tdir / "version.txt").write_text("4.7.1.stable\n")
        r = self.set_json("official-4.7.2", root)
        self.assertEqual(r.returncode, 1)
        self.assertIn("version.txt", r.stderr)

    def test_visionos_mismatch(self):
        s, root, tdir = self.build_set("fork-c064")
        s["visionos"]["sha256"] = "c" * 64
        self.t.write()
        r = self.set_json("fork-c064", root)
        self.assertEqual(r.returncode, 1)
        self.assertIn("visionos", r.stderr)

    def test_unknown_set(self):
        t = LockTree(self)
        r = run("set-json", "nope", "--root", str(t.dir), lock=t.path)
        self.assertEqual(r.returncode, 2)


class InstallChecksTest(unittest.TestCase):
    def load(self):
        from importlib.machinery import SourceFileLoader
        return SourceFileLoader("lockctl", str(LOCKCTL)).load_module()

    def test_checks_cover_every_input_and_package(self):
        m = self.load()
        checks = m.install_checks(REAL_LOCK, env={"GRADLE_USER_HOME": "/opt/gradle-home"})
        names = {c.name for c in checks}
        for input_id in REAL_LOCK["inputs"]:
            if input_id.startswith("godot-"):
                continue
            self.assertIn(f"input:{input_id}", names)
        for pkg in REAL_LOCK["android"]["packages"]:
            self.assertIn(f"android:{pkg}", names)
        self.assertIn("godot-sets", names)
        self.assertIn("apple-sdk-absent", names)
        self.assertIn("ruby", names)
        self.assertIn("fastlane", names)

    def test_checks_cover_base_tools_and_python_modules(self):
        m = self.load()
        checks = {c.name: c for c in m.install_checks(REAL_LOCK, env={"GRADLE_USER_HOME": "/opt/gradle-home"})}
        for tool in REAL_LOCK["base_tools"]:
            self.assertIn(f"base:{tool}", checks)
            self.assertEqual(checks[f"base:{tool}"].argv[0], tool)
        self.assertIn("python-modules", checks)
        self.assertIn("import yaml", " ".join(checks["python-modules"].argv))
        self.assertIn("PIL", " ".join(checks["python-modules"].argv))

    def test_image_env_check_matches_lock(self):
        m = self.load()
        c = {c.name: c for c in m.install_checks(REAL_LOCK, env={})}["image-env"]
        want = {
            "APPLE_SDK": "/opt/apple-sdk",
            "ANDROID_HOME": "/opt/android-sdk",
            "ANDROID_SDK_ROOT": "/opt/android-sdk",
            "ANDROID_BUILD_TOOLS_VERSION": "36.1.0",
            "GRADLE_USER_HOME": "/opt/gradle-home",
            "GODOT": "/opt/godot/official/godot",
            "GODOT_TEMPLATES_DIR": "/opt/godot-data/godot/export_templates/4.7.2.stable",
            "XDG_DATA_HOME": "/opt/godot-data",
            "GODOT_ANDROID_KEYSTORE_DEBUG_PATH": "/opt/android/debug.keystore",
            "GODOT_ANDROID_KEYSTORE_DEBUG_USER": "androiddebugkey",
            "GODOT_ANDROID_KEYSTORE_DEBUG_PASSWORD": "android",
            "GODOT_FORK_SETS": "c064",
            "GODOT_FORK_C064_COMMIT": "c0643ef9d8e5c7efbe2893502611a77025cc0a86",
            "GODOT_FORK_C064_EDITOR": "/opt/godot-fork/c064/godot",
            "GODOT_FORK_C064_VISIONOS_TEMPLATE": "/opt/godot-fork/c064/visionos.zip",
            "GODOT_FORK_C064_TEMPLATES": "/opt/godot-data/godot/export_templates/4.8.dev",
            "GODOT_FORK": "/opt/godot-fork/c064/godot",
            "VISIONOS_GODOT": "/opt/godot-fork/c064/godot",
            "VISIONOS_TEMPLATE": "/opt/godot-fork/c064/visionos.zip",
            "UE_HOME": "/opt/universal-export",
        }
        self.assertEqual(c.env, want)
        ok = m.evaluate([c], environ=dict(want))
        self.assertTrue(ok[0]["ok"], ok[0]["detail"])
        drift = dict(want, GODOT_FORK_C064_COMMIT="0" * 40)
        bad = m.evaluate([c], environ=drift)
        self.assertFalse(bad[0]["ok"])
        self.assertIn("GODOT_FORK_C064_COMMIT", bad[0]["detail"])
        missing = dict(want)
        del missing["VISIONOS_TEMPLATE"]
        self.assertFalse(m.evaluate([c], environ=missing)[0]["ok"])

    def test_keystore_entry_check(self):
        m = self.load()
        c = {c.name: c for c in m.install_checks(REAL_LOCK, env={})}["debug-keystore-entry"]
        self.assertEqual(c.argv[:2], ["keytool", "-list"])
        self.assertIn("/opt/android/debug.keystore", c.argv)
        self.assertIn("androiddebugkey", c.argv)
        self.assertEqual(c.expect, "CN=Android Debug")

    def test_evaluate_with_fake_runner(self):
        m = self.load()
        c = m.Check("x", argv=["tool", "--version"], expect="1.2.3")
        ok = m.evaluate([c], runner=lambda argv: (0, "tool 1.2.3\n"))
        self.assertTrue(ok[0]["ok"])
        bad = m.evaluate([c], runner=lambda argv: (0, "tool 1.2.4\n"))
        self.assertFalse(bad[0]["ok"])
        failed = m.evaluate([c], runner=lambda argv: (1, "boom"))
        self.assertFalse(failed[0]["ok"])


class ImageTreeChecksTest(unittest.TestCase):
    def load(self):
        from importlib.machinery import SourceFileLoader
        return SourceFileLoader("lockctl", str(LOCKCTL)).load_module()

    def tmp(self):
        d = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, d, True)
        return d

    def test_steamcmd_check_is_wired(self):
        m = self.load()
        c = {c.name: c for c in m.install_checks(REAL_LOCK, env={})}["input:steamcmd"]
        self.assertEqual(c.tree_manifest, ("/opt/steamcmd", "/opt/universal-export/steamcmd.files.sha256"))

    def test_steamcmd_tree_manifest(self):
        m = self.load()
        d = self.tmp()
        root = d / "steamcmd"
        (root / "linux32").mkdir(parents=True)
        (root / "steamcmd.sh").write_bytes(b"#!/bin/sh\n")
        (root / "linux32" / "steamcmd").write_bytes(b"ELF")
        os.symlink("steamcmd", root / "linux32" / "link")
        man = d / "steamcmd.files.sha256"
        man.write_text("".join(f"{H(b)}  ./{rel}\n" for rel, b in [
            ("linux32/link", b"ELF"), ("linux32/steamcmd", b"ELF"), ("steamcmd.sh", b"#!/bin/sh\n")]))
        c = m.Check("input:steamcmd", tree_manifest=(str(root), str(man)))
        ok = m.evaluate([c], environ={})[0]
        self.assertTrue(ok["ok"], ok["detail"])
        (root / "steamcmd.sh").write_bytes(b"#!/bin/sh\nupdated\n")
        bad = m.evaluate([c], environ={})[0]
        self.assertFalse(bad["ok"])
        self.assertIn("changed since extraction", bad["detail"])
        (root / "steamcmd.sh").write_bytes(b"#!/bin/sh\n")
        (root / "package").mkdir()
        (root / "package" / "steam_cmd_linux.zip").write_bytes(b"PK")
        bad = m.evaluate([c], environ={})[0]
        self.assertFalse(bad["ok"])
        self.assertIn("not from the pinned archive", bad["detail"])
        shutil.rmtree(root / "package")
        (root / "linux32" / "steamcmd").unlink()
        bad = m.evaluate([c], environ={})[0]
        self.assertFalse(bad["ok"])
        self.assertIn("missing", bad["detail"])
        no_manifest = m.Check("input:steamcmd", tree_manifest=(str(root), str(d / "nope")))
        self.assertFalse(m.evaluate([no_manifest], environ={})[0]["ok"])

    def test_dsymutil_check_is_wired(self):
        m = self.load()
        c = {c.name: c for c in m.install_checks(REAL_LOCK, env={})}["linux-apple-dsymutil"]
        self.assertEqual(c.links, {"/opt/linux-apple/bin/dsymutil": "/opt/darwin-tools/bin/dsymutil"})
        self.assertEqual(c.resolves, {"dsymutil": "/opt/linux-apple/bin/dsymutil"})

    def test_dsymutil_link_and_path_order(self):
        m = self.load()
        d = self.tmp()
        dt, la, sw = d / "darwin-tools/bin", d / "linux-apple/bin", d / "swift/bin"
        for b in (dt, la, sw):
            b.mkdir(parents=True)
        for b in (dt, sw):
            (b / "dsymutil").write_text("#!/bin/sh\n")
            (b / "dsymutil").chmod(0o755)
        os.symlink(str(dt / "dsymutil"), la / "dsymutil")
        c = m.Check("linux-apple-dsymutil", links={str(la / "dsymutil"): str(dt / "dsymutil")},
                    resolves={"dsymutil": str(la / "dsymutil")})
        good = m.evaluate([c], environ={"PATH": f"{la}:{sw}"})[0]
        self.assertTrue(good["ok"], good["detail"])
        bad = m.evaluate([c], environ={"PATH": f"{sw}:{la}"})[0]
        self.assertFalse(bad["ok"])
        self.assertIn("resolves to", bad["detail"])
        (la / "dsymutil").unlink()
        os.symlink(str(sw / "dsymutil"), la / "dsymutil")
        bad = m.evaluate([c], environ={"PATH": f"{la}:{sw}"})[0]
        self.assertFalse(bad["ok"])
        self.assertIn("must be a symlink", bad["detail"])

    def test_dockerfile_wires_dsymutil_before_swift(self):
        text = (UE / "Dockerfile").read_text()
        self.assertIn("ln -sf /opt/darwin-tools/bin/dsymutil /opt/linux-apple/bin/dsymutil", text)
        path = [l for l in text.splitlines() if "PATH=" in l and "/opt/swift/bin" in l][0]
        self.assertLess(path.index("/opt/linux-apple/bin"), path.index("/opt/swift/bin"))

    def test_android_package_revisions(self):
        m = self.load()
        d = self.tmp()
        lock = {"android": {"home": "/nonexistent", "packages": ["platform-tools", "build-tools;36.1.0",
                                                                  "platforms;android-35"]}}
        for rel, body in [("platform-tools", "Pkg.Desc=x\nPkg.Revision=36.0.2\n"),
                          ("build-tools/36.1.0", "Pkg.Revision = 36.1.0\n"),
                          ("cmdline-tools/latest", "Pkg.Revision=19.0\n")]:
            (d / rel).mkdir(parents=True)
            (d / rel / "source.properties").write_text(body)
        want = {"cmdline-tools;latest": "19.0", "platform-tools": "36.0.2",
                "build-tools;36.1.0": "36.1.0", "platforms;android-35": None}
        self.assertEqual(m.android_package_revisions(lock, str(d)), want)
        old = os.environ.get("ANDROID_SDK_ROOT")
        os.environ["ANDROID_SDK_ROOT"] = str(d)
        try:
            lock_path = d / "lock.json"
            lock_path.write_text("{}")
            rec = m.receipt(dict(REAL_LOCK, android=lock["android"]), lock_path, "r" * 64, [])
        finally:
            if old is None:
                del os.environ["ANDROID_SDK_ROOT"]
            else:
                os.environ["ANDROID_SDK_ROOT"] = old
        self.assertEqual(rec["android_packages"], want)


if __name__ == "__main__":
    unittest.main()
