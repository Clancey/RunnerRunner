"""Local-only reproductions. All SSH, rsync, Docker, df and timeout calls are faked.

Assertions registered before running against 25beac18:
* Env-only root override is rejected, with no SSH.
* A symlinked ctx cannot let rsync --delete remove an outside sentinel.
* Unapproved running containers block build; admission is repeated for smoke.
* 90 GiB free fails the default first-run admission.
* Build/smoke disk drops or monitor errors stop the owned client, preserve the
  tmp tag and both UNKNOWN locks, and never run the next phase.

UE_SAFETY_BASELINE=25beac18 replaces only the copied build.sh with that revision
to run these same assertions red, without changing the checkout.
"""
import os
import hashlib
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


UE = Path(__file__).resolve().parents[1]
ROOT = "/mnt/user/appdata/rr-builds/universal-export"
GIB = 1024**3

# These are intentionally strict allow-list shims: an unexpected operation
# cannot fall through to a real binary. SSH executes only in the sandbox.
SHIM = r'''#!/usr/bin/env python3
import json, os, pathlib, shutil, signal, subprocess, sys, time
p = pathlib.Path
base = p(os.environ["UE_TEST_DIR"])
cmd, args = p(sys.argv[0]).name, sys.argv[1:]
root = str(base / "remote")
def log(s):
    with (base / "calls").open("a") as f: f.write(s + "\n")
def die(s): sys.exit("unexpected shim command: " + s)
def image(ref): return base / "images" / ref
log(cmd + " " + " ".join(args))
scenario = os.environ.get("UE_TEST_SCENARIO", "")
if cmd == "ssh":
    while args and args[0].startswith("-"):
        if args[0] in ("-o", "-p", "-i", "-l"): args = args[2:]
        else: args = args[1:]
    if not args or args.pop(0) != "shim": die("non-shim host")
    text = " ".join(args).replace("/mnt/user/appdata/rr-builds/universal-export", root)
    if scenario == "guard-transport-error" and "paths" in text: sys.exit(255)
    if scenario == "presync-symlink" and "printf" in text and "/owner" in text:
        (p(root) / "ctx").rmdir()
        (p(root) / "ctx").symlink_to(base / "outside")
    if "cas.sh" in text:
        if scenario.startswith("real-cas"):
            (base / "cas-ran").touch()
            # Run the actual local CAS against one pinned, local-only input.
            # No production plan or network sources are available to this run.
            os.execv("/bin/bash", ["bash", str(base / "ue" / "cas.sh"),
                     "--plan", str(base / "cas-plan.tsv"), "--cache", root + "/cache",
                     "--ctx", root + "/ctx", "--offline"])
        sys.exit(0)
    os.execv("/bin/bash", ["bash", "-c", text])
elif cmd == "rsync":
    # Model the dangerous destination-following deletion, only inside sandbox.
    dest = args[-1].removeprefix("shim:").replace("/mnt/user/appdata/rr-builds/universal-export", root)
    resolved = p(dest).resolve()
    if not resolved.is_relative_to(base.resolve()): die("rsync outside sandbox")
    (base / "synced").touch()
    sentinel = resolved / "victim"
    if sentinel.exists(): sentinel.unlink()
    if scenario == "real-cas-late-inputs":
        (p(root) / "ctx" / "inputs").symlink_to(base / "outside")
elif cmd == "realpath":
    if scenario == "path-error": sys.exit(1)
    value = str(p(args[-1]).resolve())
    print(value + ("/wrong" if scenario == "path-mismatch" else ""))
elif cmd == "timeout":
    # Bounded by the outer Python test timeout; never executes a real remote tool.
    if args[0] == "-k": args = args[2:]
    args = args[1:]
    os.execvp(args[0], args)
elif cmd == "df":
    if args[0] == "-Pk":
        print("Filesystem 1024-blocks Used Available Capacity Mounted")
        print("fake 419430400 209715200 209715200 50% /fake")
        sys.exit(0)
    phase = (base / "phase").read_text() if (base / "phase").exists() else ""
    if scenario == phase + "-probe-error": sys.exit(1)
    if scenario == phase + "-malformed": print("bad data"); sys.exit(0)
    if scenario == "disk-malformed": print("Avail Size\n12oops 400000000000"); sys.exit(0)
    if scenario == "disk-negative": print("Avail Size\n-1 400000000000"); sys.exit(0)
    if scenario == "disk-empty": sys.exit(0)
    avail = 90 * 1024**3 if scenario == "startup-low" else 200 * 1024**3
    if scenario == phase + "-drop": avail = 19 * 1024**3
    done = base / ("built" if phase == "build" else "smoke-completed")
    if scenario == phase + "-final-drop" and done.exists(): avail = 19 * 1024**3
    if scenario == phase + "-pct-drop": avail = 30 * 1024**3
    if scenario == "build-delayed-drop" and phase == "build":
        counter = base / "runtime-samples"
        count = int(counter.read_text()) + 1 if counter.exists() else 1
        counter.write_text(str(count))
        if count >= 3: avail = 19 * 1024**3
    print("Avail Size")
    print(avail, 400 * 1024**3)
elif cmd == "docker":
    if args[0] == "ps":
        if scenario == "exclusive-error": sys.exit(1)
        busy = scenario == "busy" or (scenario == "smoke-busy" and (base / "receipt-written").exists())
        if busy or scenario.startswith("service-"): print("a" * 64)
    elif args[0] == "inspect":
        if scenario == "service-inspect-error": sys.exit(1)
        project = "approved"
        service = "server"
        oneoff = "False"
        job = "-"
        if scenario == "service-wrong-project": project = "other"
        if scenario == "service-generic": service = "web"
        if scenario == "service-oneoff": oneoff = "True"
        if scenario == "service-job": job = "job"
        if scenario == "service-unlabelled": project = service = "-"
        if scenario == "service-postgres": service = "postgres"
        if scenario == "service-worker": service = "host-worker"
        print("|".join(["a"*64, "true", project, service, oneoff, job]))
    elif args[:2] == ["info", "--format"]:
        if scenario == "disk-info-error": sys.exit(1)
        if scenario == "disk-info-malformed": print("not-an-absolute-root"); sys.exit(0)
        print("/fake/docker")
    elif args[0] == "build":
        (base / "build-pid").write_text(str(os.getpid()))
        if scenario == "receipt-symlink":
            (base / "remote" / "receipts" / "ue-safety.json").symlink_to(base / "victim")
        image(args[args.index("-t") + 1]).touch()
        (base / "phase").write_text("build")
        if scenario == "build-hang":
            # TERM-ignoring *own* CLI tests the bounded KILL escalation.
            signal.signal(signal.SIGTERM, signal.SIG_IGN)
            time.sleep(20)
        elif scenario == "build-delayed-drop": time.sleep(10)
        elif scenario.startswith("build-") and scenario != "build-final-drop": time.sleep(3)
        (base / "built").touch()
    elif args[:2] == ["image", "inspect"]:
        sys.exit(0 if image(args[-1]).exists() else 1)
    elif args[0] == "tag": image(args[2]).touch()
    elif args[0] == "rmi": image(args[1]).unlink()
    elif args[0] == "run":
        if "cat" in args:
            (base / "receipt-written").touch()
            if scenario == "smoke-symlink":
                (base / "remote" / "smoke" / "ue-safety").symlink_to(base / "outside")
            print('{"receipt": "shim"}')
        elif "/opt/universal-export/smoke/run_smoke.sh" in args:
            (base / "smoke-pid").write_text(str(os.getpid()))
            (base / "smoked").touch()
            (base / "phase").write_text("smoke")
            if scenario.startswith("smoke-") and scenario != "smoke-final-drop": time.sleep(3)
            (base / "smoke-completed").touch()
            sys.exit(2)
        else: die("docker run")
    else: die("docker " + " ".join(args))
else: die(cmd)
'''


