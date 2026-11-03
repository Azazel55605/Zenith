#!/usr/bin/env python3
"""Disposable 1/2/4-vCPU acceptance for opt-in BSP/native AP integer workers."""
import argparse
import os
import time
import importlib.util
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT/'tools'))
spec = importlib.util.spec_from_file_location('headless', ROOT/'tools/headless-smoke.py')
h = importlib.util.module_from_spec(spec); spec.loader.exec_module(h)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('keep', type=Path, nargs='?', default=Path('/tmp/zn-native-cpu'))
    parser.add_argument('--iso', type=Path, default=ROOT/'output-x64/Zenith.iso')
    args = parser.parse_args(); args.keep.mkdir(parents=True, exist_ok=True)
    if len(str(args.keep.resolve()).encode()) > 70: parser.error('use a short keep path for Unix sockets')
    checks = 0
    image_times = {}
    def check(ok, label):
        nonlocal checks
        if not ok: raise RuntimeError(label)
        checks += 1; print('ok   ' + label, flush=True)
    with tempfile.TemporaryDirectory(prefix='zn-cpu-') as scratch:
        scratch = Path(scratch); conf = scratch/'limine.conf'; iso = scratch/'native.iso'
        with (args.keep/'iso.log').open('w') as log:
            subprocess.run(['xorriso','-osirrox','on','-indev',str(args.iso),'-extract',
                            '/boot/limine/limine.conf',str(conf)],stdout=log,stderr=log,check=True)
            text = conf.read_text()
            text, count = re.subn(r'cmdline: root=auto', 'cmdline: root=live smp=native', text, count=1)
            if count != 1: raise RuntimeError('unexpected live ISO boot configuration')
            conf.write_text(text.replace('timeout: 5','timeout: 0'))
            shutil.copyfile(args.iso, iso)
            subprocess.run(['xorriso','-boot_image','any','keep','-dev',str(iso),
                            '-map',str(conf),'/boot/limine/limine.conf','-commit_eject','all'],
                           stdout=log,stderr=log,check=True)
        for cpus, enabled in [(1,True),(2,True),(4,True),(4,False)]:
            label = str(cpus) + ('-native' if enabled else '-disabled')
            machine = h.Machine(args.keep/label, cpus=cpus, iso=iso if enabled else args.iso, network=[])
            try:
                check(machine.wait('headless ready',timeout=600),label+' boot')
                def command(text,marker):
                    offset=len(machine.log());machine.send(text+'\r')
                    check(machine.wait(marker,offset,90),label+' '+marker)
                    raw=machine.log()[offset:]
                    return raw+'\n'+h.efi.smoke.COSMOS_NOISE.sub('',raw)
                def cpu(text,name,code=0):
                    # Save the producer status before any logger pipeline. File
                    # capture also drains complete output before the final status,
                    # unlike the asynchronous COM1 display-output queue.
                    return command(text+' > /tmp/cpu-output; echo '+name+'=$? > /tmp/cpu-status; '
                                   'cat /tmp/cpu-output | logger; cat /tmp/cpu-status | logger',
                                   'user: '+name+'='+str(code))
                raw=cpu('cpu status','CPU_STATUS')
                check('initial-idt-interrupts-enabled=False' in raw,label+' initial IDT keeps interrupts masked')
                cpu('cpu irq-test','IRQ_TEST')
                check('IDT interrupt-state preservation failures=0' in machine.log(),label+' IDT preserves masked and enabled states')
                check('detected='+str(cpus)+' native-workers='+str(cpus if enabled else 0) in raw,label+' detected/started CPUs')
                if enabled:
                    raw=cpu('cpu image-test','CPU_IMAGE')
                    result=re.search(r'native image verified; tiles=32 pixels=4096 rounds=128 cpus=(\d+) elapsed-us=(\d+) hash=([0-9a-f]+)',raw)
                    check(result is not None,label+' equal-work native pixel pilot')
                    image_times[cpus]=dict(microseconds=int(result[2]),hash=result[3])
                if enabled and cpus > 1:
                    cpu('cpu queue-test','CPU_QUEUE')
                    cpu('cpu idle-test','CPU_IDLE')
                    def cpu_time():
                        # The raw harness PID must be QEMU itself; skip no timing
                        # through a container/wrapper and never invent CPU counters.
                        p=Path('/proc')/str(machine.process.pid)
                        if 'qemu-system' not in (p/'comm').read_text(): raise RuntimeError('idle measurement requires direct QEMU PID')
                        stat=(p/'stat').read_text().rsplit(')',1)[1].split()
                        return (int(stat[11])+int(stat[12]))/os.sysconf('SC_CLK_TCK')
                    start_cpu=cpu_time();start_wall=time.monotonic();time.sleep(1)
                    cpu_seconds=cpu_time()-start_cpu;wall=time.monotonic()-start_wall
                    print('IDLE '+label+' qemu_cpu_seconds='+str(round(cpu_seconds,3))+' wall_seconds='+str(round(wall,3)),flush=True)
                    check(cpu_seconds/wall<0.4,label+' idle QEMU CPU below 0.4 core; APs halt')
                    cpu('cpu cancel-test','CPU_CANCEL')
                    check('native CPU cancellation verified; workers reusable' in machine.log(),label+' cancellation barrier')
                    for run in range(2):
                        raw=cpu('cpu test 10000000','CPU_TEST')
                        ids=re.findall(r'(?:user: )?worker=(\d+) apic=(\d+) actual=(\d+)',raw)
                        ids={int(i):(int(expected),int(actual)) for i,expected,actual in ids}
                        check(len(ids)==cpus and len({pair[1] for pair in ids.values()})==cpus
                              and all(a==b for a,b in ids.values()),label+' distinct actual CPUs run '+str(run))
                        check('native CPU results verified; overlap=True' in raw,label+' correct concurrent results '+str(run))
                    cpu('cpu test 0','CPU_BOUND',1)
                    cpu('cpu test 10000001','CPU_BOUND',1)
                else:
                    cpu('cpu test','CPU_REFUSED',1)
                if enabled and cpus > 1:
                    cpu('cpu stop-test','CPU_STOP')
                    cpu('cpu test','CPU_STOPPED',1)
                command('echo cpu-shell-alive | logger','user: cpu-shell-alive')
                raw=command('poweroff','all filesystems unmounted')
                if enabled:
                    check('native workers quiesced='+str(cpus) in raw,label+' AP stop acknowledged before unmount')
                check(machine.process.wait(timeout=30)==0,label+' clean poweroff')
            finally: machine.stop()
        check(len({item['hash'] for item in image_times.values()})==1,'identical native image output at 1/2/4 CPUs')
        print('IMAGE_TIMINGS '+str(image_times),flush=True)
        # Fault containment needs a fresh pool: a poisoned AP is never restarted.
        for cpus in (2,4):
            label=str(cpus)+'-fault';machine=h.Machine(args.keep/label,cpus=cpus,iso=iso,network=[])
            try:
                check(machine.wait('headless ready',timeout=180),label+' boot')
                cpu('cpu fault-test','CPU_FAULT')
                check('native fault contained;' in machine.log(),label+' private IST fault and sibling isolation')
                # Healthy earlier APs may accept held work before the failed last
                # AP refuses admission. Owner abort must stop that partial batch.
                cpu('cpu test','CPU_ABORT',1)
                raw=cpu('cpu status','CPU_ABORT_STATUS')
                check('stopping=True' in raw,label+' aborted owner closes admission')
                off=len(machine.log());machine.send('poweroff\r')
                check(machine.wait('all filesystems unmounted',off,60),label+' faulted worker acknowledged quiescence')
                check(machine.process.wait(timeout=30)==0,label+' clean poweroff')
            finally:machine.stop()
    print('RESULT: '+str(checks)+' native CPU checks passed',flush=True)


if __name__=='__main__':main()
