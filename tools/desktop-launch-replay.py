#!/usr/bin/env python3
"""No-build Welcome/System launcher checks on a copy of a failed Settings fixture."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
from types import SimpleNamespace
import boot_image

ROOT=Path(__file__).resolve().parents[1]
def load(name,path):
    spec=importlib.util.spec_from_file_location(name,path)
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module

def fixture(build):
    build=build.expanduser().resolve()
    disks=[p for p in (build/'obj/distributor').glob('release-*/native/a/desktop/disk.img') if p.is_file() and not p.is_symlink()]
    if not disks:raise ValueError('No retained Settings desktop fixture; use --disk and --elf for an active workspace')
    elf=build/'output-x64/Zenith.elf'
    if not elf.is_file():raise ValueError('Retained build ELF missing')
    return max(disks,key=lambda p:p.stat().st_mtime),elf

def audit(disk,elf,keep):
    state=boot_image.verify(disk,elf)
    partition=next(p for p in boot_image.partitions(disk) if str(p[1])==state['root'])
    root=keep/'root.ext2'
    with disk.open('rb') as source,root.open('wb') as target:
        source.seek(partition[2]*512);remaining=(partition[3]-partition[2]+1)*512
        while remaining:
            chunk=source.read(min(remaining,1048576))
            if not chunk:raise RuntimeError('incomplete root image')
            target.write(chunk);remaining-=len(chunk)
    result=subprocess.run(['e2fsck','-fn',str(root)],capture_output=True)
    (keep/'e2fsck.log').write_bytes(result.stdout+result.stderr)
    if result.returncode:raise RuntimeError('independent ext2 audit failed')

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build',type=Path,help='retained distributor build; select its latest Settings desktop fixture and exact ELF')
    parser.add_argument('--disk',type=Path,help='retained Settings desktop fixture, including desktop-tools and /root/p')
    parser.add_argument('--elf',type=Path,help='exact ELF from the failed build')
    parser.add_argument('--keep',type=Path,help='new short evidence directory')
    parser.add_argument('--cycles',type=int,default=5)
    args=parser.parse_args()
    if not 1<=args.cycles<=20:parser.error('--cycles must be 1..20')
    if args.build:
        if args.disk or args.elf:parser.error('use --build or explicit --disk/--elf')
        try:args.disk,args.elf=fixture(args.build)
        except ValueError as error:parser.error(str(error))
    if not args.disk or not args.elf:parser.error('--build or both --disk and --elf are required')
    boot_image.verify(args.disk,args.elf)
    keep=args.keep.resolve() if args.keep else Path(tempfile.mkdtemp(prefix='zn-launch-',dir='/var/tmp'))/'replay'
    if len(str(keep/'desktop/serial.sock').encode())>100:parser.error('--keep path too long')
    keep.mkdir(parents=True,exist_ok=False)
    print('Original fixture preserved: '+str(args.disk.resolve()),flush=True)
    print('Replay evidence: '+str(keep),flush=True)
    version=subprocess.check_output([os.environ.get('QEMU','qemu-system-x86_64'),'--version'],text=True,timeout=10).splitlines()[0]
    print(version,flush=True)
    h=load('launch_headless',ROOT/'tools/headless-smoke.py')
    login=load('launch_login',ROOT/'tools/login-session-smoke.py')
    policy=load('launch_input',ROOT/'deploy/distributor/validation-input.py')
    terminal=load('launch_terminal',ROOT/'deploy/distributor/validation-terminal.py')
    load('launch_accel',ROOT/'deploy/distributor/validation-accelerator.py').install(h)
    load('launch_profile',ROOT/'deploy/distributor/validation-profile.py').install(h.Machine,elf=args.elf,harness='native-settings-smoke.py')
    policy.DIAGNOSTIC_ELF=args.elf;policy.install(h.Machine)
    monitor=keep/'monitor.sock';buttons=[];keys=[];m=None;started=time.monotonic()
    def transport(path,text):
        if text.startswith('mouse_button '):buttons.append(text)
        if text.startswith('sendkey '):keys.append(text)
        return policy.monitor_text(path,text)
    mouse=policy.RenderedMouse(monitor,transport)
    def require(marker,off=0,timeout=30):
        if not m.wait(marker,off,timeout):raise RuntimeError('missing '+marker)
    def move(x,y):
        (cx,cy),_=mouse.position();mouse.command('mouse_move '+str(x-cx)+' '+str(y-cy))
    try:
        devices=['-netdev','user,id=n','-device','e1000e,netdev=n','-device','virtio-gpu-pci','-device','virtio-keyboard-pci']
        m=h.Machine(keep/'desktop',args.disk,'session=de',network=devices,monitor=monitor)
        require('desktop ready (installed)',timeout=180);terminal.install(login,[m],policy)
        # Same initial cursor presentation boundary as the full Settings gate.
        deadline=time.monotonic()+20
        while True:
            try:_,(width,height)=mouse.position();break
            except RuntimeError:
                if time.monotonic()>=deadline:raise
                time.sleep(.05)
        for cycle in range(args.cycles):
            for row,name in ((0,'Welcome'),(5,'System')):
                move(32,height-32);mouse.command('mouse_button 1');mouse.command('mouse_button 0')
                deadline=time.monotonic()+20
                while True:
                    data=mouse.capture(keep/'launcher.ppm');w,ht,rgb=policy.frame_pixels(data)
                    point=((ht-480)*w+250)*3
                    if rgb[point:point+3]==bytes.fromhex('2a2c31'):break
                    if time.monotonic()>=deadline:raise RuntimeError('launcher menu not presented')
                    time.sleep(.05)
                move(130,height-480+row*46);off=len(m.log())
                transport(monitor,'mouse_button 1')
                try:require('native surface opened 600x480',off)
                finally:transport(monitor,'mouse_button 0')
                # Blocks on actual enabled native Close pixels and focused chrome.
                # The full gate's old shared-colour predicate accepted Terminal here.
                mouse.capture(keep/('launcher-native-'+str(row)+'.ppm'))
                off=len(m.log());transport(monitor,'sendkey esc 30')
                require('native surface retired',off)
                print('PASS launcher '+name+' cycle '+str(cycle+1),flush=True)
        if buttons!=['mouse_button 1','mouse_button 0']*(args.cycles*4):raise RuntimeError('button sequence changed')
        if keys!=['sendkey esc 30']*(args.cycles*2):raise RuntimeError('Escape sequence changed')
        off=len(m.log());login.monitor_type(monitor,'sh /root/p\n')
        require('all filesystems unmounted',off,60)
        if m.process.wait(timeout=30)!=0:raise RuntimeError('unclean QEMU exit')
        audit(m.disk,args.elf,keep)
        result=dict(cycles=args.cycles,launches=args.cycles*2,button_commands=len(buttons),escape_commands=len(keys),
                    seconds=round(time.monotonic()-started,3),qemu=version,elf_sha256=hashlib.sha256(args.elf.read_bytes()).hexdigest())
        (keep/'result.json').write_text(json.dumps(result,indent=2)+'\n')
        print('PASS launcher readiness, single Escape retirement, clean poweroff and independent ext2 audit',flush=True)
    except BaseException:
        if m is not None:
            try:mouse.capture(keep/'wait-failure.ppm')
            except (OSError,RuntimeError,ValueError):pass
            try:load('launch_failure',ROOT/'deploy/distributor/validation-diagnostics.py').capture(SimpleNamespace(directory=keep),'MERIDIAN_INPUT',args.elf)
            except (OSError,RuntimeError,ValueError):pass
        raise
    finally:
        if m is not None:m.stop()

if __name__=='__main__':main()
