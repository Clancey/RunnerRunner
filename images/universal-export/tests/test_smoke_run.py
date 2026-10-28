"""smoke/run_smoke.sh end to end against fake godot / godot-set / signing tools, and smoke/report.py."""
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import textwrap
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SMOKE = HERE.parent / "smoke"
sys.path[:0] = [str(SMOKE), str(HERE)]
import report  # noqa: E402
from test_smoke_apple_link import FAKE_BUNDLE_UTIL, FAKE_XCRUN  # noqa: E402

SECRET = "feedface" * 4  # what the fake `openssl rand` hands out; must never be printed

FAKE_GODOT = """\
import json, os, plistlib, sys
from pathlib import Path
sys.path[:0] = [{tests!r}]
from test_smoke_checks import elf, fat, macho, pe, zip_bytes
from test_smoke_apple_link import make_export
a = sys.argv[1:]
assert a[0] == "--headless", a
proj = Path(a[a.index("--path") + 1])
if "--import" in a:
    sys.exit(0)
mode = next(m for m in ("--export-release", "--export-debug") if m in a)
preset, out = a[a.index(mode) + 1], Path(a[a.index(mode) + 2])
assert out.is_absolute(), out
manifest = json.loads((proj / "smoke.json").read_text())
entry = next(e for e in manifest["presets"] if e["name"] == preset)
assert mode == "--export-" + entry["mode"], (mode, entry)
settings = Path(os.environ["XDG_CONFIG_HOME"]) / "godot" / "editor_settings-4.tres"
assert "android_sdk_path" in settings.read_text()
if "--install-android-build-template" in a:
    (proj / "android" / "build").mkdir(parents=True)
if os.environ.get("FAKE_FAIL") == manifest["set"] + "/" + preset:
    print("SCRIPT ERROR: fake failure")
    sys.exit(0)
d, stem, plat = out.parent, out.stem, entry["platform"]
if plat in ("Linux", "Windows Desktop"):
    arch = "arm64" if preset.endswith("arm64") else "x86_64"
    out.write_bytes(elf(183 if arch == "arm64" else 62) if plat == "Linux"
                    else pe(sections=((".rsrc", manifest["product_name"].encode("utf-16-le")),)))
    (d / (stem + ".pck")).write_bytes(b"GDPC")
elif plat == "macOS":
    info = plistlib.dumps({{"CFBundleExecutable": "UE Smoke", "CFBundleIdentifier": "org.universalexport.smoke"}})
    out.write_bytes(zip_bytes({{"UE Smoke.app/Contents/Info.plist": info,
                               "UE Smoke.app/Contents/MacOS/UE Smoke": fat(macho("x86_64", 1), macho("arm64", 1)),
                               "UE Smoke.app/Contents/Resources/UE Smoke.pck": b"GDPC"}}))
elif plat == "Web":
    for ext in ("html", "js", "wasm", "pck"):
        (d / ("index." + ext)).write_bytes(b"x")
elif plat == "Android":
    assert (proj / "android" / "build").is_dir(), "template not installed"
    if entry["mode"] == "debug":
        dn = "CN=Android Debug, O=Android, C=US"
    else:
        assert os.environ["GODOT_ANDROID_KEYSTORE_RELEASE_PASSWORD"]
        assert os.environ["GODOT_ANDROID_KEYSTORE_RELEASE_USER"]
        dn = Path(os.environ["GODOT_ANDROID_KEYSTORE_RELEASE_PATH"]).read_text()
    files = {{"base/manifest/AndroidManifest.xml": b"", "BundleConfig.pb": b"",
             "base/dex/classes.dex": b"", "base/lib/arm64-v8a/libgodot_android.so": b""}} if out.suffix == ".aab" else \\
        {{"AndroidManifest.xml": b"", "classes.dex": b"", "lib/arm64-v8a/libgodot_android.so": b""}}
    files["META-INF/FAKE-DN"] = dn.encode()
    out.write_bytes(zip_bytes(files))
elif plat in ("iOS", "visionOS"):
    make_export(d, "ios" if plat == "iOS" else "visionos", name=stem)
else:
    sys.exit("unexpected platform " + plat)
"""
FAKE_GODOT_SET = """\
import os, shlex, sys
ids = os.environ["FAKE_SETS"].split(",")
a = sys.argv[1:]
if a == ["list", "--ids"]:
    print("\\n".join(ids))
    sys.exit(0)
if a[0] != "env" or a[1] not in ids:
    print("godot-set: unknown set " + a[-1], file=sys.stderr)
    sys.exit(2)
sid, root = a[1], os.environ["FAKE_ROOT"]
env = {"GODOT": root + "/bin/godot", "GODOT_SET": sid, "GODOT_SET_COMMIT": "abc",
       "GODOT_SET_VERSION": "4.8.dev" if sid.startswith("fork") else "4.7.2.stable",
       "XDG_DATA_HOME": root + "/data", "GODOT_TEMPLATES_DIR": root + "/data/godot/export_templates/x",
       "GODOT_ANDROID_KEYSTORE_DEBUG_PATH": root + "/debug.keystore"}
if sid.startswith("fork"):
    env["VISIONOS_TEMPLATE"] = root + "/visionos.zip"
for k, v in env.items():
    print("export %s=%s" % (k, shlex.quote(v)))
print('export PATH=%s"${PATH:+:$PATH}"' % shlex.quote(root + "/bin"))
"""
FAKE_KEYTOOL = """\
import os, sys
from pathlib import Path
a = sys.argv[1:]
assert a[0] == "-genkeypair" and os.environ[a[a.index("-storepass:env") + 1]], a
Path(a[a.index("-keystore") + 1]).write_text(a[a.index("-dname") + 1])
"""
FAKE_OPENSSL = """\
import sys
from pathlib import Path
a = sys.argv[1:]
if a[0] == "rand":
    print({secret!r})
elif a[0] == "req":
    Path(a[a.index("-keyout") + 1]).write_text("key")
    Path(a[a.index("-out") + 1]).write_text("cert")
elif a[0] == "pkcs12":
    assert Path(a[a.index("-passout") + 1].split(":", 1)[1]).read_text() == {secret!r}
    Path(a[a.index("-out") + 1]).write_text("p12")
else:
    sys.exit("unexpected openssl " + a[0])
"""
FAKE_RCODESIGN = """\
import sys
from pathlib import Path
a = sys.argv[1:]
if a[0] == "sign":
    if "--p12-file" in a:
        assert Path(a[a.index("--p12-file") + 1]).is_file() and Path(a[a.index("--entitlements-xml-file") + 1]).is_file()
    (Path(a[-1]) / "_CodeSignature").mkdir(parents=True, exist_ok=True)
    (Path(a[-1]) / "_CodeSignature" / "CodeResources").write_text("x")
elif a[0] == "verify":
    assert Path(a[-1]).is_file(), a
elif a[0] == "print-signature-info":
    print("identifier: org.example.smoke")
"""
FAKE_DN_READER = """\
import sys, zipfile
from pathlib import Path
dn = zipfile.ZipFile(sys.argv[-1]).read("META-INF/FAKE-DN").decode()
if Path(sys.argv[0]).name == "apksigner":
    assert sys.argv[1:3] == ["verify", "--print-certs"], sys.argv
    print("Signer #1 certificate DN: " + dn)
else:
    print("jar verified.\\n  X.509, " + dn)
"""
FAKE_ADMIT = """\
import os, sys
print("admit", *sys.argv[1:])
sys.exit(1 if os.environ.get("FAKE_ADMIT") == "fail" else 0)
"""
FAKE_ISOLATION = """\
import os, sys
from pathlib import Path
n = Path(os.environ["FAKE_ROOT"]) / "isolation-calls"
calls = int(n.read_text()) + 1 if n.exists() else 1
n.write_text(str(calls))
if str(calls) in os.environ.get("FAKE_ISOLATION_FAIL", "").split(","):
    print("network: reachable tcp 1.1.1.1/443 (fail)")
    sys.exit(1)
print("isolation ok")
"""


