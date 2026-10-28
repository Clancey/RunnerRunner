#!/usr/bin/env python3
"""Generate the tiny Godot 4 smoke project for one Godot set.

  gen_project.py --set ID --out DIR [--lock PATH] [--set-json PATH]
                 [--visionos-template PATH] [--android-deps | --no-android-deps]

Set data: --set-json, else $GODOT_SETS_ROOT/<id>/set.json (default /opt/godot/sets) when
present, else the lock's godot_sets entry. An explicit --lock is used for everything.
Writes project.godot, main.tscn, icon.png, export_presets.cfg (one preset per
smoke_presets entry, named after it) and smoke.json (what run_smoke.sh exports and checks).
"""
from __future__ import annotations

import argparse
import json
import os
import struct
import sys
import zlib
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_LOCK = HERE.parent / "toolchain.lock.json"
STEM = "Smoke"
PRODUCT = "UE Smoke Pr\u00f8duct"  # non-ASCII so the .rsrc UTF-16LE check is meaningful
COMPANY = "UE Smoke C\u00f8mpany"
BUNDLE_ID = "org.universalexport.smoke"
TEAM_ID = "ABCDE12345"
PLUGIN_DIR = "addons/ue_smoke_android_deps"

# preset id -> (platform, export file name, mode)
PRESETS = {
    "linux-x86_64": ("Linux", f"{STEM}.x86_64", "release"),
    "linux-arm64": ("Linux", f"{STEM}.arm64", "release"),
    "windows-x86_64": ("Windows Desktop", f"{STEM}.exe", "release"),
    "macos": ("macOS", f"{STEM}.zip", "release"),
    "web-nothreads": ("Web", "index.html", "release"),
    "android-apk-debug": ("Android", f"{STEM}.apk", "debug"),
    "android-apk-release": ("Android", f"{STEM}.apk", "release"),
    "android-aab-release": ("Android", f"{STEM}.aab", "release"),
    "ios": ("iOS", f"{STEM}.xcodeproj", "release"),
    "visionos": ("visionOS", f"{STEM}.xcodeproj", "release"),
}


class GenError(Exception):
    pass


def linux(arch):
    return {"debug/export_console_wrapper": 0, "binary_format/embed_pck": False,
            "texture_format/s3tc_bptc": arch == "x86_64", "texture_format/etc2_astc": arch == "arm64",
            "binary_format/architecture": arch, "ssh_remote_deploy/enabled": False}


def windows():
    return {"debug/export_console_wrapper": 0, "binary_format/embed_pck": False,
            "texture_format/s3tc_bptc": True, "texture_format/etc2_astc": False,
            "binary_format/architecture": "x86_64", "codesign/enable": False,
            "application/modify_resources": True, "application/icon": "res://icon.png",
            "application/console_wrapper_icon": "", "application/icon_interpolation": 4,
            "application/file_version": "1.0.0.0", "application/product_version": "1.0.0.0",
            "application/company_name": COMPANY, "application/product_name": PRODUCT,
            "application/file_description": "Universal export smoke test",
            "application/copyright": f"\u00a9 {COMPANY}", "application/trademarks": "",
            "application/export_angle": 0, "application/export_d3d12": 0,
            "ssh_remote_deploy/enabled": False}


def macos():
    return {"export/distribution_type": 0, "binary_format/architecture": "universal",
            "application/icon": "res://icon.png", "application/bundle_identifier": BUNDLE_ID,
            "application/signature": "", "application/short_version": "1.0", "application/version": "1",
            "application/min_macos_version_x86_64": "10.12", "application/min_macos_version_arm64": "11.00",
            "codesign/codesign": 0, "notarization/notarization": 0,
            "texture_format/s3tc_bptc": True, "texture_format/etc2_astc": True}


def web():
    return {"variant/extensions_support": False, "variant/thread_support": False,
            "vram_texture_compression/for_desktop": True, "vram_texture_compression/for_mobile": True,
            "html/export_icon": True, "html/custom_html_shell": "", "html/head_include": "",
            "html/canvas_resize_policy": 2, "html/focus_canvas_on_start": True,
            "html/experimental_virtual_keyboard": False, "progressive_web_app/enabled": False}


def android(fmt, min_sdk, target_sdk):
    return {"gradle_build/use_gradle_build": True, "gradle_build/gradle_build_directory": "",
            "gradle_build/android_source_template": "", "gradle_build/compress_native_libraries": False,
            "gradle_build/export_format": fmt, "gradle_build/min_sdk": str(min_sdk),
            "gradle_build/target_sdk": str(target_sdk),
            "architectures/armeabi-v7a": False, "architectures/arm64-v8a": True,
            "architectures/x86": False, "architectures/x86_64": False,
            "version/code": 1, "version/name": "1.0", "package/unique_name": BUNDLE_ID,
            "package/name": "UE Smoke", "package/signed": True,
            "xr_features/xr_mode": 0, "screen/immersive_mode": True, "permissions/internet": False}


