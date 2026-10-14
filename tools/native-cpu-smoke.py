#!/usr/bin/env python3
"""Disposable 1/2/4-vCPU acceptance for opt-in BSP/native AP integer workers."""
import argparse
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
                raw=command('cpu status | logger','managed-cpus=1')
                check('detected='+str(cpus)+' native-workers='+str(cpus if enabled else 0) in raw,label+' detected/started CPUs')
                if enabled and cpus > 1:
                    for run in range(2):
                        raw=command('cpu test 10000000 | logger; echo CPU_TEST=$? | logger','user: CPU_TEST=0')
                        ids=re.findall(r'user: worker=(\d+) apic=(\d+) actual=(\d+)',raw)
                        ids={int(i):(int(expected),int(actual)) for i,expected,actual in ids}
                        check(len(ids)==cpus and len({pair[1] for pair in ids.values()})==cpus
                              and all(a==b for a,b in ids.values()),label+' distinct actual CPUs run '+str(run))
                        check('native CPU results verified; overlap=True' in raw,label+' correct concurrent results '+str(run))
                    command('cpu test 0; echo CPU_BOUND=$? | logger','user: CPU_BOUND=1')
                    command('cpu test 10000001; echo CPU_BOUND=$? | logger','user: CPU_BOUND=1')
                else:
                    command('cpu test; echo CPU_REFUSED=$? | logger','user: CPU_REFUSED=1')
                command('echo cpu-shell-alive | logger','user: cpu-shell-alive')
                command('poweroff','all filesystems unmounted')
                check(machine.process.wait(timeout=30)==0,label+' clean poweroff')
            finally: machine.stop()
    print('RESULT: '+str(checks)+' native CPU checks passed',flush=True)


if __name__=='__main__':main()
