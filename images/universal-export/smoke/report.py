#!/usr/bin/env python3
"""Build the smoke JSON report from run_smoke.sh's results TSV and print the verdict.

  report.py --results TSV --out FILE --isolated true|false [--apple-build B] --apple-admitted 0|1
  report.py validate --report FILE --lock LOCK (--apple-build B | --no-apple-pack)

TSV rows: set, preset, status (pass|fail|skip), detail, artifact_sha256. Rows of the pseudo set
"_image" are image hygiene checks and go to report["hygiene"].
Exit 0 = pass and promotable, 2 = pass but not promotable, 1 = any failure.

`validate` is the build host's promotion gate: it re-checks a fetched report against the lock
(every godot_set and its smoke_presets present and passing, every required hygiene row passing,
isolation proven, the Apple result matching the pack). Exit 0 = valid, 1 = not, 64 = usage.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

APPLE_PRESETS = ("ios", "visionos")
HYGIENE = "_image"
REQUIRED_HYGIENE = ("secret-env", "verify-installed", "isolation-check", "ephemeral-keys")


def build(rows, isolated: bool, apple_build, apple_admitted: bool):
    sets: dict = {}
    hygiene: dict = {}
    for sid, preset, status, detail, sha in rows:
        if sid == HYGIENE:
            hygiene[preset] = {"status": status, "detail": detail}
        else:
            sets.setdefault(sid, {})[preset] = {"status": status, "detail": detail, "artifact_sha256": sha or None}
    statuses = [r[2] for r in rows]
    rows = [r for r in rows if r[0] != HYGIENE]
    apple_rows = [r for r in rows if r[1] in APPLE_PRESETS]
    if not apple_build:
        apple = {"status": "skip", "build": None, "detail": "no Apple SDK pack (--allow-no-apple-smoke)"}
    elif not apple_admitted:
        apple = {"status": "fail", "build": apple_build, "detail": "apple-sdk-admit failed"}
    elif apple_rows and all(r[2] == "pass" for r in apple_rows):
        apple = {"status": "pass", "build": apple_build,
                 "detail": f"linked {', '.join(f'{r[0]}/{r[1]}' for r in apple_rows)}"}
    else:
        apple = {"status": "fail", "build": apple_build,
                 "detail": "no iOS/visionOS preset linked" if not apple_rows else
                 f"failed: {', '.join(f'{r[0]}/{r[1]}' for r in apple_rows if r[2] != 'pass')}"}
    failed = not rows or "fail" in statuses or apple["status"] == "fail"
    promotable = not failed and isolated and apple["status"] == "pass"
    report = {"schema": 1, "network_isolated": isolated, "sets": sets, "apple": apple, "hygiene": hygiene,
              "promotable": promotable}
    if failed:
        return report, 1, "SMOKE RESULT: FAIL"
    return report, 0 if promotable else 2, f"SMOKE RESULT: PASS promotable={'true' if promotable else 'false'}"


def validate(report, lock, apple_build=None) -> list[str]:
    """Problems that keep `report` from passing the promotion gate (empty = valid). With
    `apple_build` the report must be promotable for that pack; without, a no-pack pass."""
    if not isinstance(report, dict):
        return ["report is not a JSON object"]
    errors = []
    if report.get("schema") != 1 or isinstance(report.get("schema"), bool):
        errors.append(f"schema is {report.get('schema')!r}, expected 1")
    if report.get("network_isolated") is not True:
        errors.append("network_isolated is not true")
    sets = report.get("sets")
    sets = sets if isinstance(sets, dict) else {}
    want_sets = lock.get("godot_sets") or {}
    if not want_sets:
        errors.append("lock has no godot_sets")
    for sid, spec in want_sets.items():
        rows = sets.get(sid)
        if not isinstance(rows, dict):
            errors.append(f"set {sid}: missing from the report")
            continue
        presets = list((spec or {}).get("smoke_presets") or [])
        if not presets:
            errors.append(f"set {sid}: lock has no smoke_presets")
        for preset in presets:
            row = rows.get(preset)
            status = row.get("status") if isinstance(row, dict) else None
            if status != "pass":
                errors.append(f"set {sid}: preset {preset} is {status or 'missing'}")
        for preset, row in rows.items():
            if preset not in presets and (not isinstance(row, dict) or row.get("status") != "pass"):
                errors.append(f"set {sid}: extra row {preset} did not pass")
    for sid in sets:
        if sid not in want_sets:
            errors.append(f"set {sid}: not in the lock")
    hygiene = report.get("hygiene")
    hygiene = hygiene if isinstance(hygiene, dict) else {}
    for name in sorted(set(REQUIRED_HYGIENE) | set(hygiene)):
        row = hygiene.get(name)
        status = row.get("status") if isinstance(row, dict) else None
        if status != "pass":
            errors.append(f"hygiene {name} is {status or 'missing'}")
    apple = report.get("apple")
    apple = apple if isinstance(apple, dict) else {}
    if apple_build:
        if apple.get("build") != apple_build:
            errors.append(f"apple build is {apple.get('build')!r}, expected {apple_build!r}")
        if apple.get("status") != "pass":
            errors.append(f"apple status is {apple.get('status')!r}, expected 'pass'")
        if report.get("promotable") is not True:
            errors.append("promotable is not true")
    else:
        if apple.get("status") != "skip" or apple.get("build") is not None:
            errors.append(f"apple is {apple.get('status')!r}/{apple.get('build')!r}, expected skip without a pack")
        if report.get("promotable") is not False:
            errors.append("promotable is not false without an Apple SDK pack")
    return errors


def validate_main(argv) -> int:
    ap = argparse.ArgumentParser(prog="report.py validate")
    ap.add_argument("--report", required=True)
    ap.add_argument("--lock", required=True)
    g = ap.add_mutually_exclusive_group(required=True)
    g.add_argument("--apple-build")
    g.add_argument("--no-apple-pack", action="store_true")
    try:
        a = ap.parse_args(argv)
    except SystemExit as e:
        return 0 if e.code == 0 else 64
    try:
        lock = json.loads(Path(a.lock).read_text(encoding="utf-8"))
    except (OSError, ValueError) as e:
        print(f"report validate: cannot read lock {a.lock}: {e}", file=sys.stderr)
        return 1
    try:
        report = json.loads(Path(a.report).read_text(encoding="utf-8"))
    except (OSError, ValueError) as e:
        print(f"report validate: missing or malformed report {a.report}: {e}", file=sys.stderr)
        return 1
    errors = validate(report, lock, a.apple_build)
    for e in errors:
        print(f"report validate: {e}", file=sys.stderr)
    print(f"report validate: {'INVALID' if errors else 'ok'} ({a.report})")
    return 1 if errors else 0


def read_rows(path):
    rows = []
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        if line.strip():
            parts = (line.split("\t") + [""] * 5)[:5]
            rows.append(tuple(parts))
    return rows


def main(argv=None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    if argv[:1] == ["validate"]:
        return validate_main(argv[1:])
    ap = argparse.ArgumentParser(prog="report.py")
    ap.add_argument("--results", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--isolated", required=True, choices=["true", "false"])
    ap.add_argument("--apple-build", default="")
    ap.add_argument("--apple-admitted", required=True, choices=["0", "1"])
    a = ap.parse_args(argv)
    rows = read_rows(a.results) if Path(a.results).exists() else []
    report, code, line = build(rows, a.isolated == "true", a.apple_build or None, a.apple_admitted == "1")
    Path(a.out).write_text(json.dumps(report, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    for sid, presets in report["sets"].items():
        for preset, r in presets.items():
            print(f"  {r['status']:4}  {sid}/{preset}: {r['detail']}")
    for name, r in report["hygiene"].items():
        print(f"  {r['status']:4}  hygiene/{name}: {r['detail']}")
    print(f"  apple: {report['apple']['status']} ({report['apple']['detail']}); "
          f"network_isolated={str(report['network_isolated']).lower()}; report: {a.out}")
    print(line)
    return code


if __name__ == "__main__":
    sys.exit(main())