def ios():
    return {"architectures/arm64": True, "application/app_store_team_id": TEAM_ID,
            "application/export_method_debug": 1, "application/export_method_release": 0,
            "application/bundle_identifier": BUNDLE_ID, "application/signature": "",
            "application/short_version": "1.0", "application/version": "1",
            "application/export_project_only": True, "application/targeted_device_family": 2,
            "application/min_ios_version": "16.0", "application/generate_simulator_library_if_missing": False,
            "shader_baker/enabled": False, "capabilities/access_wifi": False,
            "entitlements/push_notifications": "Disabled", "entitlements/game_center": False,
            "entitlements/increased_memory_limit": False, "storyboard/use_launch_screen_storyboard": False,
            "storyboard/image_scale_mode": 0}


def visionos():
    return {"architectures/arm64": True, "application/app_store_team_id": TEAM_ID,
            "application/bundle_identifier": BUNDLE_ID, "application/signature": "",
            "application/short_version": "1.0", "application/version": "1",
            "application/export_project_only": True, "application/min_visionos_version": "26.0",
            "application/app_role": 0, "application/immersion_style": 0, "shader_baker/enabled": False}


def preset_options(pid, min_sdk, target_sdk):
    return {
        "linux-x86_64": lambda: linux("x86_64"), "linux-arm64": lambda: linux("arm64"),
        "windows-x86_64": windows, "macos": macos, "web-nothreads": web,
        "android-apk-debug": lambda: android(0, min_sdk, target_sdk),
        "android-apk-release": lambda: android(0, min_sdk, target_sdk),
        "android-aab-release": lambda: android(1, min_sdk, target_sdk),
        "ios": ios, "visionos": visionos,
    }[pid]()


def cfg_value(v) -> str:
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, int):
        return str(v)
    return '"' + str(v).replace("\\", "\\\\").replace('"', '\\"') + '"'


def section(name, values) -> str:
    return f"[{name}]\n\n" + "".join(f"{k}={cfg_value(v)}\n" for k, v in values.items())


def export_presets(presets, min_sdk, target_sdk, visionos_template) -> str:
    out = []
    for i, pid in enumerate(presets):
        platform, file, _ = PRESETS[pid]
        head = {"name": pid, "platform": platform, "runnable": True, "advanced_options": False,
                "dedicated_server": False, "custom_features": "", "export_filter": "all_resources",
                "include_filter": "", "exclude_filter": "", "export_path": f"out/{pid}/{file}",
                "encryption_include_filters": "", "encryption_exclude_filters": "", "seed": 0,
                "encrypt_pck": False, "encrypt_directory": False, "script_export_mode": 2}
        tpl = visionos_template if pid == "visionos" and visionos_template else ""
        opts = {"custom_template/debug": tpl, "custom_template/release": tpl}
        opts.update(preset_options(pid, min_sdk, target_sdk))
        out += [section(f"preset.{i}", head), section(f"preset.{i}.options", opts)]
    return "\n".join(out)