def install(path, body):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(f"#!{sys.executable}\n" + textwrap.dedent(body))
    path.chmod(path.stat().st_mode | stat.S_IXUSR)


class RunSmokeTest(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        b = self.tmp / "bin"
        install(b / "godot", FAKE_GODOT.format(tests=str(HERE)))
        install(b / "godot-set", FAKE_GODOT_SET)
        install(b / "keytool", FAKE_KEYTOOL)
        install(b / "openssl", FAKE_OPENSSL.format(secret=SECRET))
        install(b / "rcodesign", FAKE_RCODESIGN)
        install(b / "jarsigner", FAKE_DN_READER)
        install(b / "apple-sdk-admit", FAKE_ADMIT)
        install(b / "isolation-check", FAKE_ISOLATION)
        install(b / "xcrun", FAKE_XCRUN)
        install(b / "apple-bundle-util", FAKE_BUNDLE_UTIL)
        install(self.tmp / "android" / "build-tools" / "36.1.0" / "apksigner", FAKE_DN_READER)
        # The developer's own env may hold secret-like names; the image env holds only the public debug password.
        base = {k: v for k, v in os.environ.items()
                if not re.search(r"PASSWORD|SECRET|TOKEN|KEY_CONTENT|KEYSTORE_BASE64|P12", k) and k != "UE_HOME"}
        base["GODOT_ANDROID_KEYSTORE_DEBUG_PASSWORD"] = "android"
        self.env = dict(base, PATH=f"{b}{os.pathsep}{os.environ['PATH']}", FAKE_ROOT=str(self.tmp),
                        FAKE_SETS="official-4.7.2,fork-c064", ANDROID_HOME=str(self.tmp / "android"),
                        ANDROID_BUILD_TOOLS_VERSION="36.1.0", JAVA_HOME=str(self.tmp / "jdk"),
                        GODOT_SETS_ROOT=str(self.tmp / "no-sets"), SMOKE_SKIP_ISOLATION_PROBE="1")
        self.work = self.tmp / "work"

    def tearDown(self):
        self._tmp.cleanup()

    def run_smoke(self, *args, **env):
        r = subprocess.run(["bash", str(SMOKE / "run_smoke.sh"), "--mode", "offline", "--work", str(self.work),
                            *args], capture_output=True, text=True, env=dict(self.env, **env), timeout=300)
        rep = self.work / "smoke-report.json"
        return r, json.loads(rep.read_text()) if rep.exists() else None

    def assertNoSecret(self, r):
        self.assertNotIn(SECRET, r.stdout + r.stderr)
        for path in self.work.rglob("*"):
            if path.is_file():
                self.assertNotIn(SECRET.encode(), path.read_bytes(), path)
        self.assertFalse((self.work / "keys").exists(), "ephemeral keys are deleted")

    def test_all_sets_pass_without_apple(self):
        r, rep = self.run_smoke("--allow-no-apple-smoke")
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertEqual(r.stdout.rstrip().splitlines()[-1], "SMOKE RESULT: PASS promotable=false")
        self.assertEqual((rep["schema"], rep["network_isolated"], rep["promotable"]), (1, False, False))
        self.assertEqual(rep["apple"]["status"], "skip")
        self.assertEqual(sorted(rep["sets"]), ["fork-c064", "official-4.7.2"])
        self.assertIn("visionos", rep["sets"]["fork-c064"])
        self.assertNotIn("visionos", rep["sets"]["official-4.7.2"])
        for sid, presets in rep["sets"].items():
            self.assertEqual(len(presets), 10 if sid.startswith("fork") else 9)
            for name, res in presets.items():
                self.assertEqual(res["status"], "pass", f"{sid}/{name}: {res['detail']}")
                self.assertRegex(res["artifact_sha256"], r"^[0-9a-f]{64}$", f"{sid}/{name}")
            self.assertIn("link smoke skipped", presets["ios"]["detail"])
        cfg = self.work / "sets" / "fork-c064" / "config" / "godot"
        self.assertIn(str(self.tmp / "android"), (cfg / "editor_settings-4.8.tres").read_text())
        self.assertTrue((self.work / "sets" / "fork-c064" / "logs" / "visionos.log").is_file())
        self.assertEqual(rep["hygiene"]["secret-env"]["status"], "pass")
        self.assertEqual(rep["hygiene"]["verify-installed"]["status"], "skip")  # UE_HOME unset: not in the image
        self.assertEqual(rep["hygiene"]["isolation-check"]["status"], "pass")
        self.assertEqual((self.tmp / "isolation-calls").read_text(), "2")  # before and after admission
        self.assertNoSecret(r)

    def test_isolation_check_is_mandatory(self):
        cases = {"before": dict(FAKE_ISOLATION_FAIL="1"), "after": dict(FAKE_ISOLATION_FAIL="2")}
        for name, env in cases.items():
            with self.subTest(name):
                (self.tmp / "isolation-calls").unlink(missing_ok=True)
                shutil.rmtree(self.work, ignore_errors=True)
                r, rep = self.run_smoke("--allow-no-apple-smoke", "--sets", "official-4.7.2", **env)
                self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
                row = rep["hygiene"]["isolation-check"]
                self.assertEqual(row["status"], "fail")
                self.assertIn(f"{name}-admission", row["detail"])
                self.assertIn("reachable", row["detail"])
        with self.subTest("missing"):
            shutil.rmtree(self.work, ignore_errors=True)
            (self.tmp / "bin" / "isolation-check").unlink()
            r, rep = self.run_smoke("--allow-no-apple-smoke", "--sets", "official-4.7.2")
            self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
            self.assertEqual(rep["hygiene"]["isolation-check"]["status"], "fail")
            self.assertIn("not on PATH", rep["hygiene"]["isolation-check"]["detail"])

    def test_isolation_check_reruns_after_admission(self):
        pack = self.tmp / "pack.tar.zst"
        pack.write_bytes(b"x")
        r, rep = self.run_smoke("--apple-pack", str(pack), "--apple-build", "27A9275", "--sets", "official-4.7.2",
                                FAKE_ISOLATION_FAIL="2")
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("after-admission", rep["hygiene"]["isolation-check"]["detail"])
        self.assertTrue((self.work / "logs" / "isolation-check-after-admission.log").is_file())

    def test_secret_like_env_fails(self):
        r, rep = self.run_smoke("--allow-no-apple-smoke", "--sets", "official-4.7.2", MY_API_TOKEN="hunter2")
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertEqual(rep["hygiene"]["secret-env"]["status"], "fail")
        self.assertIn("MY_API_TOKEN", rep["hygiene"]["secret-env"]["detail"])
        self.assertNotIn("GODOT_ANDROID_KEYSTORE_DEBUG_PASSWORD", rep["hygiene"]["secret-env"]["detail"])
        self.assertNotIn("hunter2", r.stdout + r.stderr + json.dumps(rep))

    def test_failures_are_reported_and_the_run_continues(self):
        r, rep = self.run_smoke("--allow-no-apple-smoke", "--sets", "official-4.7.2,nope-9",
                                FAKE_FAIL="official-4.7.2/web-nothreads")
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertEqual(r.stdout.rstrip().splitlines()[-1], "SMOKE RESULT: FAIL")
        official = rep["sets"]["official-4.7.2"]
        self.assertEqual(official["web-nothreads"]["status"], "fail")
        self.assertIn("SCRIPT ERROR", official["web-nothreads"]["detail"])
        self.assertEqual({k for k, v in official.items() if v["status"] != "pass"}, {"web-nothreads"})
        self.assertEqual(rep["sets"]["nope-9"]["setup"]["status"], "fail")
        self.assertFalse(rep["promotable"])
        self.assertNoSecret(r)

    def test_apple_pack_links_ios(self):
        pack = self.tmp / "pack.tar.zst"
        pack.write_bytes(b"x")
        r, rep = self.run_smoke("--apple-pack", str(pack), "--apple-build", "27A9275", "--sets", "official-4.7.2")
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)  # isolation probe skipped -> never promotable
        self.assertEqual(rep["apple"], {"status": "pass", "build": "27A9275", "detail": rep["apple"]["detail"]})
        ios = rep["sets"]["official-4.7.2"]["ios"]
        self.assertEqual(ios["status"], "pass", ios["detail"])
        self.assertIn("linked", ios["detail"])
        self.assertTrue((self.work / "logs" / "apple-sdk-admit.log").is_file())

    def test_apple_admission_failure(self):
        pack = self.tmp / "pack.tar.zst"
        pack.write_bytes(b"x")
        r, rep = self.run_smoke("--apple-pack", str(pack), "--apple-build", "27A9275", "--sets", "official-4.7.2",
                                FAKE_ADMIT="fail")
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertEqual(rep["apple"]["status"], "fail")
        self.assertEqual(rep["sets"]["official-4.7.2"]["ios"]["status"], "fail")
        self.assertEqual(rep["sets"]["official-4.7.2"]["linux-x86_64"]["status"], "pass")

    def test_logs_are_kept_next_to_an_outside_report(self):
        out = self.tmp / "out"
        out.mkdir()
        r, _ = self.run_smoke("--allow-no-apple-smoke", "--sets", "official-4.7.2", "--report", str(out / "r.json"))
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertTrue((out / "r.json").is_file())
        with tarfile.open(out / "r-logs.tar.gz") as tar:
            names = set(tar.getnames())
            blob = b"".join(tar.extractfile(m).read() for m in tar.getmembers() if m.isfile())
        self.assertIn("results.tsv", names)
        self.assertIn("sets/official-4.7.2/logs/linux-x86_64.log", names)
        self.assertNotIn(SECRET.encode(), blob)

    def test_log_archive_failure_fails_the_run(self):
        out = self.tmp / "out"
        out.mkdir()
        install(self.tmp / "bin" / "tar", "import sys\nsys.exit(2)\n")
        r, rep = self.run_smoke("--allow-no-apple-smoke", "--sets", "official-4.7.2", "--report", str(out / "r.json"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        rep = json.loads((out / "r.json").read_text())
        self.assertEqual(rep["hygiene"]["log-archive"]["status"], "fail")
        self.assertFalse((out / "r-logs.tar.gz").exists())

    def test_work_dir_must_be_empty(self):
        self.work.mkdir()
        (self.work / "old").write_text("x")
        r, _ = self.run_smoke("--allow-no-apple-smoke")
        self.assertEqual(r.returncode, 64)


class ReportTest(unittest.TestCase):
    def rows(self, *statuses):
        return [("s", f"p{i}", st, "d", "0" * 64) for i, st in enumerate(statuses)] + \
            [("s", "ios", "pass", "linked", "1" * 64)]

    def test_promotable_only_when_isolated_and_apple_passed(self):
        rep, code, line = report.build(self.rows("pass"), isolated=True, apple_build="B", apple_admitted=True)
        self.assertEqual((rep["promotable"], code, line), (True, 0, "SMOKE RESULT: PASS promotable=true"))
        self.assertEqual(rep["apple"]["status"], "pass")
        rep, code, _ = report.build(self.rows("pass"), isolated=False, apple_build="B", apple_admitted=True)
        self.assertEqual((rep["promotable"], code), (False, 2))
        rep, code, _ = report.build(self.rows("pass", "skip"), isolated=True, apple_build=None, apple_admitted=False)
        self.assertEqual((rep["apple"]["status"], rep["promotable"], code), ("skip", False, 2))

    def test_failures(self):
        rep, code, line = report.build(self.rows("pass", "fail"), isolated=True, apple_build="B", apple_admitted=True)
        self.assertEqual((rep["promotable"], code, line), (False, 1, "SMOKE RESULT: FAIL"))
        rep, code, _ = report.build(self.rows("pass"), isolated=True, apple_build="B", apple_admitted=False)
        self.assertEqual((rep["apple"]["status"], code), ("fail", 1))
        rep, code, _ = report.build([], isolated=True, apple_build=None, apple_admitted=False)
        self.assertEqual(code, 1, "nothing tested is a failure")
        no_link = [("s", "linux-x86_64", "pass", "", "0" * 64)]
        rep, code, _ = report.build(no_link, isolated=True, apple_build="B", apple_admitted=True)
        self.assertEqual((rep["apple"]["status"], code), ("fail", 1), "an admitted SDK must link something")

    def test_image_rows_are_hygiene(self):
        rows = self.rows("pass") + [("_image", "secret-env", "pass", "", ""), ("_image", "verify-installed", "skip", "", "")]
        rep, code, _ = report.build(rows, isolated=True, apple_build="B", apple_admitted=True)
        self.assertNotIn("_image", rep["sets"])
        self.assertEqual(rep["hygiene"]["secret-env"], {"status": "pass", "detail": ""})
        self.assertEqual(code, 0)
        rows[-2] = ("_image", "secret-env", "fail", "MY_TOKEN", "")
        rep, code, line = report.build(rows, isolated=True, apple_build="B", apple_admitted=True)
        self.assertEqual((rep["promotable"], code, line), (False, 1, "SMOKE RESULT: FAIL"))
        only_hygiene = [("_image", "secret-env", "pass", "", "")]
        self.assertEqual(report.build(only_hygiene, isolated=True, apple_build=None, apple_admitted=False)[1], 1)


FAKE_LOCKCTL = """\
import os, sys
a = sys.argv[1:]
if a == ["gradle-dist-dir"]:
    print(os.environ["FAKE_ROOT"] + "/gradle")
    sys.exit(0)
values = {"inputs.gradle.version": "9.0", "android.plugin_prewarm.compile_sdk": "36",
          "android.plugin_prewarm.min_sdk": "24", "android.plugin_prewarm.agp": "8.0.0",
          "android.plugin_prewarm.kotlin": "2.0.0", "android.build_tools": "36.1.0",
          "android.maven_prewarm": "androidx.core:core:1.0.0"}
assert a[:2] == ["get", "--raw"], a
print(values[a[2]])
"""
FAKE_GRADLE = """\
import os, shutil, sys
from pathlib import Path
sdk = Path(os.environ["ANDROID_HOME"])
if "assembleRelease" in sys.argv:
    change = os.environ.get("FAKE_SDK_CHANGE")
    if change == "add":
        (sdk / "ndk" / "27.0.1").mkdir(parents=True)
    elif change == "remove":
        shutil.rmtree(sdk / "build-tools" / "36.1.0")
"""
# GNU find's -printf (the Godot-set fingerprint) is missing from BSD find.
FAKE_FIND = """\
import os, subprocess, sys
a = sys.argv[1:]
if "-printf" not in a:
    sys.exit(subprocess.call(["/usr/bin/find", *a]) if os.path.exists("/usr/bin/find") else 127)
for top, dirs, files in os.walk(a[0]):
    for n in sorted(dirs + files):
        print(os.path.join(top, n))
"""


class PrewarmTest(unittest.TestCase):
    def setUp(self):
        RunSmokeTest.setUp(self)
        b = self.tmp / "bin"
        install(self.tmp / "ue" / "lib" / "lockctl.py", FAKE_LOCKCTL)
        install(self.tmp / "gradle" / "gradle-9.0" / "bin" / "gradle", FAKE_GRADLE)
        if subprocess.run(["find", "/dev/null", "-printf", ""], capture_output=True).returncode:
            install(b / "find", FAKE_FIND)
        (self.tmp / "no-sets").mkdir()  # every watched dir exists in the image
        self.env.update(FAKE_SETS="official-4.7.2", UE_HOME=str(self.tmp / "ue"),
                        GRADLE_USER_HOME=str(self.tmp / "gradle-home"), TMPDIR=str(self.tmp))

    def tearDown(self):
        RunSmokeTest.tearDown(self)

    def prewarm(self, **env):
        return subprocess.run(["bash", str(SMOKE / "android_prewarm.sh")], capture_output=True, text=True,
                              env=dict(self.env, **env), timeout=300)

    def test_unchanged_sdk_passes(self):
        r = self.prewarm()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("done:", r.stderr + r.stdout)

    def test_sdk_package_set_change_fails(self):
        for change, sign in (("add", "> "), ("remove", "< ")):
            with self.subTest(change):
                r = self.prewarm(FAKE_SDK_CHANGE=change)
                self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
                self.assertIn("changed the Android SDK package set", r.stderr)
                self.assertIn(sign, r.stderr)


LOCK_PATH = HERE.parent / "toolchain.lock.json"


def full_report(lock, apple_build=None):
    sets = {sid: {pr: {"status": "pass", "detail": "ok", "artifact_sha256": "0" * 64}
                  for pr in spec["smoke_presets"]}
            for sid, spec in lock["godot_sets"].items()}
    hygiene = {n: {"status": "pass", "detail": "ok"} for n in report.REQUIRED_HYGIENE}
    apple = ({"status": "pass", "build": apple_build, "detail": "ok"} if apple_build
             else {"status": "skip", "build": None, "detail": "no pack"})
    return {"schema": 1, "network_isolated": True, "sets": sets, "apple": apple,
            "hygiene": hygiene, "promotable": bool(apple_build)}


class ValidateTest(unittest.TestCase):
    lock = json.loads(LOCK_PATH.read_text())

    def test_full_reports_are_valid(self):
        self.assertEqual(report.validate(full_report(self.lock, "27A9275"), self.lock, "27A9275"), [])
        self.assertEqual(report.validate(full_report(self.lock), self.lock, None), [])

    def test_mutations_are_rejected(self):
        sid = sorted(self.lock["godot_sets"])[0]
        preset = self.lock["godot_sets"][sid]["smoke_presets"][0]

        def drop_set(r): del r["sets"][sid]
        def drop_preset(r): del r["sets"][sid][preset]
        def fail_preset(r): r["sets"][sid][preset]["status"] = "fail"
        def skip_preset(r): r["sets"][sid][preset]["status"] = "skip"
        def extra_failing(r): r["sets"][sid]["bogus"] = {"status": "fail"}
        def unknown_set(r): r["sets"]["nope"] = {}
        def drop_hygiene(r): del r["hygiene"]["isolation-check"]
        def fail_hygiene(r): r["hygiene"]["secret-env"]["status"] = "fail"
        def extra_hygiene(r): r["hygiene"]["other"] = {"status": "fail"}
        def schema2(r): r["schema"] = 2
        def schema_true(r): r["schema"] = True
        def not_isolated(r): r["network_isolated"] = False
        def wrong_build(r): r["apple"]["build"] = "27A0000"
        def apple_fail(r): r["apple"]["status"] = "fail"
        def not_promotable(r): r["promotable"] = False
        def no_apple(r): del r["apple"]
        def no_sets(r): del r["sets"]

        for mut in (drop_set, drop_preset, fail_preset, skip_preset, extra_failing, unknown_set,
                    drop_hygiene, fail_hygiene, extra_hygiene, schema2, schema_true, not_isolated,
                    wrong_build, apple_fail, not_promotable, no_apple, no_sets):
            with self.subTest(mut.__name__):
                r = full_report(self.lock, "27A9275")
                mut(r)
                self.assertNotEqual(report.validate(r, self.lock, "27A9275"), [])
        self.assertNotEqual(report.validate([], self.lock, None), [])

    def test_no_pack_rejects_apple_or_promotable(self):
        r = full_report(self.lock)
        r["promotable"] = True
        self.assertNotEqual(report.validate(r, self.lock, None), [])
        self.assertNotEqual(report.validate(full_report(self.lock, "27A9275"), self.lock, None), [])
        self.assertNotEqual(report.validate(full_report(self.lock), self.lock, "27A9275"), [])

    def test_cli_exit_codes(self):
        with tempfile.TemporaryDirectory() as d:
            d = Path(d)
            good, bad, broken = d / "good.json", d / "bad.json", d / "broken.json"
            good.write_text(json.dumps(full_report(self.lock, "27A9275")))
            r = full_report(self.lock, "27A9275"); r["hygiene"]["ephemeral-keys"]["status"] = "fail"
            bad.write_text(json.dumps(r))
            broken.write_text("{not json")

            def run(*args):
                return subprocess.run([sys.executable, str(SMOKE / "report.py"), "validate",
                                       "--lock", str(LOCK_PATH), *args],
                                      capture_output=True, text=True).returncode
            self.assertEqual(run("--report", str(good), "--apple-build", "27A9275"), 0)
            self.assertEqual(run("--report", str(bad), "--apple-build", "27A9275"), 1)
            self.assertEqual(run("--report", str(broken), "--apple-build", "27A9275"), 1)
            self.assertEqual(run("--report", str(d / "missing.json"), "--apple-build", "27A9275"), 1)
            self.assertEqual(run("--report", str(good), "--no-apple-pack"), 1)
            self.assertEqual(run("--report", str(good)), 64)


if __name__ == "__main__":
    unittest.main()
