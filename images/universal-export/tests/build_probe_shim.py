#!/usr/bin/env python3
"""Probe-only shims for the shell E2Es. Never invoke real Docker/df/timeout."""
import os
from pathlib import Path
import sys

name, args = Path(sys.argv[0]).name, sys.argv[1:]
if name == "realpath":
    print(os.path.realpath(args[-1]))
elif name == "df":
    print("Avail Size\n214748364800 429496729600")
elif name == "timeout":
    if args[0] == "-k":
        args = args[2:]
    os.execvp(args[1], args[1:])
elif name == "docker":
    if args[:2] == ["info", "--format"]:
        print("/fake/docker")
    elif args == ["ps", "--no-trunc", "--quiet"]:
        pass
    elif args[:2] == ["image", "inspect"]:
        sys.exit(1)
    else:
        if args[0] == "build" and os.environ.get("E2E_SUBMITTED"):
            Path(os.environ["E2E_SUBMITTED"]).touch()
        sys.exit("unexpected Docker operation in probe-only shim: " + repr(args))
else:
    sys.exit("unexpected probe shim: " + name)
