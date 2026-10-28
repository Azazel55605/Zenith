#!/usr/bin/env python3
"""Read-only source fixture replay of rapid Meridian wheel input; boots a disk copy."""
from pathlib import Path
import argparse, importlib.util, time
ROOT=Path(__file__).resolve().parents[1]
def load(name,path):
    s=importlib.util.spec_from_file_location(name,path);m=importlib.util.module_from_spec(s);s.loader.exec_module(m);return m

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--disk',type=Path,required=True);p.add_argument('--elf',type=Path,required=True);p.add_argument('--keep',type=Path,required=True);p.add_argument('--ps2',action='store_true');p.add_argument('--legacy',action='store_true');a=p.parse_args()
    import boot_image,recovery_console
    boot_image.verify(a.disk,a.elf)
    a.keep=a.keep.resolve();a.keep.mkdir(parents=True,exist_ok=False)
    h=load('scroll_headless',ROOT/'tools/headless-smoke.py');load('scroll_accel',ROOT/'deploy/distributor/validation-accelerator.py').install(h)
    profile=load('scroll_input',ROOT/'deploy/distributor/validation-input.py')
    if not a.ps2:profile.install(h.Machine)
    monitor=a.keep/'monitor.sock';net=['-device','virtio-gpu-pci']
    if not a.ps2:net+=['-device','virtio-keyboard-pci']
    m=h.Machine(a.keep/'desktop',a.disk,'session=de',network=net,monitor=monitor)
    raw=recovery_console.monitor_command
    try:
        assert m.wait('desktop ready (installed)',timeout=180)
        login=load('scroll_login',ROOT/'tools/login-session-smoke.py');time.sleep(1)
        login.monitor_type(monitor,'settings set appearance dark teal; settings set materials blur; sh /root/m\n')
        assert m.wait('MERIDIAN_FOCUSED',timeout=60),'Meridian admission'
        raw(monitor,'sendkey ctrl-f 30');time.sleep(.4)
        observer=profile.RenderedMouse(monitor,raw)
        data=observer.capture(a.keep/'before.ppm');w,hh,rgb=profile.frame_pixels(data)
        # Click-free wheel target in this native window; find the search border.
        xy=None
        for y in range(hh):
            at=rgb[y*w*3:(y+1)*w*3].find(bytes.fromhex('7ecac7')*(400 if a.legacy else 130))
            if at>=0 and at%3==0:
                left,top=at//3-(32 if a.legacy else 31),y-(58 if a.legacy else 60);xy=(left+350,top+250);break
        assert xy,'search frame was not composed'
        (cx,cy),_=observer.position();raw(monitor,f'mouse_move {xy[0]-cx} {xy[1]-cy}');time.sleep(.3)
        for burst in range(32):
            for n in range(24):raw(monitor,'mouse_move 0 0 '+('1' if burst%2 else '-1'))
            time.sleep(.05)
        print('Sent 768 rapid wheel movements',flush=True)
        time.sleep(2);observer.capture(a.keep/'after.ppm')
        off=len(m.log());raw(monitor,'sendkey esc 30');time.sleep(.3);raw(monitor,'sendkey esc 30')
        assert m.wait('MERIDIAN_PAGE quick.overview',off,30),'Meridian unresponsive after wheel stress'
        print('PASS wheel stress and subsequent keyboard navigation',flush=True)
        (cx,cy),_=observer.position();raw(monitor,f'mouse_move {left+280-cx} {top-22-cy}');time.sleep(.2)
        raw(monitor,'mouse_button 1')
        for n in range(128):raw(monitor,'mouse_move '+('16 12' if n%2 else '-16 -12'))
        raw(monitor,'mouse_button 0');time.sleep(.5)
        off=len(m.log());raw(monitor,'sendkey ctrl-f 30');assert m.wait('MERIDIAN_PAGE navigation',off,30),'keyboard after drag'
        print('PASS window movement and keyboard input after release',flush=True)
        off=len(m.log());raw(monitor,'sendkey ctrl-q 30');assert m.wait('S_MERIDIAN=0',off,30),'clean application exit'
        time.sleep(.5);raw(monitor,'mouse_move -200 -200');raw(monitor,'mouse_button 1');time.sleep(.15);raw(monitor,'mouse_button 0');time.sleep(.3);off=len(m.log());login.monitor_type(monitor,'echo WHEEL_SHELL_ALIVE | logger\n');assert m.wait('user: WHEEL_SHELL_ALIVE',off,20),'host shell response'
        print('PASS native client exit and host shell response; original fixture preserved',flush=True)
    finally:m.stop()
if __name__=='__main__':main()
