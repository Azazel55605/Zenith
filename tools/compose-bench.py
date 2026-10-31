#!/usr/bin/env python3
"""Report compose timings with one and three windows in a disposable VM."""
from pathlib import Path
import argparse
import importlib.util
import shutil
import time
ROOT = Path(__file__).resolve().parents[1]
def load(name, path):
    spec=importlib.util.spec_from_file_location(name,path);module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('keep',type=Path);args=parser.parse_args()
    args.keep.mkdir(parents=True,exist_ok=False)
    smoke=load('bench_smoke',ROOT/'tools/smoke-test.py')
    load('bench_accel',ROOT/'deploy/distributor/validation-accelerator.py').install(smoke)
    m,_=smoke.boot(ROOT/'output-x64/Zenith.iso',180,'compose bench')
    if not m:raise RuntimeError('desktop boot')
    def bench():
        off=len(m.raw_log());m.type('sleep 2; fps bench\n')
        assert m.wait_for('bench windows=',60,off),'bench result'
        print([line for line in m.raw_log()[off:].splitlines() if 'bench windows=' in line][0],flush=True);time.sleep(1)
    try:
        m.type('settings set materials background-blur\n');time.sleep(2)
        bench()
        for _ in range(2):
            m.type('{ctrl-alt-t}');time.sleep(3)
        bench()
        m.screenshot(args.keep/'three.ppm')
    finally:
        m.stop();shutil.copyfile(m.serial,args.keep/'serial.log');shutil.rmtree(m.workdir,ignore_errors=True)
if __name__=='__main__':main()
