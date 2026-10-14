#!/usr/bin/env python3
"""Install once, then repeatedly boot UEFI and verify early IDT interrupt state."""
from pathlib import Path
import importlib.util
import sys

ROOT=Path(__file__).resolve().parents[1];sys.path.insert(0,str(ROOT/'tools'))
spec=importlib.util.spec_from_file_location('headless',ROOT/'tools/headless-smoke.py')
h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)
keep=Path(sys.argv[1] if len(sys.argv)>1 else '/tmp/zn-idt');keep.mkdir(parents=True,exist_ok=True)
net=['-netdev','user,id=net0','-device','e1000e,netdev=net0']
checks=0

def check(ok,label):
    global checks
    if not ok:raise RuntimeError(label)
    checks+=1;print('ok   '+label,flush=True)

for boot in range(4):
    m=h.Machine(keep/('live' if boot==0 else 'prepared-'+str(boot)),None if boot==0 else disk,network=net)
    try:
        check(m.wait('headless ready',timeout=600),'boot '+str(boot)+' headless')
        check(m.wait('network: online address=10.0.2.15',timeout=90),'boot '+str(boot)+' DHCP')
        def command(text,marker):
            offset=len(m.log());m.send(text+'\r')
            check(m.wait(marker,offset,180),'boot '+str(boot)+' '+marker)
        command('cpu status | logger','user: initial-idt-interrupts-enabled=False')
        command('cpu irq-test | logger; echo IDT=$? | logger','user: IDT=0')
        if boot==0:
            command('session set-default headless; install sata0 --yes; echo INSTALL=$? | logger','user: INSTALL=0')
        else:
            command('cat /proc/mounts | grep " / ext2 " | logger',' / ext2 rw')
        command('poweroff','all filesystems unmounted')
        check(m.process.wait(timeout=30)==0,'boot '+str(boot)+' clean poweroff')
        disk=m.disk
    finally:m.stop()
print('RESULT: '+str(checks)+' IDT/live/installed boot checks passed',flush=True)