class Harness(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.base = Path(self.tmp.name).resolve()
        self.ue = self.base / "ue"
        shutil.copytree(UE, self.ue, ignore=shutil.ignore_patterns(
            "tests", "logs", ".build-lock", ".plan.tsv", "__pycache__"))
        baseline = os.environ.get("UE_SAFETY_BASELINE")
        if baseline:
            old = subprocess.check_output(
                ["git", "show", f"{baseline}:images/universal-export/build.sh"], cwd=UE)
            (self.ue / "build.sh").write_bytes(old)
        self.shim = self.base / "shim"
        self.shim.mkdir()
        for name in ("ssh", "docker", "rsync", "df", "realpath", "timeout", "curl"):
            path = self.shim / name
            path.write_text(SHIM)
            path.chmod(0o755)
        (self.base / "images").mkdir()
        (self.base / "outside").mkdir()
        (self.base / "victim").write_text("keep")

    def run_build(self, scenario="", args=(), extra_env=None):
        env = os.environ.copy()
        for key in ("UE_REMOTE_ROOT", "BUILD_PROJECTED_BYTES", "BUILD_START_MIN_BYTES",
                    "BUILD_TAIL_MIN_BYTES", "BUILD_TAIL_PCT"):
            env.pop(key, None)
        env.update(PATH=f"{self.shim}:{env['PATH']}", UE_TEST_DIR=str(self.base),
                   UE_TEST_SCENARIO=scenario, BUILD_TIMEOUT_SECONDS="0",
                   SMOKE_TIMEOUT_SECONDS="0", BUILD_WATCHDOG_SECONDS="1",
                   BUILD_LOCK_WAIT_SECONDS="0")
        env.update(extra_env or {})
        result = subprocess.run(
            ["/bin/bash", str(self.ue / "build.sh"), "--host", "shim",
             "--tag", "ue-safety", "--allow-no-apple-smoke", *args],
            env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            timeout=30)
        self.output = result.stdout
        self.assertNotEqual(result.returncode, 0, self.output)
        return result

    def assert_not_built(self):
        self.assertFalse((self.base / "built").exists(), self.output)

    def assert_unknown(self, phase, completed=False):
        self.assertIn("STOP UNKNOWN", self.output)
        for lock in (self.ue / ".build-lock", self.base / "remote" / ".build-lock"):
            self.assertTrue((lock / "unknown").is_file(), self.output)
        self.assertTrue(list((self.base / "images").glob("*:ue-tmp-*")), self.output)
        done = self.base / ("built" if phase == "build" else "smoke-completed")
        self.assertEqual(done.exists(), completed, self.output)
        # The supervisor must have reaped its specific Docker CLI process.
        with self.assertRaises(ProcessLookupError):
            os.kill(int((self.base / f"{phase}-pid").read_text()), 0)
        if phase == "build":
            self.assertFalse((self.base / "smoked").exists(), self.output)


class SafetyTests(Harness):
    def test_env_override_requires_flag(self):
        self.run_build(args=("--dry-run",), extra_env={"UE_REMOTE_ROOT": str(self.base / "alt")})
        self.assertFalse((self.base / "calls").exists())

    def test_ctx_symlink_cannot_delete_outside_sentinel(self):
        remote = self.base / "remote"
        remote.mkdir()
        (remote / "ctx").symlink_to(self.base / "outside")
        victim = self.base / "outside" / "victim"
        victim.write_text("keep")
        self.run_build()
        self.assertTrue(victim.exists(), "rsync followed ctx symlink and deleted victim\n" + self.output)
        self.assertFalse((self.base / "synced").exists(), self.output)

    def test_busy_container_blocks_build(self):
        self.run_build("busy")
        self.assert_not_built()

    def test_busy_container_blocks_smoke(self):
        self.run_build("smoke-busy")
        self.assertTrue((self.base / "built").exists(), self.output)
        self.assertFalse((self.base / "smoked").exists(), self.output)

    def test_default_startup_requires_100_gib(self):
        self.run_build("startup-low")
        self.assert_not_built()

    def test_build_drop_is_unknown(self):
        self.run_build("build-drop")
        self.assert_unknown("build")

    def test_smoke_drop_is_unknown(self):
        self.run_build("smoke-drop")
        self.assert_unknown("smoke")

    def test_build_monitor_error_is_unknown(self):
        self.run_build("build-probe-error")
        self.assert_unknown("build")

    def test_smoke_monitor_error_is_unknown(self):
        self.run_build("smoke-probe-error")
        self.assert_unknown("smoke")

    def test_receipt_symlink_is_rechecked(self):
        self.run_build("receipt-symlink")
        self.assertEqual((self.base / "victim").read_text(), "keep", self.output)

    def test_smoke_symlink_is_rechecked(self):
        self.run_build("smoke-symlink")
        self.assertFalse((self.base / "smoked").exists(), self.output)


class EdgeTests(Harness):
    def test_explicitly_empty_env_root_is_not_the_default(self):
        result = self.run_build(args=("--dry-run",), extra_env={"UE_REMOTE_ROOT": ""})
        self.assertEqual(result.returncode, 64, self.output)
        self.assertFalse((self.base / "calls").exists())

    def test_explicit_canonical_alternative_dry_run_has_no_remote_calls(self):
        env = os.environ.copy()
        env.update(PATH=f"{self.shim}:{env['PATH']}", UE_TEST_DIR=str(self.base),
                   UE_REMOTE_ROOT="/bad/env/override")
        result = subprocess.run(
            ["/bin/bash", str(self.ue / "build.sh"), "--dry-run",
             "--remote-root", "/srv/private/builds/universal-export"],
            env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=10)
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn("/srv/private/builds/universal-export/ctx", result.stdout)
        self.assertFalse((self.base / "calls").exists())

    def test_unsafe_root_arguments_rejected_locally(self):
        for root in ("/", "/mnt/user", "/srv/build", "relative/path", "/a/b/c/../d",
                     "/a/b/c//d", "/a/b/c/d/", "/a/b/c/d;touch", "/a/b/c/d\nx"):
            with self.subTest(root=root):
                result = self.run_build(args=("--dry-run", "--remote-root", root))
                self.assertEqual(result.returncode, 64, self.output)
                self.assertFalse((self.base / "calls").exists())

    def test_unsafe_thresholds_rejected_locally(self):
        for key, value in (("BUILD_START_MIN_BYTES", str(90*GIB)),
                           ("BUILD_TAIL_MIN_BYTES", "0"), ("BUILD_TAIL_PCT", "9"),
                           ("BUILD_TAIL_PCT", "101"), ("BUILD_WATCHDOG_SECONDS", "0"),
                           ("BUILD_WATCHDOG_SECONDS", "31"), ("BUILD_PROJECTED_BYTES", "-1"),
                           ("BUILD_PROJECTED_BYTES", "08"), ("BUILD_PROJECTED_BYTES", "1oops")):
            with self.subTest(key=key, value=value):
                result = self.run_build(args=("--dry-run",), extra_env={key: value})
                self.assertEqual(result.returncode, 64, self.output)
                self.assertFalse((self.base / "calls").exists())

    def test_all_remote_directories_reject_symlinks(self):
        remote = self.base / "remote"
        for name in ("ctx", "cache", "smoke", "receipts", ".build-lock",
                     "ctx/inputs", "cache/sha256", "cache/tmp", "cache/quarantine"):
            with self.subTest(name=name):
                if remote.exists(): shutil.rmtree(remote)
                shutil.rmtree(self.base / "images")
                (self.base / "images").mkdir()
                (self.base / "synced").unlink(missing_ok=True)
                link = remote / name
                link.parent.mkdir(parents=True, exist_ok=True)
                link.symlink_to(self.base / "outside")
                self.run_build()
                self.assertIn("symlink in remote path", self.output)
                self.assertFalse((self.base / "synced").exists(), self.output)
                link.unlink()

    def test_root_ancestor_symlink_rejected(self):
        (self.base / "remote").symlink_to(self.base / "outside")
        self.run_build()
        self.assertIn("symlink in remote path", self.output)
        self.assertFalse((self.base / "synced").exists(), self.output)

    def test_dangling_symlink_rejected(self):
        (self.base / "remote").mkdir()
        (self.base / "remote" / "ctx").symlink_to(self.base / "missing")
        self.run_build()
        self.assertIn("symlink in remote path", self.output)

    def test_guard_repeated_immediately_before_rsync(self):
        self.run_build("presync-symlink")
        self.assertIn("before rsync", self.output)
        self.assertFalse((self.base / "synced").exists(), self.output)

    def test_realpath_errors_mismatch_and_transport_fail_closed(self):
        for scenario in ("path-error", "path-mismatch", "guard-transport-error"):
            with self.subTest(scenario=scenario):
                self.run_build(scenario)
                self.assertFalse((self.base / "synced").exists(), self.output)
                self.assert_not_built()

    def test_disk_and_exclusivity_probe_failures_block_build(self):
        for scenario in ("disk-info-error", "disk-malformed", "disk-negative",
                         "disk-empty", "exclusive-error"):
            with self.subTest(scenario=scenario):
                self.run_build(scenario)
                self.assert_not_built()

    def test_zero_projection_does_not_disable_admission(self):
        self.run_build("startup-low", extra_env={"BUILD_PROJECTED_BYTES": "0"})
        self.assert_not_built()

    def test_approved_services_reach_smoke(self):
        for scenario, service in (("service-valid", "server"), ("service-postgres", "postgres"),
                                  ("service-worker", "host-worker")):
            with self.subTest(service=service):
                # Reset only the private fake host between completed attempts.
                for path in (self.base / "remote", self.base / "images"):
                    if path.exists(): shutil.rmtree(path)
                (self.base / "images").mkdir()
                for name in ("smoked", "built", "phase", "receipt-written", "smoke-completed"):
                    (self.base / name).unlink(missing_ok=True)
                self.run_build(scenario, args=("--allow-rr-service", f"approved/{service}"))
                self.assertTrue((self.base / "smoked").exists(), self.output)
                self.assertNotIn("STOP UNKNOWN", self.output)

    def test_non_rr_and_job_containers_rejected_even_with_approval(self):
        for scenario in ("service-valid", "service-wrong-project", "service-generic",
                         "service-job", "service-oneoff", "service-unlabelled", "service-inspect-error"):
            with self.subTest(scenario=scenario):
                args = () if scenario == "service-valid" else ("--allow-rr-service", "approved/server")
                self.run_build(scenario, args=args)
                self.assert_not_built()

    def test_percentage_tail_floor(self):
        self.run_build("build-pct-drop")
        self.assert_unknown("build")
        self.assertIn(str(40*GIB), self.output)

    def test_malformed_runtime_probe_is_unknown(self):
        self.run_build("smoke-malformed")
        self.assert_unknown("smoke")

    def test_timeout_cancels_only_owned_term_ignoring_client(self):
        unrelated = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
        try:
            self.run_build("build-hang", extra_env={"BUILD_TIMEOUT_SECONDS": "1"})
            self.assert_unknown("build")
            self.assertIn("timeout; cancelling owned Docker CLI PID", self.output)
            self.assertIsNone(unrelated.poll(), "cancelled an unrelated process")
        finally:
            unrelated.terminate()
            unrelated.wait(timeout=5)

    def test_completed_build_still_requires_final_probe(self):
        self.run_build("build-final-drop")
        self.assert_unknown("build", completed=True)

    def test_completed_smoke_still_requires_final_probe(self):
        self.run_build("smoke-final-drop")
        self.assert_unknown("smoke", completed=True)


class ReviewTests(Harness):
    """Pre-registered review assertions, before changing the implementation:

    CAS: actual offline cas.sh must never delete/write through the four
    nested directories, and build.sh must repeat the guard immediately before CAS.
    Watch: each supervised phase resolves DockerRootDir once; multiple df
    samples and the final sample must still run and detect a delayed disk drop.
    Smoke: UNKNOWN diagnostics must identify the container, host, temp image
    and evidence, disclaim automatic force-removal and require shutdown proof.
    Types: a file at smoke/<tag> or directory at receipts/<tag>.json must
    refuse during the initial guard, before rsync/build and without UNKNOWN.
    """

    def prepare_cas(self):
        payload = b"offline CAS reproduction\n"
        digest = hashlib.sha256(payload).hexdigest()
        source = self.base / "cas-source"
        source.write_bytes(payload)
        (self.base / "cas-plan.tsv").write_text(
            f"fixture\t{digest}\t{len(payload)}\tfixture.bin\t-\t0\t{source}\n")
        for directory in ("ctx", "cache"):
            (self.base / "remote" / directory).mkdir(parents=True, exist_ok=True)
        return digest, payload

    def test_real_cas_control_uses_only_local_pinned_input(self):
        digest, payload = self.prepare_cas()
        self.run_build("real-cas-control")
        self.assertTrue((self.base / "cas-ran").exists(), self.output)
        self.assertEqual((self.base / "remote/cache/sha256" / digest).read_bytes(), payload)
        self.assertEqual((self.base / "remote/ctx/inputs" / digest).read_bytes(), payload)
        self.assertTrue((self.base / "built").exists(), self.output)

    def test_real_cas_inputs_cannot_delete_outside_hex_sentinel(self):
        self.prepare_cas()
        (self.base / "remote/ctx/inputs").symlink_to(self.base / "outside")
        victim = self.base / "outside" / ("a" * 64)
        victim.write_text("must survive")
        self.run_build("real-cas-inputs")
        self.assertTrue(victim.exists(), "actual CAS deleted outside-root hex sentinel\n" + self.output)
        self.assertFalse((self.base / "cas-ran").exists(), self.output)

    def test_real_cas_sha256_cannot_write_outside_root(self):
        digest, _ = self.prepare_cas()
        (self.base / "remote/cache/sha256").symlink_to(self.base / "outside")
        self.run_build("real-cas-sha256")
        self.assertFalse((self.base / "outside" / digest).exists(),
                         "actual CAS wrote outside root\n" + self.output)
        self.assertFalse((self.base / "cas-ran").exists(), self.output)

    def test_real_cas_guard_repeated_after_rsync(self):
        self.prepare_cas()
        victim = self.base / "outside" / ("a" * 64)
        victim.write_text("must survive")
        self.run_build("real-cas-late-inputs")
        self.assertTrue((self.base / "synced").exists(), self.output)
        self.assertTrue(victim.exists(), "CAS ran without a post-rsync guard\n" + self.output)
        self.assertFalse((self.base / "cas-ran").exists(), self.output)
        self.assertIn("before CAS", self.output)

    def watch(self, scenario):
        env = os.environ.copy()
        env.update(PATH=f"{self.shim}:{env['PATH']}", UE_TEST_DIR=str(self.base),
                   UE_TEST_SCENARIO=scenario)
        result = subprocess.run(
            ["/bin/bash", str(self.ue / "build-safety.sh"), "watch", "build", "0", "1",
             str(25*GIB), str(100*GIB), str(20*GIB), "10",
             "exec docker build -t rr-universal-export:ue-tmp-watch ."],
            env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=30)
        self.output = result.stdout
        calls = (self.base / "calls").read_text().splitlines()
        info = [call for call in calls if call.startswith("docker info ")]
        df = [call for call in calls if call.startswith("df -B1 ")]
        return result, info, df

    def test_watch_resolves_root_once_and_still_detects_delayed_drop(self):
        result, info, df = self.watch("build-delayed-drop")
        self.assertEqual(result.returncode, 75, self.output)
        self.assertGreaterEqual(len(df), 4, self.output)
        self.assertIn("watchdog", self.output)
        self.assertFalse((self.base / "built").exists(), self.output)
        self.assertEqual(len(info), 1, f"requeried DockerRootDir {len(info)} times\n{self.output}")

    def test_watch_resolves_root_once_including_final_sample(self):
        result, info, df = self.watch("build-slow")
        self.assertEqual(result.returncode, 0, self.output)
        self.assertGreaterEqual(len(df), 4, self.output)
        self.assertTrue((self.base / "built").exists(), self.output)
        self.assertEqual(len(info), 1, f"requeried DockerRootDir {len(info)} times\n{self.output}")

    def test_watch_initial_root_probe_error_is_closed(self):
        result, _, _ = self.watch("disk-info-error")
        self.assertNotEqual(result.returncode, 0, self.output)
        self.assertFalse((self.base / "build-pid").exists(), self.output)

    def test_watch_initial_malformed_root_is_closed(self):
        result, _, _ = self.watch("disk-info-malformed")
        self.assertNotEqual(result.returncode, 0, self.output)
        self.assertFalse((self.base / "build-pid").exists(), self.output)

    def test_smoke_unknown_identifies_recovery_targets(self):
        self.run_build("smoke-drop")
        self.assert_unknown("smoke")
        recovery = "\n".join(line for line in self.output.splitlines()
                             if "STOP UNKNOWN" in line)
        for expected in ("ue-smoke-ue-safety", "shim", "rr-universal-export:ue-tmp-",
                         ROOT + "/smoke/ue-safety", str(self.ue / "logs"),
                         "no automatic force-remove", "daemon-side stop is not confirmed"):
            self.assertIn(expected, recovery, self.output)

    def assert_initial_refusal(self):
        self.assert_not_built()
        self.assertFalse((self.base / "synced").exists(), self.output)
        self.assertNotIn("STOP UNKNOWN", self.output)
        self.assertFalse((self.ue / ".build-lock").exists(), self.output)

    def test_smoke_leaf_file_refuses_before_submission(self):
        leaf = self.base / "remote/smoke/ue-safety"
        leaf.parent.mkdir(parents=True)
        leaf.write_text("not a directory")
        self.run_build()
        self.assert_initial_refusal()
        self.assertEqual(leaf.read_text(), "not a directory")

    def test_receipt_leaf_directory_refuses_before_submission(self):
        leaf = self.base / "remote/receipts/ue-safety.json"
        leaf.mkdir(parents=True)
        self.run_build()
        self.assert_initial_refusal()

    def test_typed_guard_accepts_directory_and_regular_receipt(self):
        root = self.base / "remote"
        smoke = root / "smoke/ue-safety"
        smoke.mkdir(parents=True)
        receipt = root / "receipts/ue-safety.json"
        receipt.parent.mkdir(parents=True)
        receipt.write_text("existing receipt must not be changed by guard")
        env = os.environ.copy()
        env.update(PATH=f"{self.shim}:{env['PATH']}", UE_TEST_DIR=str(self.base))
        result = subprocess.run(
            ["/bin/bash", str(self.ue / "build-safety.sh"), "paths",
             str(root), str(smoke), str(receipt)],
            env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual(receipt.read_text(), "existing receipt must not be changed by guard")


if __name__ == "__main__":
    unittest.main()
