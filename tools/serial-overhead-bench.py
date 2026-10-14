#!/usr/bin/env python3
"""Compare serial volume and wall time for the same disposable live workload."""
import argparse
import importlib.util
import json
from pathlib import Path
import sys
import time

ROOT=Path(__file__).resolve().parents[1];sys.path.insert(0,str(ROOT/'tools'))
spec=importlib.util.spec_from_file_location('headless',ROOT/'tools/headless-smoke.py')
h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--iso',type=Path,default=ROOT/'output-x64/Zenith.iso')
parser.add_argument('--keep',type=Path,required=True)
args=parser.parse_args();rows=[]
started=time.monotonic();m=h.Machine(args.keep,iso=args.iso,network=[])
try:
    assert m.wait('headless ready',timeout=600),'live boot'
    rows.append(dict(operation='boot',seconds=round(time.monotonic()-started,3)))
    for operation,command,marker in [
        ('idle','sleep 3; logger BENCH_IDLE','user: BENCH_IDLE'),
        ('filesystem','fsprobe /home/user; echo BENCH_FS=$? | logger','user: BENCH_FS=0'),
        ('logging','for i in 1 2 3 4 5 6 7 8 9 10; do logger serial-benchmark-$i; done; logger BENCH_LOG','user: BENCH_LOG'),
        ('shutdown','poweroff','all filesystems unmounted')]:
        offset=len(m.log());before=m.serial.stat().st_size;start=time.monotonic()
        m.send(command+'\r');assert m.wait(marker,offset,180),operation
        rows.append(dict(operation=operation,seconds=round(time.monotonic()-start,3),serial_bytes=m.serial.stat().st_size-before))
    assert m.process.wait(timeout=30)==0,'clean poweroff'
finally:m.stop()
raw=m.log()
result=dict(iso=str(args.iso),seconds=round(time.monotonic()-started,3),serial_bytes=m.serial.stat().st_size,
            loop_traces=raw.count('[Kernel] Calling Run()')+raw.count('[Kernel] Run() returned'),
            allocation_traces=raw.count('[PageAllocator] AllocPages - Type:'),operations=rows)
(args.keep/'benchmark.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result,indent=2))
