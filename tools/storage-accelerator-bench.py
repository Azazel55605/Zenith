#!/usr/bin/env python3
"""Compare KVM/TCG FAT I/O on disposable images, without a kernel build or install."""
import argparse
import importlib.util
import json
import re
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT/'tools'))

def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    return module

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--iso', type=Path, required=True)
    parser.add_argument('--keep', type=Path, required=True)
    parser.add_argument('--accelerator', choices=('kvm', 'tcg'), required=True)
    args = parser.parse_args()
    keep = args.keep.resolve()
    if keep.exists(): raise SystemExit('Use a new empty benchmark directory')
    if not args.iso.is_file(): raise SystemExit('ISO not found')
    for tool in ('mkfs.fat', 'fsck.fat', 'mcopy'):
        if not shutil.which(tool): raise SystemExit('Missing benchmark tool: '+tool)
    keep.mkdir(parents=True)
    rows = []
    result = dict(accelerator=args.accelerator, iso=str(args.iso.resolve()), operations=rows, passed=False)
    volume = keep/'fat.img'; disk = keep/'secondary.img'
    with volume.open('wb') as stream: stream.truncate(64*1024*1024)
    subprocess.run(['mkfs.fat', '-F', '32', str(volume)], check=True, stdout=subprocess.DEVNULL)
    sector = bytearray(512)
    sector[446:462] = struct.pack('<B3sB3sII', 0, b'\0'*3, 0x0c, b'\0'*3, 2048, volume.stat().st_size//512)
    sector[510:512] = b'\x55\xaa'
    with disk.open('wb') as stream, volume.open('rb') as source:
        stream.write(sector); stream.seek(2048*512); shutil.copyfileobj(source, stream)
    h = load('headless_bench', ROOT/'tools/headless-smoke.py')
    load('accelerator_bench', ROOT/'deploy/distributor/validation-accelerator.py').install(h, args.accelerator)
    m = None
    started = time.monotonic()
    try:
        boot = time.monotonic()
        m = h.Machine(keep/'guest', extra_disks=(disk,), network=[], iso=args.iso.resolve())
        if not m.wait('headless ready', timeout=180): raise RuntimeError('boot deadline')
        rows.append(dict(operation='boot', seconds=round(time.monotonic()-boot, 3)))
        for label, command in (
            ('mount', 'mkdir -p /mnt; mount sata1p0 /mnt'),
            ('write-4MiB', 'dd if=/dev/zero of=/mnt/bench.bin bs=65536 count=64'),
            ('read-4MiB', 'dd if=/mnt/bench.bin of=/dev/null bs=65536 count=64'),
            ('sync', 'sync')):
            offset = len(m.log()); began = time.monotonic()
            marker = 'IO_BENCH_'+label.replace('-', '_')
            m.send(command+'; echo '+marker+'=$?\r')
            passed = m.wait(marker+'=0', offset, 300)
            rows.append(dict(operation=label, seconds=round(time.monotonic()-began, 3), passed=passed))
            print(json.dumps(rows[-1]), flush=True)
            if not passed: raise RuntimeError(label+' failed or timed out; inspect guest/serial.log')
        offset=len(m.log()); m.send('poweroff\r')
        if not m.wait('all filesystems unmounted', offset, 60) or m.process.wait(timeout=30) != 0:
            raise RuntimeError('clean poweroff failed')
        with (keep/'guest/secondary.img').open('rb') as source, volume.open('wb') as target:
            source.seek(2048*512); shutil.copyfileobj(source, target)
        audit=subprocess.run(['fsck.fat', '-n', str(volume)], capture_output=True, text=True)
        (keep/'fsck.log').write_text(audit.stdout+audit.stderr)
        # Cosmos does not maintain FAT32's optional FSInfo free-space hint.
        # Accept only that exact diagnostic; allocation/chain errors still fail.
        hint_only = re.fullmatch(r'fsck.fat [^\n]+\nFree cluster summary wrong \(\d+ vs. really \d+\)\n'
                                 r'  Auto-correcting\.\n\nLeaving filesystem unchanged\.\n'+
                                 re.escape(str(volume))+r': 1 files, \d+/\d+ clusters\n', audit.stdout)
        if audit.returncode != 0 and not (audit.returncode == 1 and hint_only and not audit.stderr):
            raise RuntimeError('independent FAT audit failed')
        result['fat_fsinfo_hint_warning'] = bool(hint_only)
        extracted=keep/'bench.bin'
        subprocess.run(['mcopy', '-i', str(volume), '::/bench.bin', str(extracted)], check=True)
        if extracted.read_bytes() != bytes(4*1024*1024): raise RuntimeError('guest write readback mismatch')
        result['passed']=True
    finally:
        if m: m.stop()
        result['seconds']=round(time.monotonic()-started, 3)
        (keep/'benchmark.json').write_text(json.dumps(result, indent=2)+'\n')
        print(json.dumps(result, indent=2), flush=True)

if __name__ == '__main__': main()
