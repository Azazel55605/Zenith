#!/usr/bin/env python3
"""Run an unchanged tagged acceptance harness with efficient independent image copies."""
from pathlib import Path
import runpy
import importlib.machinery
import importlib.util
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


def run_harness(script):
    spec = importlib.util.spec_from_file_location('validation_network', Path(__file__).with_name('validation-network.py'))
    network = importlib.util.module_from_spec(spec); spec.loader.exec_module(network)
    spec = importlib.util.spec_from_file_location('validation_status', Path(__file__).with_name('validation-status.py'))
    status = importlib.util.module_from_spec(spec); spec.loader.exec_module(status)
    original_exec = importlib.machinery.SourceFileLoader.exec_module
    instances = []
    headless = script.parent / 'headless-smoke.py'
    def execute(loader, module):
        original_exec(loader, module)
        if Path(loader.path).resolve() == headless.resolve():
            network.install(module.Machine, instances)
            status.install(module.Machine)
    importlib.machinery.SourceFileLoader.exec_module = execute
    try: runpy.run_path(str(script), run_name='__main__')
    finally:
        importlib.machinery.SourceFileLoader.exec_module = original_exec
        # A failed boot can raise before its harness assigns the machine to the
        # outer cleanup variable. Reap that VM before retaining/removing evidence.
        for machine in instances:
            try: machine.stop()
            except (OSError, RuntimeError): pass


def main():
    if len(sys.argv) < 3: raise SystemExit("usage: validation-runner.py HARNESS KEEP [HARNESS_ARGS...]")
    script = Path(sys.argv[1]).resolve(); keep = sys.argv[2]
    sys.path.insert(0, str(script.parent))
    sys.argv = [str(script), keep, *sys.argv[3:]]
    shutil.copyfile = copyfile
    try: run_harness(script)
    finally: shutil.copyfile = original_copyfile


if __name__ == "__main__": main()
