#!/usr/bin/env python3
"""C4 real material filter and UI composition, on disposable 1/2/4-CPU boots."""
import argparse
import importlib.util
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'tools'))
def load(name,path):
    spec=importlib.util.spec_from_file_location(name,path)
    result=importlib.util.module_from_spec(spec);spec.loader.exec_module(result);return result

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('keep',type=Path);parser.add_argument('--cpus',type=int,nargs='+',default=[1,2,4])
    args=parser.parse_args();args.keep.mkdir(parents=True,exist_ok=False)
    h=load('compositor_headless',ROOT/'tools/headless-smoke.py')
    load('compositor_accelerator',ROOT/'deploy/distributor/validation-accelerator.py').install(h)
    login=load('compositor_login',ROOT/'tools/login-session-smoke.py')
    policy=load('compositor_input',ROOT/'deploy/distributor/validation-input.py')
    terminal=load('compositor_terminal',ROOT/'deploy/distributor/validation-terminal.py')
    import guest_cpu_frame,recovery_console
    from guest_compositor import wait_record, BENCHMARK, FILTER
    results={}
    with tempfile.TemporaryDirectory(prefix='zn-compositor-') as temporary:
        temp=Path(temporary);configuration=temp/'limine.conf'
        with (args.keep/'iso.log').open('w') as log:
            subprocess.run(['xorriso','-osirrox','on','-indev',str(ROOT/'output-x64/Zenith.iso'),'-extract','/boot/limine/limine.conf',str(configuration)],check=True,stdout=log,stderr=log)
        original=configuration.read_text()
        def iso(name,options):
            content,n=re.subn('cmdline: root=auto','cmdline: root=live '+options,original,count=1)
            if n!=1:raise RuntimeError('live configuration changed')
            configuration.write_text(content.replace('timeout: 5','timeout: 0'))
            target=temp/(name+'.iso');shutil.copyfile(ROOT/'output-x64/Zenith.iso',target)
            with (args.keep/'iso.log').open('a') as log:
                subprocess.run(['xorriso','-boot_image','any','keep','-dev',str(target),'-map',str(configuration),'/boot/limine/limine.conf','-commit_eject','all'],check=True,stdout=log,stderr=log)
            return target
        headless=iso('headless','smp=native');desktop=iso('desktop','smp=native session=de')
        for cpus in args.cpus:
            folder=args.keep/str(cpus);monitor=folder/'monitor.sock'
            m=h.Machine(folder,cpus=cpus,iso=headless,network=[],monitor=monitor)
            try:
                assert m.wait('headless ready',timeout=120),'compositor headless boot'
                off=len(m.log());m.send('cpu blur-test > /tmp/blur-result; echo C_BLUR=$? | logger; cat /tmp/blur-result | logger\r')
                match=wait_record(m,FILTER,off)
                raw=m.log()[off:];assert 'user: C_BLUR=0' in raw,'filter result'
                assert match and int(match[1])==cpus,'complete blur proof'
                assert (int(match[4])>0)==(cpus>1),'actual AP work / scalar fallback'
                kvm=policy.monitor_text(monitor,'info kvm')
                (folder/'accelerator.txt').write_text(kvm)
                accelerator='kvm' if 'enabled' in kvm else 'tcg' if 'disabled' in kvm else 'unknown'
                assert accelerator!='unknown','actual accelerator acknowledgement: '+kvm
                results[cpus]=dict(scalar_us=int(match[2]),parallel_us=int(match[3]),ap_tiles=int(match[4]),hash=match[5],accelerator=accelerator)
                off=len(m.log());m.send('poweroff\r');assert m.wait('all filesystems unmounted',off,timeout=60)
                assert m.process.wait(timeout=30)==0
                print('PASS exact region/halo filter at '+str(cpus)+' CPUs: '+str(results[cpus]),flush=True)
            finally:
                if m.process.poll() is None:m.process.terminate();m.process.wait(timeout=10)
            folder=args.keep/('gui-'+str(cpus));monitor=folder/'monitor.sock'
            m=h.Machine(folder,cpus=cpus,iso=desktop,network=['-device','virtio-gpu-pci','-device','virtio-keyboard-pci'],monitor=monitor)
            try:
                assert m.wait('desktop ready',timeout=120),'compositor desktop boot'
                recovery_console.monitor_command(monitor,'sendkey ctrl-alt-shift-t 50')
                observer=policy.RenderedMouse(monitor,policy.monitor_text)
                def command(text,label):
                    guest_cpu_frame.prompt(observer,terminal)
                    off=len(m.log());login.monitor_type(monitor,text+'; echo '+label+'=$? | logger\n')
                    assert m.wait('user: '+label+'=0',off,timeout=30),label
                    return off
                command('settings set materials background-blur','C_MATERIALS')
                command('settings set translucency 80','C_TRANSLUCENCY')
                guest_cpu_frame.prompt(observer,terminal)
                off=len(m.log());login.monitor_type(monitor,'fps parallel-bench\n')
                match=wait_record(m,BENCHMARK,off)
                assert match and int(match[1])==0,'whole-scene pixel parity'
                assert int(match[5])==7,'real whole-app material enabled'
                assert (int(match[4])>0)==(cpus>1),'real compositor consumes AP tiles'
                results[cpus]['scene']=dict(scalar_us=int(match[2]),parallel_us=int(match[3]),ap_tiles=int(match[4]))
                # Also cover a full workspace: four cores need more than the
                # two small tiles in the ordinary recovery Terminal window.
                guest_cpu_frame.prompt(observer,terminal)
                off=len(m.log());login.monitor_type(monitor,'fps parallel-bench fullscreen\n')
                match=wait_record(m,BENCHMARK,off)
                assert 'parallel viewport=0,0,1280,800' in m.log()[off:],'fullscreen viewport acknowledgement'
                assert match and int(match[1])==0 and int(match[5])==7,'fullscreen exact material pixels'
                assert (int(match[4])>0)==(cpus>1),'fullscreen AP consumption'
                results[cpus]['fullscreen']=dict(scalar_us=int(match[2]),parallel_us=int(match[3]),ap_tiles=int(match[4]))
                print('PASS fullscreen compositor at '+str(cpus)+' CPUs: '+str(results[cpus]['fullscreen']),flush=True)
                if cpus>1:
                    command('cpu managed-hold > /tmp/c4-held','C_HELD')
                    guest_cpu_frame.prompt(observer,terminal)
                    off=len(m.log());login.monitor_type(monitor,'fps parallel-bench\n')
                    busy=wait_record(m,BENCHMARK,off)
                    assert busy and int(busy[1])==0 and int(busy[4])==0,'busy APs must preserve exact scalar rendering'
                    command('cpu managed-release > /tmp/c4-released','C_RELEASE')
                    guest_cpu_frame.prompt(observer,terminal)
                    off=len(m.log());login.monitor_type(monitor,'fps parallel-bench\n')
                    resumed=wait_record(m,BENCHMARK,off)
                    assert resumed and int(resumed[1])==0 and int(resumed[4])>0,'released owners must be reusable without blur quarantine'
                    results[cpus]['busy_owner_fallback_and_reuse']=True
                    print('PASS live compositor over held managed roots; scalar fallback and released owner reuse at '+str(cpus)+' CPUs',flush=True)
                started=time.monotonic();command('echo C_INPUT | logger','C_INPUT')
                results[cpus]['public_command_roundtrip_seconds']=round(time.monotonic()-started,3)
                observer.capture(folder/'compositor.ppm')
                guest_cpu_frame.prompt(observer,terminal)
                off=len(m.log());login.monitor_type(monitor,'poweroff\n')
                assert m.wait('all filesystems unmounted',off,timeout=60),'desktop shutdown'
                assert m.process.wait(timeout=30)==0,'desktop poweroff'
                print('PASS real material composition, exact pixels and focused input: '+str(cpus)+' CPUs '+str(results[cpus]['scene']),flush=True)
            finally:
                if m.process.poll() is None:m.process.terminate();m.process.wait(timeout=10)
        assert len({r['hash'] for r in results.values()})==1,'CPU-dependent pixels'
        (args.keep/'compositor-times.json').write_text(json.dumps(results,indent=2)+'\n')
    print('PASS C4 compositor acceptance; general thread migration remains disabled',flush=True)
if __name__=='__main__':main()
