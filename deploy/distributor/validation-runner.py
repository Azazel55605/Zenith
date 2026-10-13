#!/usr/bin/env python3
"""Run an unchanged tagged acceptance harness with efficient independent image copies."""
from pathlib import Path
import runpy
import shutil
import subprocess
import sys

original_copyfile = shutil.copyfile


def copyfile(source, destination, *, follow_symlinks=True):
    src, dst = Path(source), Path(destination)
    if follow_symlinks and src.suffix == ".img" and src.is_file() and not src.is_symlink() and not dst.is_symlink():
        if dst.exists() and (not dst.is_file() or src.samefile(dst)):
            return original_copyfile(source, destination)
        cp = shutil.which("cp")
        if cp:
            # Match copyfile's creation mode/umask, including read-only sources.
            with src.open("rb"), dst.open("wb"): pass
            try:
                result = subprocess.run([cp, "--reflink=auto", "--sparse=always", "--", str(src), str(dst)],
                                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                if result.returncode == 0: return destination
            except OSError: pass
    return original_copyfile(source, destination, follow_symlinks=follow_symlinks)


def main():
    if len(sys.argv) != 3: raise SystemExit("usage: validation-runner.py HARNESS KEEP")
    script = Path(sys.argv[1]).resolve(); keep = sys.argv[2]
    sys.path.insert(0, str(script.parent))
    sys.argv = [str(script), keep]
    shutil.copyfile = copyfile
    try: runpy.run_path(str(script), run_name="__main__")
    finally: shutil.copyfile = original_copyfile


if __name__ == "__main__": main()