def png(size=256) -> bytes:
    rows = b"".join(b"\0" + b"".join(bytes((x, y, (x + y) // 2 & 0xFF, 255)) for x in range(size))
                    for y in range(size))

    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))


def project_godot(major_minor, plugin) -> str:
    text = (f'; Engine configuration file.\n\nconfig_version=5\n\n[application]\n\n'
            f'config/name="UE Smoke"\nrun/main_scene="res://main.tscn"\n'
            f'config/features=PackedStringArray("{major_minor}")\nconfig/icon="res://icon.png"\n')
    if plugin:
        text += f'\n[editor_plugins]\n\nenabled=PackedStringArray("res://{PLUGIN_DIR}/plugin.cfg")\n'
    return text + ("\n[rendering]\n\ntextures/vram_compression/import_etc2_astc=true\n"
                   "textures/vram_compression/import_s3tc_bptc=true\n")


MAIN_SCENE = """[gd_scene format=3]

[node name="Main" type="Node2D"]

[node name="Label" type="Label" parent="."]
offset_right = 240.0
offset_bottom = 40.0
text = "UE Smoke"
"""

PLUGIN_CFG = """[plugin]

name="UE Smoke Android deps"
description="Adds the lock's android.maven_prewarm coordinates to Gradle exports."
author="universal-export"
version="1.0"
script="plugin.gd"
"""

PLUGIN_GD = """@tool
extends EditorPlugin

var _export_plugin: AndroidDepsExport


func _enter_tree() -> void:
\t_export_plugin = AndroidDepsExport.new()
\tadd_export_plugin(_export_plugin)


func _exit_tree() -> void:
\tremove_export_plugin(_export_plugin)
\t_export_plugin = null


class AndroidDepsExport extends EditorExportPlugin:
\tfunc _get_name() -> String:
\t\treturn "UESmokeAndroidDeps"

\tfunc _supports_platform(platform: EditorExportPlatform) -> bool:
\t\treturn platform.get_os_name() == "Android"

\tfunc _get_android_dependencies(_platform: EditorExportPlatform, _debug: bool) -> PackedStringArray:
\t\treturn PackedStringArray([{deps}])
"""


def load_set(set_id, lock, lock_explicit, set_json):
    path = Path(set_json) if set_json else None
    if path is None and not lock_explicit:
        candidate = Path(os.environ.get("GODOT_SETS_ROOT") or "/opt/godot/sets") / set_id / "set.json"
        path = candidate if candidate.is_file() else None
    if path is not None:
        try:
            data = json.loads(path.read_text())
        except (OSError, ValueError) as e:
            raise GenError(f"cannot read {path}: {e}") from None
        if data.get("id", set_id) != set_id:
            raise GenError(f"{path} is for set {data.get('id')!r}, not {set_id!r}")
        return data
    try:
        return lock["godot_sets"][set_id]
    except KeyError:
        raise GenError(f"unknown Godot set {set_id!r} (lock has {', '.join(lock.get('godot_sets', {}))})") from None


def generate(set_id, out, lock_path=DEFAULT_LOCK, lock_explicit=False, set_json=None,
             visionos_template=None, android_deps=None) -> dict:
    try:
        lock = json.loads(Path(lock_path).read_text())
    except (OSError, ValueError) as e:
        raise GenError(f"cannot read lock {lock_path}: {e}") from None
    s = load_set(set_id, lock, lock_explicit, set_json)
    presets = list(s.get("smoke_presets") or [])
    unknown = [p for p in presets if p not in PRESETS]
    if unknown or not presets:
        raise GenError(f"set {set_id}: unknown or empty smoke_presets {unknown or presets}")
    out = Path(out)
    if out.exists() and (not out.is_dir() or any(out.iterdir())):
        raise GenError(f"{out} exists and is not an empty directory")
    major_minor = ".".join(str(s["version"]).split(".")[:2])
    min_sdk = lock.get("android", {}).get("plugin_prewarm", {}).get("min_sdk", 24)
    target_sdk = s.get("android", {}).get("compile_sdk") or lock["android"]["plugin_prewarm"]["compile_sdk"]
    if android_deps is None:
        android_deps = not s.get("fork_set_id")
    deps = [c for c in lock.get("android", {}).get("maven_prewarm", []) if not c.startswith("org.godotengine:")]
    plugin = android_deps and bool(deps) and any(PRESETS[p][0] == "Android" for p in presets)

    files = {
        "project.godot": project_godot(major_minor, plugin),
        "main.tscn": MAIN_SCENE,
        "export_presets.cfg": export_presets(presets, min_sdk, target_sdk, visionos_template),
    }
    if plugin:
        files[f"{PLUGIN_DIR}/plugin.cfg"] = PLUGIN_CFG
        files[f"{PLUGIN_DIR}/plugin.gd"] = PLUGIN_GD.format(deps=", ".join(json.dumps(c) for c in deps))
    manifest = {"schema": 1, "set": set_id, "version": s["version"], "product_name": PRODUCT,
                "android_deps": deps if plugin else [],
                "presets": [{"id": p, "name": p, "platform": PRESETS[p][0], "mode": PRESETS[p][2],
                             "file": PRESETS[p][1]} for p in presets]}
    files["smoke.json"] = json.dumps(manifest, indent=1, ensure_ascii=False) + "\n"
    out.mkdir(parents=True, exist_ok=True)
    for rel, text in files.items():
        (out / rel).parent.mkdir(parents=True, exist_ok=True)
        (out / rel).write_text(text, encoding="utf-8")
    (out / "icon.png").write_bytes(png())
    return manifest


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="gen_project.py", description=__doc__.split("\n\n")[0])
    ap.add_argument("--set", required=True, dest="set_id")
    ap.add_argument("--out", required=True)
    ap.add_argument("--lock")
    ap.add_argument("--set-json")
    ap.add_argument("--visionos-template")
    deps = ap.add_mutually_exclusive_group()
    deps.add_argument("--android-deps", dest="deps", action="store_true", default=None)
    deps.add_argument("--no-android-deps", dest="deps", action="store_false")
    a = ap.parse_args(argv)
    try:
        m = generate(a.set_id, a.out, a.lock or DEFAULT_LOCK, a.lock is not None, a.set_json,
                     a.visionos_template, a.deps)
    except GenError as e:
        print(f"gen_project: {e}", file=sys.stderr)
        return 1
    print(f"gen_project: {a.out}: {' '.join(p['id'] for p in m['presets'])}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
