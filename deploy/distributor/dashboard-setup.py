#!/usr/bin/env python3
"""Attach the dashboard route to the existing single-server nginx site safely."""
import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import time


def without_comments(text):
    return re.sub(r'''("(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*')|\#[^\n]*''',
                  lambda match: match[1] or " " * len(match[0]), text)


def include_route(text):
    active = without_comments(text)
    if re.search(r'''\binclude\s+["']?/etc/nginx/snippets/zenith-dashboard\*?\.conf["']?\s*;''', active): return text
    # Mask quoted text too, retaining offsets into the original configuration.
    code = re.sub(r'''("(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*')''', lambda match: " " * len(match[0]), active)
    servers = list(re.finditer(r"\bserver\s*\{", code))
    if len(servers) != 1: raise ValueError("expected one nginx server block; add the dashboard include to the intended server manually")
    point = servers[0].end()
    return text[:point] + "\n    include /etc/nginx/snippets/zenith-dashboard.conf;" + text[point:]


def activate(path):
    if path.is_symlink() or not path.is_file(): raise ValueError("expected an existing regular nginx site file")
    if path.stat().st_size > 1024 * 1024: raise ValueError("nginx site too large for automatic setup")
    original = path.read_text(); updated = include_route(original)
    backup = None
    if updated != original:
        backup = path.with_name(path.name + ".before-dashboard-" + str(time.time_ns()))
        shutil.copy2(path, backup)
        descriptor, staged = tempfile.mkstemp(prefix=".zenith-dashboard-", dir=path.parent)
        try:
            with os.fdopen(descriptor, "w") as output:
                output.write(updated); output.flush(); os.fsync(output.fileno())
                info = path.stat(); os.fchmod(output.fileno(), info.st_mode & 0o777)
                os.fchown(output.fileno(), info.st_uid, info.st_gid)
            os.replace(staged, path)
        finally:
            if os.path.exists(staged): os.unlink(staged)
    try: subprocess.run(["nginx", "-t"], check=True)
    except (OSError, subprocess.SubprocessError):
        if backup: shutil.copy2(backup, path)
        raise ValueError("nginx validation failed; original site restored; inspect nginx output")
    subprocess.run(["systemctl", "reload", "nginx"], check=True)
    print("Dashboard nginx route validated and reloaded; backup: " + (str(backup) if backup else "no change needed"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("site", type=Path, nargs="?", default=Path("/etc/nginx/sites-available/zenith-packages"))
    args = parser.parse_args()
    try: activate(args.site); return 0
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        print("Dashboard nginx setup failed: " + str(error)); return 1


if __name__ == "__main__": raise SystemExit(main())
