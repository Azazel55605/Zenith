#!/usr/bin/env python3
"""Read a cached signed kernel repeatedly on a copy of an installed test fixture."""
import argparse
import hashlib
import importlib.util
from pathlib import Path
import re
import sys

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'tools'))
import boot_image
spec=importlib.util.spec_from_file_location('h',ROOT/'tools/headless-smoke.py')
h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('keep',type=Path)
    parser.add_argument('--disk',type=Path,required=True)
    parser.add_argument('--archive',required=True)
    parser.add_argument('--elf',type=Path,default=ROOT/'output-x64/Zenith.elf')
    parser.add_argument('--checks',type=int,default=12)
    args=parser.parse_args()
    if not 1<=args.checks<=32: parser.error('checks must be 1..32')
    if not re.fullmatch(r'/var/lib/zenith/pkg/repositories/[0-9a-f]{64}\.zpk',args.archive): parser.error('expected a cached fixture archive')
    if args.keep.exists(): parser.error('evidence directory must be new')
    if not args.disk.is_file() or args.disk.stat().st_size>2*1024**3: parser.error('expected a disposable fixture image up to 2 GiB')
    state=boot_image.verify(args.disk)
    if state['current']!=hashlib.sha256(args.elf.read_bytes()).hexdigest():
        parser.error('fixture kernel does not match the selected ELF')
    args.keep.mkdir(parents=True)
    m=None
    try:
        m=h.Machine(args.keep/'replay',args.disk,network=['-netdev','user,id=n','-device','e1000e,netdev=n'],monitor=args.keep/'monitor.sock')
        assert m.wait('headless ready',timeout=180),'boot'
        for attempt in range(args.checks):
            label='K_READ_'+str(attempt)
            off=len(m.log());m.send('pkg kernel check '+args.archive+'; echo '+label+'=$? | logger\r')
            if not m.wait('user: '+label+'=0',off,600):
                raise RuntimeError(label+' failed; retained '+str(m.serial))
            print('PASS '+label,flush=True)
        collections=m.log().count('memory: pressure collection:')
        print('Read checks passed; pressure collections='+str(collections),flush=True)
        off=len(m.log());m.send('poweroff\r');assert m.wait('all filesystems unmounted',off,30),'shutdown'
        m.process.wait(timeout=30)
    finally:
        if m:m.stop()

if __name__=='__main__':main()
