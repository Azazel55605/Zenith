#!/usr/bin/env python3
"""Replay the failed Settings maximize input on a copied dark/teal disposable fixture."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import subprocess
import tempfile
import time
import boot_image

ROOT=Path(__file__).resolve().parents[1]

def load(name,path):
    spec=importlib.util.spec_from_file_location(name,path)
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module


def restored_geometry(w,ht,rgb):
    needle=bytes.fromhex('7ecac7')*130
    for row in range(61,ht-420):
        at=rgb[row*w*3:(row+1)*w*3].find(needle)
        if at<0 or at%3:continue
        x,y=at//3-31,row-60
        def color(dx,dy):return rgb[((y+dy)*w+x+dx)*3:((y+dy)*w+x+dx)*3+3]
        if 0<x and 44<y and x+600<=w and color(80,59)!=bytes.fromhex('7ecac7') and color(80,61)==bytes.fromhex('2a2c31'):
            return x,y,w,ht
    return None

def maximized_chrome(w,ht,rgb):
    def pixel(x,y):return rgb[(y*w+x)*3:(y*w+x)*3+3]
    # Full-screen restore icon and close X are host-rendered, so the check is
    # independent of which native Settings page Ctrl+F left visible.
    cx,cy=w-58,22;ink=pixel(cx-3,cy-6)
    if max(abs(a-b) for a,b in zip(ink,pixel(cx,cy-10)))<30:return False
    return (all(pixel(cx+i,cy-6)==ink for i in range(-3,5)) and
            all(pixel(cx+5,cy+i)==ink for i in range(-6,2)) and
            all(pixel(w-22+i,22+i)==ink and pixel(w-22+i,22-i)==ink for i in range(-4,5)))


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--disk',type=Path,required=True,help='retained failed desktop disk, with /root/m and installed Settings')
    parser.add_argument('--elf',type=Path,required=True,help='exact fixture ELF; kernel is not compiled')
    parser.add_argument('--keep',type=Path,help='new short evidence directory')
    parser.add_argument('--cycles',type=int,default=3)
    args=parser.parse_args()
    if not 1<=args.cycles<=100:parser.error('--cycles must be 1..100')
    started=time.monotonic()
    boot_image.verify(args.disk,args.elf)
    keep=args.keep.resolve() if args.keep else Path(tempfile.mkdtemp(prefix='zn-max-',dir='/var/tmp'))/'replay'
    if len(str(keep/'desktop/serial.sock').encode())>100:parser.error('--keep path too long')
    keep.mkdir(parents=True,exist_ok=False)
    print('Original fixture preserved: '+str(args.disk.resolve()),flush=True)
    print('Replay evidence: '+str(keep),flush=True)
    version=subprocess.run([os.environ.get('QEMU','qemu-system-x86_64'),'--version'],capture_output=True,text=True,check=True,timeout=10).stdout.splitlines()[0]
    print(version,flush=True)
    h=load('max_headless',ROOT/'tools/headless-smoke.py')
    login=load('max_login',ROOT/'tools/login-session-smoke.py')
    policy=load('max_input',ROOT/'deploy/distributor/validation-input.py')
    terminal=load('max_terminal',ROOT/'deploy/distributor/validation-terminal.py')
    load('max_accelerator',ROOT/'deploy/distributor/validation-accelerator.py').install(h)
    policy.install(h.Machine);policy.DIAGNOSTIC_ELF=args.elf.resolve()
    monitor=keep/'monitor.sock';mouse=policy.RenderedMouse(monitor,policy.monitor_text);m=None
    buttons=[];last_command={};phase='boot'
    original=mouse.raw
    def transport(path,text):
        if text.startswith('mouse_button '):buttons.append(text)
        if text.startswith(('mouse_move ','mouse_button ')):
            last_command.clear();last_command.update(command=text,phase=phase,time=time.time(),qemu_log_offset=(keep/'desktop/qemu.log').stat().st_size)
            with (keep/'mouse-commands.jsonl').open('a') as output:output.write(json.dumps(last_command)+'\n')
        return original(path,text)
    mouse.raw=transport
    def wait(marker,off=0):
        if not m.wait(marker,off,30):raise RuntimeError('missing '+marker)
    def frame(name,predicate):
        deadline=time.monotonic()+20
        while True:
            raw=mouse.capture(keep/(name+'.ppm'));w,ht,rgb=policy.frame_pixels(raw)
            found=predicate(w,ht,rgb)
            if found:return found
            if time.monotonic()>=deadline:raise RuntimeError('missing rendered '+name+' (fixture must retain dark/teal appearance)')
            time.sleep(.05)
    def focus_geometry():
        policy.monitor_text(monitor,'sendkey ctrl-f 30')
        return frame('search',restored_geometry)
    def move(x,y):
        (cx,cy),_=mouse.position();mouse.command('mouse_move '+str(x-cx)+' '+str(y-cy))
    def click(x,y):
        move(x,y);mouse.command('mouse_button 1');mouse.command('mouse_button 0')
    try:
        m=h.Machine(keep/'desktop',args.disk,'session=de',network=['-device','virtio-gpu-pci','-device','virtio-keyboard-pci','-trace','enable=virtio_input_queue_full'],monitor=monitor)
        wait('desktop ready (installed)');terminal.install(login,[m],policy)
        off=len(m.log());login.monitor_type(monitor,'sh /root/m\n');wait('MERIDIAN_FOCUSED',off)
        x,y,w,ht=focus_geometry()
        # Same bounded wheel and WIP-search prelude as the failed gate. Do not
        # repeat mutations after any missing acknowledgement.
        phase='wheel-search-warmup'
        for target in ((x+100,y+260),(x+350,y+260)):
            move(*target)
            for burst in range(8):
                for _ in range(24):policy.monitor_text(monitor,'mouse_move 0 0 '+('1' if burst%2 else '-1'))
        off=len(m.log());policy.monitor_text(monitor,'sendkey esc 30');time.sleep(.2);policy.monitor_text(monitor,'sendkey esc 30')
        wait('MERIDIAN_PAGE quick.overview',off);x,y,w,ht=focus_geometry()
        login.monitor_type(monitor,'bluetooth');policy.monitor_text(monitor,'sendkey ret 30');time.sleep(.2)
        off=len(m.log());policy.monitor_text(monitor,'sendkey ret 30');wait('MERIDIAN_PAGE devices.bluetooth',off)
        for cycle in range(args.cycles):
            phase='maximize-'+str(cycle+1)
            off=len(m.log());click(x+542,y-22);wait('MERIDIAN_RESIZED',off)
            frame('maximized',maximized_chrome)
            phase='restore-'+str(cycle+1)
            off=len(m.log());click(w-58,22);wait('MERIDIAN_RESIZED',off)
            x,y,w,ht=focus_geometry()
            print('PASS exact maximize/restore cycle '+str(cycle+1),flush=True)
        if buttons!=['mouse_button 1','mouse_button 0']*(args.cycles*2):raise RuntimeError('button sequence changed')
        result=dict(cycles=args.cycles,button_commands=len(buttons),seconds=round(time.monotonic()-started,3),qemu=version,elf_sha256=hashlib.sha256(args.elf.read_bytes()).hexdigest())
        (keep/'result.json').write_text(json.dumps(result,indent=2)+'\n')
        print('PASS focused maximize input replay; original fixture preserved',flush=True)
    except (OSError,RuntimeError,ValueError) as error:
        summary=dict(phase=phase,error=str(error),last_mouse_command=last_command.copy(),observation_only=True)
        try:
            path=keep/'pipeline-stall.json'
            if path.is_file() and path.stat().st_size<1024*1024:
                report=json.loads(path.read_text())
                for key in ('mouse_manager','mouse_manager_after','mouse_devices','mouse_devices_after'):
                    if key in report:summary[key]=report[key]
            if 'qemu_log_offset' in last_command:
                with (keep/'desktop/qemu.log').open('rb') as stream:
                    stream.seek(last_command['qemu_log_offset']);tail=stream.read(1024*1024)
                summary['queue_full_after_last_command']=tail.count(b'virtio_input_queue_full')
            index,active=policy.mouse_index(policy.monitor_text(monitor,'info mice'))
            summary.update(backend_mouse_index=index,backend_mouse_active=active)
            (keep/'failure-summary.json').write_text(json.dumps(summary,indent=2)+'\n')
            print('Focused mouse failure: '+json.dumps(summary),flush=True)
        except (OSError,RuntimeError,ValueError):pass
        raise
    finally:
        if m is not None:m.stop()

if __name__=='__main__':main()
