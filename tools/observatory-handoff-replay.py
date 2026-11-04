#!/usr/bin/env python3
"""No-build Observatory/Polaris handoff on a copy of a tagged graphics setup disk."""
import argparse
import importlib.util
from pathlib import Path
import sys
import time
import tempfile
import boot_image
import hashlib
import subprocess

ROOT=Path(__file__).resolve().parents[1]
def load(name,path):
    spec=importlib.util.spec_from_file_location(name,path);module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module

def fixture(build):
    build=build.expanduser().resolve()
    disks=list((build/'obj/distributor').glob('release-*/native/g/setup/disk.img'))
    if not disks:raise ValueError('No retained graphics setup disk; use --disk/--elf/--harness for an active workspace')
    return max(disks,key=lambda p:p.stat().st_mtime),build/'output-x64/Zenith.elf',build/'tools/native-graphics-smoke.py'

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--full',action='store_true',help='run the unchanged tagged desktop sequence and filesystem audit')
    p.add_argument('--build',type=Path,help='retained distributor checkout; select latest clean graphics setup fixture')
    p.add_argument('--disk',type=Path);p.add_argument('--elf',type=Path)
    p.add_argument('--harness',type=Path,help='exact tagged native-graphics-smoke.py')
    p.add_argument('--keep',type=Path);args=p.parse_args()
    if args.build:
        if args.disk or args.elf or args.harness:p.error('use --build or explicit --disk/--elf/--harness')
        try:args.disk,args.elf,args.harness=fixture(args.build)
        except ValueError as error:p.error(str(error))
    if not args.disk or not args.elf or not args.harness:p.error('--build or all of --disk/--elf/--harness are required')
    if args.keep is None:args.keep=Path(tempfile.mkdtemp(prefix='zn-observatory-',dir='/var/tmp'))/'replay'
    state=boot_image.verify(args.disk,args.elf);print('Verified exact fixture kernel: '+state['current'],flush=True)
    replay=load('observatory_graphics_replay',ROOT/'tools/graphical-input-replay.py')
    definitions,sequence=replay.desktop_plan(args.harness.read_text());fixture={};exec(definitions,fixture)
    scripts=fixture['scripts']
    actions=[('pkg install /root/polaris.zpk','G_POLARIS_PKG'),('pkg install /root/observatory.zpk','G_OBSERVATORY_PKG'),
        ('cpu test 10000000','G_POLARIS_CPU'),('cpu managed-test','G_POLARIS_MANAGED'),
        ('echo POLARIS_ALIVE | logger','G_POLARIS'),('native run observatory | logger & sleep 8; true','G_OBSERVATORY'),
        ('settings set materials background-blur','G_MATERIALS'),('settings set translucency 100','G_TRANSLUCENT'),
        ('native run observatory processes | logger & sleep 8; true','G_OBSERVATORY_PROCESSES')]
    for text,label in actions:
        if text+'; echo '+label+'=$? | logger' not in scripts:raise ValueError('Tagged fixture script missing: '+label)
    if 'poweroff' not in scripts:raise ValueError('Tagged poweroff fixture missing')
    args.keep.mkdir(parents=True,exist_ok=False);keep=args.keep.resolve();monitor=keep/'monitor.sock'
    print('Original setup fixture preserved: '+str(args.disk.resolve()),flush=True)
    print('Replay evidence: '+str(keep),flush=True)
    h=load('observatory_headless',ROOT/'tools/headless-smoke.py');login=load('observatory_login',ROOT/'tools/login-session-smoke.py')
    load('observatory_accelerator',ROOT/'deploy/distributor/validation-accelerator.py').install(h)
    policy=load('observatory_input',ROOT/'deploy/distributor/validation-input.py');policy.install(h.Machine)
    terminal=load('observatory_terminal',ROOT/'deploy/distributor/validation-terminal.py')
    graphics=load('observatory_handoff',ROOT/'deploy/distributor/validation-graphics.py')
    load('observatory_profile',ROOT/'deploy/distributor/validation-profile.py').install(h.Machine,elf=args.elf,harness='native-graphics-smoke.py')
    import recovery_console
    policy.install_monitor(recovery_console)
    machines=[];graphics.install_monitor(recovery_console,machines,policy,terminal)
    command=recovery_console.monitor_command;observer=policy.RenderedMouse(monitor,policy.monitor_text)
    m=None
    def prompt():
        previous=observer.capture(observer.snapshot);deadline=time.monotonic()+15
        while time.monotonic()<deadline:
            time.sleep(.1);current=observer.capture(observer.snapshot);box=terminal.caret_box(previous,current)
            if box is not None and terminal.public_prompt(current,box):return
            previous=current
        raise RuntimeError('Focused public prompt unavailable; no script sent')
    def status(text,label):
        prompt();off=len(m.log());login.monitor_type(monitor,'sh /root/'+scripts[text+'; echo '+label+'=$? | logger']+'\n')
        if not m.wait('user: '+label+'=0',off,180):raise RuntimeError(label+' did not complete')
        print('PASS '+label,flush=True);return off
    try:
        m=h.Machine(keep/'desktop',args.disk,'session=de smp=native',cpus=4,monitor=monitor,
            network=['-netdev','user,id=n','-device','e1000e,netdev=n','-device','virtio-gpu-pci','-device','virtio-keyboard-pci'])
        machines.append(m)
        if not m.wait('desktop ready (installed)',timeout=180):raise RuntimeError('desktop startup')
        if args.full:
            from guest_graphics import terminal_paste
            env=dict(m=m,monitor=monitor,keep=keep,ROOT=args.elf.resolve().parent.parent,
                login=login,time=time,subprocess=subprocess,hashlib=hashlib,checks=0,gui=True,
                multicore=True,terminal_paste=terminal_paste,monitor_command=command)
            exec(definitions,env)
            def tagged_command(text,marker,timeout=180):
                time.sleep(.5);off=len(m.log());login.monitor_type(monitor,'sh /root/'+scripts[text]+'\n')
                if not m.wait(marker,off,timeout):raise RuntimeError('Tagged desktop command missing '+marker)
                env['checks']+=1;print('PASS '+marker,flush=True);return m.log()[off:]
            def tagged_status(text,label,code=0):
                return tagged_command(text+'; echo '+label+'=$? | logger','user: '+label+'='+str(code))
            env.update(command=tagged_command,status=tagged_status)
            exec(sequence,env)
            boot_image.verify(m.disk,args.elf)
            print('PASS full unchanged tagged desktop sequence; original disk preserved',flush=True)
            return
        command(monitor,'sendkey ctrl-alt-shift-t 30')
        for text,label in actions[:1]:status(text,label)
        off=len(m.log());command(monitor,'sendkey ctrl-alt-t 30')
        if not m.wait('graphics: native surface opened 640x480',off,60):raise RuntimeError('Polaris did not open')
        for text,label in actions[2:5]:status(text,label)
        status(*actions[1])
        off=status(*actions[5])
        if 'user: OBSERVATORY_READY' not in m.log()[off:]:raise RuntimeError('Observatory did not start')
        command(monitor,'screendump '+str(keep/'observatory.ppm'))
        command(monitor,'sendkey esc 30')
        status('echo POLARIS_ALIVE | logger','G_POLARIS')
        for text,label in actions[6:8]:status(text,label)
        off=status(*actions[8])
        if 'user: OBSERVATORY_READY' not in m.log()[off:]:raise RuntimeError('Observatory Processes did not start')
        command(monitor,'screendump '+str(keep/'observatory-processes.ppm'))
        command(monitor,'sendkey esc 30')
        status('echo POLARIS_ALIVE | logger','G_POLARIS')
        print('PASS both legacy-client handoffs; client focus marker present='+str('user: OBSERVATORY_FOCUSED' in m.log()[off:]),flush=True)
        prompt();off=len(m.log());login.monitor_type(monitor,'sh /root/'+scripts['poweroff']+'\n')
        if not m.wait('all filesystems unmounted',off,60) or m.process.wait(timeout=30)!=0:raise RuntimeError('clean shutdown')
        audit=load('observatory_disk_audit',ROOT/'tools/desktop-launch-replay.py');audit.audit(m.disk,args.elf,keep)
        print('PASS exact tagged Observatory/Polaris replay and independent ext2 audit; original disk preserved',flush=True)
    finally:
        if m is not None:m.stop()
if __name__=='__main__':main()
