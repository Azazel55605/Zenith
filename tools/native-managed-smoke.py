#!/usr/bin/env python3
"""Disposable C3 pinned managed jobs and stopped-root collection acceptance."""
import argparse
import importlib.util
import json
from pathlib import Path
import re
import shutil
import socket
import subprocess
import tempfile
import time
import sys
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'tools'))
spec=importlib.util.spec_from_file_location('c3_headless',ROOT/'tools/headless-smoke.py')
h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)

def observe(monitor,folder,cpus):
    result=[]
    try:
        with socket.socket(socket.AF_UNIX) as s:
            s.settimeout(1);s.connect(str(monitor));time.sleep(.1);result.append(s.recv(65536).decode(errors='replace'))
            for command in ['info cpus']+[v for cpu in range(cpus) for v in ('cpu '+str(cpu),'info registers')]:
                s.sendall((command+'\n').encode());time.sleep(.1);result.append(s.recv(65536).decode(errors='replace'))
            symbols=subprocess.check_output(['nm','--defined-only',str(ROOT/'output-x64/Zenith.elf')],text=True)
            for name in ('owner','request','participants','sequence','stopped_epochs','refused','deferred','failed'):
                rows=[line.split() for line in symbols.splitlines() if line.split()[-1]==name]
                if len(rows)==1:
                    command='xp /1gx 0x'+rows[0][0]
                    # Native kernel BSS is mapped at the standard kernel virtual
                    # base; HMP x reads virtual memory on the selected CPU.
                    command='x /1gx 0x'+rows[0][0]
                    s.sendall((command+'\n').encode());time.sleep(.05);result.append(name+': '+s.recv(65536).decode(errors='replace'))
    except Exception as error:result.append('monitor: '+str(error))
    (folder/'managed-failure.txt').write_text('\n'.join(result))

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('keep',type=Path);p.add_argument('--cpus',type=int,nargs='+',default=[1,2,4]);p.add_argument('--ui-only',action='store_true',help='focused graphical check; not full C3 acceptance');a=p.parse_args()
    a.keep.mkdir(parents=True,exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='zn-managed-') as temp:
        t=Path(temp);conf=t/'limine.conf';iso=t/'managed.iso'
        with (a.keep/'iso.log').open('w') as log:
            subprocess.run(['xorriso','-osirrox','on','-indev',str(ROOT/'output-x64/Zenith.iso'),'-extract','/boot/limine/limine.conf',str(conf)],check=True,stdout=log,stderr=log)
            content,n=re.subn('cmdline: root=auto','cmdline: root=live smp=native',conf.read_text(),count=1)
            if n!=1:raise RuntimeError('live ISO configuration changed')
            conf.write_text(content.replace('timeout: 5','timeout: 0'));shutil.copyfile(ROOT/'output-x64/Zenith.iso',iso)
            subprocess.run(['xorriso','-boot_image','any','keep','-dev',str(iso),'-map',str(conf),'/boot/limine/limine.conf','-commit_eject','all'],check=True,stdout=log,stderr=log)
        timings={}
        for count in ([] if a.ui_only else a.cpus):
            folder=a.keep/str(count);monitor=folder/'monitor.sock'
            m=h.Machine(folder,cpus=count,iso=iso,network=[],monitor=monitor)
            try:
                assert m.wait('headless ready',timeout=120),'managed fixture boot'
                if count>1:
                    off=len(m.log());m.send("cpu managed-test > /tmp/m-output; echo M_MANAGED=$? > /tmp/m-status; cat /tmp/m-output | logger; cat /tmp/m-status | logger\r")
                    assert m.wait('user: M_MANAGED=',off,timeout=90),'managed completion status'
                    assert 'user: M_MANAGED=0' in m.log()[off:],'managed root/collection test'
                    assert 'pinned managed allocation, stopped roots, TLS and identity verified' in m.log()[off:],'managed completion proof'
                    print('PASS pinned managed roots/allocations/identity at '+str(count)+' CPUs',flush=True)
                off=len(m.log());m.send("cpu managed-image-test > /tmp/m-output; echo M_IMAGE=$? > /tmp/m-status; cat /tmp/m-output | logger; cat /tmp/m-status | logger\r")
                assert m.wait('user: M_IMAGE=',off,timeout=120),'managed frame completion'
                raw=m.log()[off:]
                assert 'user: M_IMAGE=0' in raw,'managed frame producer status'
                result=re.search(r'managed image verified; tiles=12 pixels=4096 rounds=32 cpus=(\d+) elapsed-us=(\d+) hash=([0-9a-f]+)',raw)
                assert result and int(result[1])==count,'managed equal-work frame proof'
                timings[count]=dict(microseconds=int(result[2]),hash=result[3])
                print('PASS equal-work managed frames: '+str(count)+' CPUs '+str(timings[count]),flush=True)
                if count>1:
                    off=len(m.log());m.send("cpu managed-fault-test > /tmp/m-output; echo M_FAULT=$? > /tmp/m-status; cat /tmp/m-output | logger; cat /tmp/m-status | logger\r")
                    assert m.wait('user: M_FAULT=',off,timeout=90),'managed fault completion'
                    assert 'user: M_FAULT=0' in m.log()[off:],'managed fault/refusal proof'
                    print('PASS managed fault quarantine and fail-closed collection at '+str(count)+' CPUs',flush=True)
                off=len(m.log());m.send('poweroff\r');assert m.wait('all filesystems unmounted',off,timeout=60),'managed shutdown'
                assert m.process.wait(timeout=30)==0,'managed clean poweroff'
            except BaseException:
                observe(monitor,folder,count)
                print(m.log()[-5000:],flush=True);raise
            finally:
                if m.process.poll() is None:m.process.terminate();m.process.wait(timeout=10)
        for label,enabled in ([] if a.ui_only else [('disabled',False),('shutdown',True)]):
            count=max(a.cpus)
            if enabled and count<2:continue
            folder=a.keep/label;monitor=folder/'monitor.sock'
            m=h.Machine(folder,cpus=count,iso=iso if enabled else ROOT/'output-x64/Zenith.iso',network=[],monitor=monitor)
            try:
                assert m.wait('headless ready',timeout=120),label+' boot'
                off=len(m.log())
                if enabled:
                    m.send('cpu managed-hold > /tmp/out; echo M_HOLD=$? | logger\r')
                    assert m.wait('user: M_HOLD=0',off,timeout=30),'held shutdown fixture'
                else:
                    m.send('cpu managed-image-test > /tmp/out; echo M_DISABLED=$? | logger\r')
                    assert m.wait('user: M_DISABLED=1',off,timeout=30),'default managed AP admission must remain disabled'
                off=len(m.log());m.send('poweroff\r')
                assert m.wait('all filesystems unmounted',off,timeout=60),label+' clean shutdown'
                assert m.process.wait(timeout=30)==0,label+' poweroff'
                print('PASS '+label+' managed admission/quiescence boundary',flush=True)
            finally:
                if m.process.poll() is None:m.process.terminate();m.process.wait(timeout=10)
        # Exercise the actual trusted UI consumer. Only the BSP assembles the
        # complete frame, opens/paints a window and processes focused input.
        def load(name,path):
            spec=importlib.util.spec_from_file_location(name,path)
            module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module
        login=load('managed_login',ROOT/'tools/login-session-smoke.py')
        policy=load('managed_input',ROOT/'deploy/distributor/validation-input.py')
        terminal=load('managed_terminal',ROOT/'deploy/distributor/validation-terminal.py')
        import guest_cpu_frame,recovery_console
        rows=guest_cpu_frame.expected()
        gui_iso=t/'managed-gui.iso';gui_conf=t/'gui.conf'
        gui_conf.write_text(conf.read_text().replace('root=live smp=native','root=live smp=native session=de'))
        shutil.copyfile(iso,gui_iso)
        with (a.keep/'gui-iso.log').open('w') as log:
            subprocess.run(['xorriso','-boot_image','any','keep','-dev',str(gui_iso),'-map',str(gui_conf),'/boot/limine/limine.conf','-commit_eject','all'],check=True,stdout=log,stderr=log)
        ui_times={}
        for count in a.cpus:
            folder=a.keep/('gui-'+str(count));monitor=folder/'monitor.sock'
            m=h.Machine(folder,cpus=count,iso=gui_iso,network=['-device','virtio-gpu-pci','-device','virtio-keyboard-pci'],monitor=monitor)
            try:
                assert m.wait('desktop ready',timeout=120),'managed desktop boot'
                recovery_console.monitor_command(monitor,'sendkey ctrl-alt-shift-t 50')
                observer=policy.RenderedMouse(monitor,policy.monitor_text)
                guest_cpu_frame.prompt(observer,terminal)
                if count > 1:
                    # Collection from a command worker must retain locals on
                    # the suspended boot/UI stack, not only allocated stacks.
                    for attempt in range(3):
                        off=len(m.log());login.monitor_type(monitor,'cpu managed-test | logger\n')
                        assert m.wait('user: managed pinned/interior roots, deferred collection, exceptions and cancellation verified',off,timeout=90),'GUI managed collection '+str(attempt)
                        guest_cpu_frame.prompt(observer,terminal)
                    print('PASS repeated command-worker GC with live recovery Terminal: '+str(count)+' CPUs',flush=True)
                # One deliberate button-free move away from the future frame;
                # never correct motion from screenshot deltas or replay a click.
                recovery_console.monitor_command(monitor,'mouse_move -2048 -2048')
                off=len(m.log());login.monitor_type(monitor,'fps cpu\n')
                assert m.wait('UI owner published complete managed frame',off,timeout=30),'UI frame publication'
                deadline=time.monotonic()+20
                while not guest_cpu_frame.locate(observer.capture(folder/'frame.ppm'),rows):
                    if time.monotonic()>deadline:raise RuntimeError('independent complete frame not presented')
                    time.sleep(.1)
                off=len(m.log());started=time.monotonic();recovery_console.monitor_command(monitor,'sendkey a 30')
                assert m.wait('managed frame preview input acknowledged',off,timeout=5),'preview input'
                ui_times[count]=dict(input_ack_seconds=round(time.monotonic()-started,3))
                off=len(m.log());recovery_console.monitor_command(monitor,'sendkey esc 30')
                assert m.wait('managed frame preview closed',off,timeout=5),'preview close'
                guest_cpu_frame.prompt(observer,terminal)
                off=len(m.log());login.monitor_type(monitor,'poweroff\n')
                assert m.wait('all filesystems unmounted',off,timeout=60),'preview clean shutdown'
                assert m.process.wait(timeout=30)==0,'preview poweroff'
                print('PASS rendered complete managed frame and focused input: '+str(count)+' CPUs '+str(ui_times[count]),flush=True)
            finally:
                if m.process.poll() is None:m.process.terminate();m.process.wait(timeout=10)
        (a.keep/'ui-times.json').write_text(json.dumps(ui_times,indent=2)+'\n')
        assert not timings or len({v['hash'] for v in timings.values()})==1,'CPU-count-dependent pixels'
        (a.keep/'managed-times.json').write_text(json.dumps(timings,indent=2)+'\n')
    print('PASS focused C3 graphical checks (not full acceptance)' if a.ui_only else 'PASS C3 managed CPU fixtures',flush=True)
if __name__=='__main__':main()
