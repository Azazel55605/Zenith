#!/usr/bin/env python3
"""Replay a retained Settings fixture without rebuilding the kernel or reinstalling the OS."""
import argparse
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build', type=Path, required=True, help='retained distributor build-kernel-v... checkout')
    parser.add_argument('--keep', type=Path, help='new short evidence directory; defaults to /var/tmp')
    args = parser.parse_args(); build = args.build.expanduser().resolve()
    fixtures = [p for p in (build/'obj/distributor').glob('release-*/native/a/setup/disk.img') if p.is_file() and not p.is_symlink()]
    if not fixtures: raise SystemExit('No retained Settings setup fixture found')
    fixture = max(fixtures, key=lambda p:p.stat().st_mtime)
    harness = build/'tools/native-settings-smoke.py'; elf = build/'output-x64/Zenith.elf'
    if not harness.is_file() or not elf.is_file(): raise SystemExit('Retained harness/ELF missing')
    keep = args.keep.resolve() if args.keep else Path(tempfile.mkdtemp(prefix='zn-settings-',dir='/var/tmp'))/'replay'
    if keep.exists(): raise SystemExit('Use a new evidence directory')
    print('Original fixture preserved: '+str(fixture),flush=True)
    print('Replay evidence: '+str(keep),flush=True)
    command = [sys.executable,'-B',str(ROOT/'deploy/distributor/validation-runner.py'),str(harness),str(keep),'--prepared',str(fixture)]
    raise SystemExit(subprocess.run(command,cwd=build).returncode)


if __name__ == '__main__': main()
