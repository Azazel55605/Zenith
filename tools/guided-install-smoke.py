#!/usr/bin/env python3
"""Guided GUI/serial installation with hidden credentials and disk-only login proof."""
import argparse
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import time
import boot_image
ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('install', ROOT / 'tools/install-smoke.py')
i = importlib.util.module_from_spec(spec); spec.loader.exec_module(i)
h = i.h; s = h.efi.smoke

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('mode', choices=('gui', 'headless')); p.add_argument('keep', type=Path)
    args = p.parse_args(); args.keep.mkdir(parents=True, exist_ok=True)
    passwords = ['S!' + os.urandom(8).hex(), 'R!' + os.urandom(8).hex()]
    checks = 0
    def check(ok, label):
        nonlocal checks
        if not ok: raise RuntimeError(label)
        checks += 1; print('ok   ' + label, flush=True)
    def zero(m):
        with m.disk.open('rb') as f: check(not any(f.read(1024 * 1024)), 'rejected setup leaves target GPT untouched')
    def cmd(m, text, marker):
        off = len(m.log()); m.send(text + '\r'); check(m.wait(marker, off, timeout=300), marker); time.sleep(.3)
    def shutdown(m):
        cmd(m, 'sync; poweroff', 'all filesystems unmounted'); check(m.process.wait(timeout=30) == 0, 'clean poweroff'); m.stop()
    if args.mode == 'gui':
        m, _ = s.boot(ROOT / 'output-x64/Zenith.iso', 600, 'guided GUI')
        if m is None: raise RuntimeError('GUI boot')
        check(True, 'desktop ready')
        try:
            m.type('open Sunrise\n'); check(m.wait_for('Sunrise ready', 120), 'Sunrise launched')
            time.sleep(.5); m.screenshot(args.keep / 'disk.ppm')
            m.type('{ret}'); check(m.wait_for('account setup', 120), 'disk selected')
            m.type('{ret}' * 6); check(m.wait_for('Enter both passwords', 120), 'empty credentials refused'); zero(m)
            m.type('{shift-tab}' * 5)
            m.type('{backspace}' * 6 + 'zenith-test{tab}' + '{backspace}' * 4 + 'pilot{tab}')
            m.type(passwords[0] + '{tab}' + passwords[0] + '{tab}' + passwords[1] + '{tab}' + passwords[1] + '{ret}')
            check(m.wait_for('review disk sata0', 120), 'installation review')
            m.screenshot(args.keep / 'review.ppm')
            m.type('{ret}'); check(m.wait_for('Confirm disk erasure first', 120), 'erase checkbox required'); zero(m)
            m.type(' {ret}'); time.sleep(1); m.screenshot(args.keep / 'progress.ppm')
            check(m.wait_for('guided installation complete', 600), 'GUI installed')
            m.screenshot(args.keep / 'done.ppm'); m.type('{esc}')
            m.type('sync; poweroff\n'); check(m.wait_for('all filesystems unmounted', 180), 'GUI clean shutdown')
            check(m.wait_exit(30), 'GUI poweroff')
            check(all(secret not in m.raw_log() for secret in passwords), 'GUI credentials absent from transcript')
            shutil.copyfile(m.disk, args.keep / 'installed.img'); shutil.copyfile(m.serial, args.keep / 'live.log')
        finally: m.stop(); shutil.rmtree(m.workdir, ignore_errors=True)
    else:
        m = h.Machine(args.keep / 'live')
        try:
            check(m.wait('headless ready', timeout=600), 'headless live ready')
            def setup(erase):
                cmd(m, 'install sata0 --setup', 'Hostname: ')
                for value, marker in [('zenith-test','User name: '),('pilot','User password: '),
                    (passwords[0],'Repeat user password: '),(passwords[0],'Root password: '),
                    (passwords[1],'Repeat root password: '),(passwords[1],'to confirm: ')]:
                    cmd(m, value, marker)
                cmd(m, erase, 'target unchanged' if erase == 'NO' else 'Zenith installed and UEFI bootable.')
            setup('NO'); zero(m); setup('ERASE sata0')
            shutdown(m)
            check(all(secret not in m.log() for secret in passwords), 'serial credentials absent from transcript')
            shutil.copyfile(m.disk, args.keep / 'installed.img')
        finally: m.stop()
    installed = args.keep / 'installed.img'; inventory = boot_image.verify(installed, ROOT / 'output-x64/Zenith.elf')
    m = h.Machine(args.keep / 'installed', installed, 'session=headless')
    try:
        check(m.wait('headless ready', timeout=600), 'configured disk-only UEFI boot')
        cmd(m, 'whoami | logger', 'user: nobody')
        cmd(m, 'login pilot', 'Password: '); cmd(m, passwords[0], 'authenticated as pilot')
        cmd(m, 'hostname | logger', 'user: zenith-test')
        cmd(m, 'echo $HOME | logger', 'user: /home/pilot')
        cmd(m, 'id | logger', 'uid=1000(pilot)')
        cmd(m, 'echo persistent > /home/pilot/proof; cat /home/pilot/proof | logger', 'user: persistent')
        cmd(m, 'cat /etc/shadow; echo protected=$? | logger', 'user: protected=1')
        cmd(m, 'test ! -d /home/user && logger no-phantom-home', 'user: no-phantom-home')
        cmd(m, 'login root', 'Password: '); cmd(m, passwords[1], 'authenticated as root')
        shutdown(m); installed = m.disk
        check(all(secret not in m.log() for secret in passwords), 'installed credentials absent from transcript')
    finally: m.stop()
    m = h.Machine(args.keep / 'reboot', installed, 'session=headless')
    try:
        check(m.wait('headless ready', timeout=600), 'second configured UEFI boot')
        cmd(m, 'login pilot', 'Password: '); cmd(m, passwords[0], 'authenticated as pilot')
        cmd(m, 'cat /home/pilot/proof | logger', 'user: persistent')
        cmd(m, 'login root', 'Password: '); cmd(m, passwords[1], 'authenticated as root'); shutdown(m)
    finally: m.stop()
    part = next(p for p in boot_image.partitions(m.disk) if str(p[1]) == inventory['root'])
    volume = args.keep / 'root.img'
    with m.disk.open('rb') as f, volume.open('wb') as out:
        f.seek(part[2]*512); remaining = (part[3]-part[2]+1)*512
        while remaining:
            data = f.read(min(remaining,1024*1024))
            if not data: raise RuntimeError('truncated disk')
            out.write(data); remaining -= len(data)
    result = subprocess.run(['e2fsck','-f','-n',str(volume)],capture_output=True,text=True)
    (args.keep/'root-check.log').write_text(result.stdout+result.stderr); check(result.returncode == 0,'independent clean ext2')
    def stat(path): return subprocess.run(['debugfs','-R','stat '+path,str(volume)],capture_output=True,text=True).stdout
    check('Mode:  0600' in stat('/etc/shadow'), 'independent private shadow mode')
    home = stat('/home/pilot'); check('Mode:  0700' in home and '1000' in home,'independent private home ownership')
    print(f'{checks}/{checks} checks passed',flush=True)
if __name__ == '__main__': main()
