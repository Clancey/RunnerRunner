#!/usr/bin/env python3
"""Check every gem in Gemfile.lock against the image's Ruby version.

Bundler resolves against the Ruby that runs it, so a lock written on a newer
Ruby can pick gems the image's Ruby cannot load. This asks rubygems.org for
each locked gem's required_ruby_version (per platform variant) and fails if
any excludes the target. Control-host tool: needs network, never runs in the
image build.

    check_ruby_compat.py Gemfile.lock --ruby 3.2.3
"""
from __future__ import annotations

import argparse
import concurrent.futures
import json
import re
import sys
import urllib.request

LOCK_PLATFORM_SUFFIX = re.compile(r"^(?P<ver>[0-9][^-]*)-(?P<plat>.+)$")


def _segments(version: str) -> list:
    out: list = []
    for part in re.findall(r"[0-9]+|[A-Za-z]+", version):
        out.append(int(part) if part.isdigit() else part)
    return out


def compare_versions(a: str, b: str) -> int:
    """Gem::Version ordering: numeric segments compare numerically, a letter
    segment marks a prerelease and sorts before any number."""
    sa, sb = _segments(a), _segments(b)
    for i in range(max(len(sa), len(sb))):
        x = sa[i] if i < len(sa) else 0
        y = sb[i] if i < len(sb) else 0
        if x == y:
            continue
        if isinstance(x, str) and isinstance(y, str):
            return -1 if x < y else 1
        if isinstance(x, str):
            return -1
        if isinstance(y, str):
            return 1
        return -1 if x < y else 1
    return 0


def _bump(version: str) -> str:
    """The exclusive upper bound of a pessimistic constraint (~> 3.2.1 -> 3.3)."""
    nums = [s for s in _segments(version) if isinstance(s, int)]
    if len(nums) > 1:
        nums = nums[:-1]
    nums[-1] += 1
    return ".".join(str(n) for n in nums)


def satisfies(requirement: str, version: str) -> bool:
    requirement = (requirement or "").strip()
    if not requirement:
        return True
    for clause in requirement.split(","):
        m = re.match(r"^\s*(>=|<=|!=|~>|>|<|=)?\s*([0-9A-Za-z.]+)\s*$", clause)
        if not m:
            raise ValueError(f"unparseable requirement {requirement!r}")
        op, ref = m.group(1) or "=", m.group(2)
        c = compare_versions(version, ref)
        ok = {
            ">=": c >= 0,
            "<=": c <= 0,
            ">": c > 0,
            "<": c < 0,
            "=": c == 0,
            "!=": c != 0,
            "~>": c >= 0 and compare_versions(version, _bump(ref)) < 0,
        }[op]
        if not ok:
            return False
    return True


def locked_specs(lock_text: str) -> list[tuple[str, str, str | None]]:
    specs = []
    section = None
    for line in lock_text.splitlines():
        if re.match(r"^[A-Z]", line):
            section = line.strip()
            continue
        m = re.match(r"^    ([A-Za-z0-9_.-]+) \(([^)]+)\)$", line)
        if section == "GEM" and m:
            name, ver = m.groups()
            plat = None
            pm = LOCK_PLATFORM_SUFFIX.match(ver)
            if pm:
                ver, plat = pm.group("ver"), pm.group("plat")
            specs.append((name, ver, plat))
    return specs


def fetch_requirement(spec: tuple[str, str, str | None]) -> str:
    name, ver, plat = spec
    url = f"https://rubygems.org/api/v2/rubygems/{name}/versions/{ver}.json"
    if plat:
        url += f"?platform={plat}"
    with urllib.request.urlopen(url, timeout=60) as resp:
        data = json.load(resp)
    return data.get("ruby_version") or ""


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("lockfile")
    ap.add_argument("--ruby", required=True, help="target Ruby version, e.g. 3.2.3")
    args = ap.parse_args(argv)
    with open(args.lockfile, encoding="utf-8") as fh:
        specs = locked_specs(fh.read())
    if not specs:
        print("no gems found in the GEM section", file=sys.stderr)
        return 1
    bad = []
    with concurrent.futures.ThreadPoolExecutor(8) as pool:
        for spec, req in zip(specs, pool.map(fetch_requirement, specs)):
            if not satisfies(req, args.ruby):
                bad.append((spec, req))
    for (name, ver, plat), req in bad:
        print(f"INCOMPATIBLE {name} {ver}{'-' + plat if plat else ''}: requires Ruby {req}", file=sys.stderr)
    print(f"{len(specs)} locked gems checked against Ruby {args.ruby}: {len(bad)} incompatible")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
