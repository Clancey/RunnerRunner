#!/usr/bin/env python3
"""Idempotent content patches for Apple SDK packs.

Format-1 packs are admitted unpatched and patched after verification by
apple-sdk-admit; format-2 packs are patched by make_sdk_bundle.sh before they
are manifested. Both call apply(), so the bytes are identical either way.
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import stat
import sys
from pathlib import Path

_OBSOLETE = re.compile(rb"module _c_standard_library_obsolete \[system\] \{.*?\n\}\n", re.S)

# name -> (does this relative path qualify, bytes -> bytes)
PATCHES = {
    "modulemap-obsolete-v1": (lambda rel: rel.endswith(".modulemap"), lambda data: _OBSOLETE.sub(b"", data)),
}


def patch_bytes(names, rel: str, data: bytes) -> bytes:
    for name in names:
        wants, fn = PATCHES[name]
        if wants(rel):
            data = fn(data)
    return data


def apply(root, names) -> list:
    """Patch regular files under root in place (symlinks are never followed).

    Returns [{path, before_sha256, after_sha256}] for the files that changed,
    sorted by path (relative, '/'-separated). Raises KeyError for an unknown patch.
    """
    names = list(names)
    for name in names:
        PATCHES[name]  # noqa: B018 - fail before touching anything
    root = Path(root)
    changed = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames.sort()
        for fname in sorted(filenames):
            path = Path(dirpath, fname)
            rel = path.relative_to(root).as_posix()
            if not any(PATCHES[n][0](rel) for n in names) or not stat.S_ISREG(path.lstat().st_mode):
                continue
            fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
            with os.fdopen(fd, "rb") as f:
                before = f.read()
            after = patch_bytes(names, rel, before)
            if after == before:
                continue
            fd = os.open(path, os.O_WRONLY | os.O_TRUNC | os.O_NOFOLLOW)
            with os.fdopen(fd, "wb") as f:
                f.write(after)
            changed.append({"path": rel, "before_sha256": hashlib.sha256(before).hexdigest(),
                            "after_sha256": hashlib.sha256(after).hexdigest()})
    return sorted(changed, key=lambda r: r["path"])


def main(argv=None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    if argv[:1] == ["list"] and len(argv) == 1:
        print("\n".join(PATCHES))
        return 0
    if argv[:1] == ["apply"] and len(argv) >= 3:
        try:
            print(json.dumps(apply(argv[1], argv[2:]), indent=2))
        except KeyError as e:
            print(f"apple_sdk_patches: unknown patch {e}", file=sys.stderr)
            return 2
        return 0
    print("usage: apple_sdk_patches.py list | apply ROOT PATCH...", file=sys.stderr)
    return 64


if __name__ == "__main__":
    sys.exit(main())
