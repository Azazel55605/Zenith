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
        # QEMU paces typed keys but not mouse commands: let the keys arrive first.
        time.sleep(1)
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
        # A test animation plays and ends on exactly the frame it started from.
        off=len(m.raw_log());m.type('sleep 3; fps motion; sleep 5; logger MOTION_DONE\n');time.sleep(1.5)
        _,_,before=profile.frame_pixels(observer.capture(args.keep/'motion-before.ppm'))
        assert m.wait_for('test frames=',30,off),'motion test result'
        report=[line for line in m.raw_log()[off:].splitlines() if 'test frames=' in line][0];print(report,flush=True)
        assert 'ended=Finished' in report or 'ended=OverBudget' in report,report
        time.sleep(.5);w,h,after=profile.frame_pixels(observer.capture(args.keep/'motion-after.ppm'))
        changed=sum(before[i:i+3]!=after[i:i+3] for i in range(0,len(before),3))
        # Only the blinking cursor and the taskbar clock may differ.
        assert changed<2000,'scene not restored: '+str(changed)+' pixels differ'
        assert m.wait_for('user: MOTION_DONE',30,off),'terminal after motion'
        print('PASS test animation restores the scene',flush=True)
        # The cached scene below the top window must equal a full redraw, also after a lower window changed.
        command('settings set materials background-blur; logger MATERIALS_ON','user: MATERIALS_ON')
        m.type('sleep 6; echo LOWER-CHANGED\n');time.sleep(1);m.type('{ctrl-alt-t}');time.sleep(3);m.type('{ctrl-alt-t}');time.sleep(3)
        off=len(m.raw_log());m.type('sleep 6; fps bench\n')
        assert m.wait_for('bench windows=',90,off),'compose bench'
        report=[line for line in m.raw_log()[off:].splitlines() if 'bench windows=' in line][0];print(report,flush=True)
        assert ' verify=0 ' in report,report
        print('PASS cached lower scene equals a full redraw',flush=True)
        # Each window change animates, ends on its own, and leaves the real state behind.
        def played(action,name):
            off=len(m.raw_log());action();assert m.wait_for(name+' frames=',30,off),name
            report=[line for line in m.raw_log()[off:].splitlines() if name+' frames=' in line][0];print(report,flush=True)
            assert 'ended=Finished' in report or 'ended=OverBudget' in report,report
            time.sleep(.5)
        def top():
            off=len(m.raw_log());m.type('fps | logger\n');assert m.wait_for('pill: ',30,off),'top window'
            words=[line for line in m.raw_log()[off:].splitlines() if 'pill: ' in line][0].split('top window: ')[1].split()
            return [int(v) for v in words[:4]],[int(v) for v in words[5:9]]
        played(lambda:m.type('{ctrl-alt-t}'),'Opened');time.sleep(1)
        (x,y,w,h),pill=top()
        def button(b,n):return b[0]+b[2]-22-n*36,b[1]+22
        played(lambda:click(*button((x,y,w,h),1)),'Resized');assert top()[0]!=[x,y,w,h],'maximized'
        played(lambda:click(*button(top()[0],1)),'Resized');assert top()[0]==[x,y,w,h],'restored'
        played(lambda:click(*button((x,y,w,h),2)),'Minimized')
        played(lambda:click(pill[0]+pill[2]//2,pill[1]+pill[3]//2),'Restored');assert top()[0]==[x,y,w,h],'restored from pill'
        played(lambda:m.type('exit\n'),'Closed')
        print('PASS window changes animate and end in their real state',flush=True)
        command('settings set animations off; logger ANIMATIONS_OFF','user: ANIMATIONS_OFF')
        off=len(m.raw_log());m.type('{ctrl-alt-t}');time.sleep(3)
        assert 'Opened frames=' not in m.raw_log()[off:],'animation played while off'
        command('settings set animations normal; logger ANIMATIONS_ON','user: ANIMATIONS_ON')
        print('PASS animations off keeps window changes immediate',flush=True)
        command('poweroff','all filesystems unmounted');assert m.wait_exit(30)
        print('PASS shared system-app styling and normal teardown',flush=True)
    finally:
        m.stop();shutil.copyfile(m.serial,args.keep/'serial.log');shutil.rmtree(m.workdir,ignore_errors=True)
if __name__=='__main__':main()
