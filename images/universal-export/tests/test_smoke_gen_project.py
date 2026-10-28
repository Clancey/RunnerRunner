"""smoke/gen_project.py: generated project, presets per set and required preset options."""
import configparser
import json
import os
import re
import struct
import subprocess
import sys
import tempfile
import unittest
import zlib
from pathlib import Path

UE = Path(__file__).resolve().parents[1]
GEN = UE / "smoke" / "gen_project.py"
LOCK = json.loads((UE / "toolchain.lock.json").read_text())
PLATFORMS = {"linux-x86_64": "Linux", "linux-arm64": "Linux", "windows-x86_64": "Windows Desktop",
             "macos": "macOS", "web-nothreads": "Web", "android-apk-debug": "Android",
             "android-apk-release": "Android", "android-aab-release": "Android", "ios": "iOS",
             "visionos": "visionOS"}


def unquote(value):
    return json.loads(value) if value.startswith('"') else value


def parse_cfg(path):
    cp = configparser.ConfigParser(delimiters=("=",), interpolation=None, strict=True)
    cp.optionxform = str
    cp.read(path, encoding="utf-8")
    return cp


class GenCase(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.env = dict(os.environ, GODOT_SETS_ROOT=str(self.tmp / "no-sets"))

    def tearDown(self):
        self._tmp.cleanup()

    def gen(self, set_id, *extra, out=None, check=True):
        out = out or self.tmp / set_id
        r = subprocess.run([sys.executable, str(GEN), "--set", set_id, "--out", str(out), *map(str, extra)],
                           capture_output=True, text=True, env=self.env)
        if check:
            self.assertEqual(r.returncode, 0, r.stderr)
        return out, r

    def presets(self, out):
        cp = parse_cfg(out / "export_presets.cfg")
        found = {}
        i = 0
        while cp.has_section(f"preset.{i}"):
            head = {k: unquote(v) for k, v in cp[f"preset.{i}"].items()}
            opts = {k: unquote(v) for k, v in cp[f"preset.{i}.options"].items()}
            found[head["name"]] = (head, opts)
            i += 1
        self.assertEqual(len(cp.sections()), 2 * i, "every preset has a header and an options section")
        return found


class PresetsPerSetTest(GenCase):
    def test_names_follow_smoke_presets(self):
        for set_id, entry in LOCK["godot_sets"].items():
            out, _ = self.gen(set_id, "--lock", UE / "toolchain.lock.json")
            presets = self.presets(out)
            self.assertEqual(list(presets), entry["smoke_presets"])
            for name, (head, opts) in presets.items():
                self.assertEqual(head["platform"], PLATFORMS[name])
                for key in ("runnable", "export_filter", "export_path", "script_export_mode", "encrypt_pck"):
                    self.assertIn(key, head, f"{set_id}/{name}")
                self.assertEqual((opts["custom_template/debug"], opts["custom_template/release"]) if name != "visionos"
                                 else ("", ""), ("", ""))

    def test_visionos_only_for_fork(self):
        official, _ = self.gen("official-4.7.2")
        fork, _ = self.gen("fork-c064")
        self.assertNotIn("visionos", self.presets(official))
        head, opts = self.presets(fork)["visionos"]
        self.assertEqual(head["platform"], "visionOS")
        self.assertEqual(opts["application/export_project_only"], "true")
        self.assertRegex(opts["application/min_visionos_version"], r"^\d+\.\d+$")
        self.assertEqual(opts["custom_template/release"], "")

    def test_visionos_template_override(self):
        tpl = self.tmp / "visionos.zip"
        out, _ = self.gen("fork-c064", "--visionos-template", tpl)
        _, opts = self.presets(out)["visionos"]
        self.assertEqual((opts["custom_template/debug"], opts["custom_template/release"]), (str(tpl), str(tpl)))

    def test_set_json_wins_over_lock(self):
        root = self.tmp / "sets" / "official-4.7.2"
        root.mkdir(parents=True)
        entry = dict(LOCK["godot_sets"]["official-4.7.2"], id="official-4.7.2", schema=1,
                     smoke_presets=["linux-x86_64", "ios"], fork_set_id=None)
        (root / "set.json").write_text(json.dumps(entry))
        self.env["GODOT_SETS_ROOT"] = str(self.tmp / "sets")
        out, _ = self.gen("official-4.7.2")
        self.assertEqual(list(self.presets(out)), ["linux-x86_64", "ios"])
        out2, _ = self.gen("official-4.7.2", "--set-json", root / "set.json", out=self.tmp / "explicit")
        self.assertEqual(list(self.presets(out2)), ["linux-x86_64", "ios"])

    def test_errors(self):
        _, r = self.gen("nope-1.0", check=False)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("nope-1.0", r.stderr)
        bad = self.tmp / "bad.json"
        bad.write_text(json.dumps(dict(LOCK["godot_sets"]["official-4.7.2"], id="official-4.7.2",
                                       smoke_presets=["linux-x86_64", "amiga"])))
        _, r = self.gen("official-4.7.2", "--set-json", bad, check=False)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("amiga", r.stderr)
        out = self.tmp / "busy"
        out.mkdir()
        (out / "keep.txt").write_text("x")
        _, r = self.gen("official-4.7.2", out=out, check=False)
        self.assertNotEqual(r.returncode, 0)
        self.assertEqual(sorted(p.name for p in out.iterdir()), ["keep.txt"])

    def test_deterministic(self):
        a, _ = self.gen("fork-c064", out=self.tmp / "a")
        b, _ = self.gen("fork-c064", out=self.tmp / "b")
        files = sorted(p.relative_to(a) for p in a.rglob("*") if p.is_file())
        self.assertEqual(files, sorted(p.relative_to(b) for p in b.rglob("*") if p.is_file()))
        for f in files:
            self.assertEqual((a / f).read_bytes(), (b / f).read_bytes(), f)


class PresetOptionsTest(GenCase):
    @classmethod
    def setUpClass(cls):
        cls._ctmp = tempfile.TemporaryDirectory()
        cls.out = {}
        for set_id in LOCK["godot_sets"]:
            out = Path(cls._ctmp.name) / set_id
            subprocess.run([sys.executable, str(GEN), "--set", set_id, "--out", str(out)], check=True,
                           env=dict(os.environ, GODOT_SETS_ROOT=str(Path(cls._ctmp.name) / "none")))
            cls.out[set_id] = out

    @classmethod
    def tearDownClass(cls):
        cls._ctmp.cleanup()

    def each(self):
        for set_id, out in self.out.items():
            yield set_id, out, self.presets(out), json.loads((out / "smoke.json").read_text())

    def test_desktop_and_web(self):
        for set_id, _, p, manifest in self.each():
            product = manifest["product_name"]
            self.assertTrue(any(ord(c) > 127 for c in product), "product name exercises UTF-16")
            _, win = p["windows-x86_64"]
            self.assertEqual(win["application/modify_resources"], "true")
            self.assertEqual(win["application/product_name"], product)
            self.assertTrue(win["application/company_name"])
            self.assertTrue(win["application/icon"].startswith("res://"))
            self.assertEqual(win["binary_format/architecture"], "x86_64")
            self.assertEqual(p["linux-x86_64"][1]["binary_format/architecture"], "x86_64")
            self.assertEqual(p["linux-arm64"][1]["binary_format/architecture"], "arm64")
            self.assertEqual(p["linux-arm64"][1]["texture_format/etc2_astc"], "true")
            _, mac = p["macos"]
            self.assertEqual((mac["codesign/codesign"], mac["notarization/notarization"]), ("0", "0"))
            self.assertEqual(mac["binary_format/architecture"], "universal")
            _, web = p["web-nothreads"]
            self.assertEqual((web["variant/thread_support"], web["variant/extensions_support"]), ("false", "false"))

    def test_android(self):
        for set_id, _, p, _ in self.each():
            compile_sdk = LOCK["godot_sets"][set_id]["android"]["compile_sdk"]
            formats = {"android-apk-debug": "0", "android-apk-release": "0", "android-aab-release": "1"}
            for name, fmt in formats.items():
                _, o = p[name]
                self.assertEqual(o["gradle_build/use_gradle_build"], "true")
                self.assertEqual(o["gradle_build/export_format"], fmt, name)
                self.assertEqual(o["gradle_build/min_sdk"], str(LOCK["android"]["plugin_prewarm"]["min_sdk"]))
                self.assertEqual(o["gradle_build/target_sdk"], str(compile_sdk))
                self.assertEqual(o["architectures/arm64-v8a"], "true")
                for arch in ("armeabi-v7a", "x86", "x86_64"):
                    self.assertEqual(o[f"architectures/{arch}"], "false")
                self.assertEqual(o["package/signed"], "true")
                self.assertRegex(o["package/unique_name"], r"^[a-z]+(\.[a-z0-9_]+)+$")
                self.assertFalse([k for k in o if k.startswith("keystore/")], "keystores come from env")

    def test_ios(self):
        for set_id, _, p, _ in self.each():
            _, o = p["ios"]
            self.assertEqual(o["application/export_project_only"], "true")
            self.assertRegex(o["application/app_store_team_id"], r"^[A-Z0-9]{10}$")
            self.assertRegex(o["application/bundle_identifier"], r"^[a-z]+(\.[a-z0-9-]+)+$")
            self.assertEqual(o["architectures/arm64"], "true")
            self.assertEqual(o["application/generate_simulator_library_if_missing"], "false")

    def test_manifest(self):
        for set_id, _, p, manifest in self.each():
            self.assertEqual(manifest["set"], set_id)
            entries = {e["id"]: e for e in manifest["presets"]}
            self.assertEqual(list(entries), list(p))
            self.assertEqual(entries["android-apk-debug"]["mode"], "debug")
            self.assertEqual({e["mode"] for k, e in entries.items() if k != "android-apk-debug"}, {"release"})
            self.assertEqual(entries["android-aab-release"]["file"].rsplit(".", 1)[1], "aab")
            self.assertEqual(entries["ios"]["file"].rsplit(".", 1)[1], "xcodeproj")
            self.assertEqual(entries["macos"]["file"].rsplit(".", 1)[1], "zip")
            self.assertEqual(entries["web-nothreads"]["file"], "index.html")
            for e in entries.values():
                self.assertEqual(e["name"], e["id"])
                self.assertNotIn("/", e["file"])

    def test_project_files(self):
        for set_id, out, _, _ in self.each():
            text = (out / "project.godot").read_text()
            self.assertIn("\nconfig_version=5\n", text)
            major_minor = ".".join(LOCK["godot_sets"][set_id]["version"].split(".")[:2])
            self.assertIn(f'config/features=PackedStringArray("{major_minor}"', text)
            scene = re.search(r'run/main_scene="res://([^"]+)"', text)[1]
            self.assertTrue((out / scene).read_text().startswith("[gd_scene"))
            icon = re.search(r'config/icon="res://([^"]+)"', text)[1]
            self.assertPng(out / icon)
            self.assertIn("import_etc2_astc=true", text)

    def test_android_deps_plugin(self):
        coords = [c for c in LOCK["android"]["maven_prewarm"] if not c.startswith("org.godotengine:")]
        official = self.out["official-4.7.2"]
        script = next((official / "addons").rglob("*.gd")).read_text()
        for c in coords:
            self.assertIn(f'"{c}"', script)
        self.assertNotIn("org.godotengine:", script)
        self.assertIn("_get_android_dependencies", script)
        cfg = re.search(r'enabled=PackedStringArray\("res://([^"]+)"\)', (official / "project.godot").read_text())[1]
        self.assertTrue((official / cfg).is_file())
        fork = self.out["fork-c064"]
        self.assertFalse((fork / "addons").exists())
        self.assertNotIn("editor_plugins", (fork / "project.godot").read_text())
        on, _ = self.gen("fork-c064", "--android-deps")
        self.assertTrue((on / "addons").is_dir())
        off, _ = self.gen("official-4.7.2", "--no-android-deps")
        self.assertFalse((off / "addons").exists())

    def assertPng(self, path):
        data = path.read_bytes()
        self.assertEqual(data[:8], b"\x89PNG\r\n\x1a\n")
        pos, chunks = 8, {}
        while pos < len(data):
            n, kind = struct.unpack_from(">I4s", data, pos)
            body = data[pos + 8:pos + 8 + n]
            self.assertEqual(struct.unpack_from(">I", data, pos + 8 + n)[0], zlib.crc32(kind + body))
            chunks.setdefault(kind, b"")
            chunks[kind] += body
            pos += 12 + n
        w, h, depth, color = struct.unpack(">IIBB", chunks[b"IHDR"][:10])
        self.assertGreaterEqual(min(w, h), 128)
        self.assertEqual((depth, color), (8, 6))
        self.assertEqual(len(zlib.decompress(chunks[b"IDAT"])), h * (1 + 4 * w))
        self.assertIn(b"IEND", chunks)


if __name__ == "__main__":
    unittest.main()
