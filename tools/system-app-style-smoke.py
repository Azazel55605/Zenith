#!/usr/bin/env python3
"""Exercise themed Terminal/Editor and mouse save/dirty-chrome close on a disposable live VM."""
from pathlib import Path
import argparse
import importlib.util
import shutil
import time
import recovery_console
ROOT = Path(__file__).resolve().parents[1]
def load(name, path):
    spec=importlib.util.spec_from_file_location(name,path);module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('keep',type=Path);args=parser.parse_args()
    args.keep.mkdir(parents=True,exist_ok=False)
    smoke=load('app_style_smoke',ROOT/'tools/smoke-test.py')
    load('app_style_accel',ROOT/'deploy/distributor/validation-accelerator.py').install(smoke)
    profile=load('app_style_input',ROOT/'deploy/distributor/validation-input.py');profile.install_monitor(recovery_console)
    m,_=smoke.boot(ROOT/'output-x64/Zenith.iso',180,'system app styling')
    if not m:raise RuntimeError('desktop boot')
    observer=profile.RenderedMouse(m.monitor,recovery_console.monitor_command)
    def command(text,marker):
        off=len(m.log());m.type(text+'\n');assert m.wait_for(marker,60,off),marker
    def editor_frame(name,accent,surface):
        deadline=time.monotonic()+20
        while time.monotonic()<deadline:
            w,h,rgb=profile.frame_pixels(observer.capture(args.keep/(name+'.ppm')))
            for row_y in range(h):
                row=rgb[row_y*w*3:(row_y+1)*w*3];at=row.find(bytes.fromhex(accent)*100)
                if at<0 or at%3 or row[at+300:at+303]==bytes.fromhex(accent):continue
                x,y=at//3-200,row_y-16
                if x<0 or y<0 or x+720>w or y+436>h:continue
                p=((y+120)*w+x+400)*3
                if rgb[p:p+3]==bytes.fromhex(surface):return x,y
            time.sleep(.1)
        raise RuntimeError('Editor frame missing: '+name)
    def click(x,y):
        (cx,cy),_=observer.position();recovery_console.monitor_command(m.monitor,f'mouse_move {x-cx} {y-cy}')
        recovery_console.monitor_command(m.monitor,'mouse_button 1');recovery_console.monitor_command(m.monitor,'mouse_button 0')
    try:
        for mode,accent,surface in [('dark','8baaff','202125'),('light','3157b7','fafafb')]:
            command('settings set appearance '+mode+' blue; logger STYLE_'+mode,'user: STYLE_'+mode)
            observer.capture(args.keep/('terminal-'+mode+'.ppm'))
            m.type('edit /tmp/style-'+mode+'.txt\n')
            x,y=editor_frame('editor-'+mode,accent,surface)
            m.type('STYLE-'+mode);click(x+250,y+26);m.type('^Q')
            command('cat /tmp/style-'+mode+'.txt | logger','user: STYLE-'+mode)
            m.type('edit /tmp/style-'+mode+'.txt\n');x,y=editor_frame('editor-dirty-'+mode,accent,surface)
            m.type('{ctrl-end}-dirty');click(x+698,y-22)
            # If chrome discarded the document, these keys go to the terminal
            # and the persisted result cannot satisfy the following assertion.
            observer.capture(args.keep/('editor-guard-'+mode+'.ppm'));m.type('^S^Q')
            command('tail -n 1 /tmp/style-'+mode+'.txt | logger','user: -dirty')
            print('PASS '+mode+' Terminal/Editor, pointer Save and guarded chrome close',flush=True)
        command('poweroff','all filesystems unmounted');assert m.wait_exit(30)
        print('PASS shared system-app styling and normal teardown',flush=True)
    finally:
        m.stop();shutil.copyfile(m.serial,args.keep/'serial.log');shutil.rmtree(m.workdir,ignore_errors=True)
if __name__=='__main__':main()
