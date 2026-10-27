#!/usr/bin/env python3
"""No-build diagnostic replay of the disposable graphical fixture on a disk copy."""
import argparse
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import time
import boot_image
from guest_graphics import check

ROOT=Path(__file__).resolve().parents[1]
def load(name,path):
    spec=importlib.util.spec_from_file_location(name,path);module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--disk',type=Path,required=True);parser.add_argument('--elf',type=Path,required=True)
    parser.add_argument('--mouse',choices=('virtio','ps2'),default='virtio');args=parser.parse_args()
    state=boot_image.verify(args.disk,args.elf)
    print('Verified fixture kernel SHA256: '+state['current'],flush=True)
    keep=Path(tempfile.mkdtemp(prefix='zn-gui-',dir='/var/tmp'));print('Replay evidence: '+str(keep),flush=True)
    print(subprocess.check_output([os.environ.get('QEMU','qemu-system-x86_64'),'--version'],text=True).splitlines()[0],flush=True)
    h=load('headless',ROOT/'tools/headless-smoke.py');login=load('login',ROOT/'tools/login-session-smoke.py')
    load('accelerator',ROOT/'deploy/distributor/validation-accelerator.py').install(h)
    if args.mouse=='virtio':load('input_profile',ROOT/'deploy/distributor/validation-input.py').install(h.Machine)
    monitor=keep/'monitor.sock';m=None
    try:
        m=h.Machine(keep/'desktop',args.disk,'session=de',network=['-device','virtio-gpu-pci','-device','virtio-keyboard-pci'],monitor=monitor)
        assert m.wait('desktop ready (installed)',timeout=180),'desktop startup'
        for script,marker in (('g0','G_PROBE=0'),('g1','G_EXIT=0'),('g2','G_FAULT=139'),('g3','G_AFTER_FAULT=0')):
            time.sleep(.5);off=len(m.log());login.monitor_type(monitor,'sh /root/'+script+'\n')
            assert m.wait('user: '+marker,off,30),marker
        def start():
            time.sleep(.5);off=len(m.log());login.monitor_type(monitor,'sh /root/g4\n');return off
        check(m,monitor,keep,start)
        print('PASS no-build graphical input replay ('+args.mouse+'); original disk preserved',flush=True)
    finally:
        if m is not None:m.stop()
if __name__=='__main__':main()
