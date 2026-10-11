#!/usr/bin/env python3
"""Install from ISO, then boot guest-written UEFI artifacts with no host repair."""
import importlib.util
from pathlib import Path
import time
import argparse
import subprocess
import boot_image
ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('headless', ROOT / 'tools/headless-smoke.py')
h = importlib.util.module_from_spec(spec); spec.loader.exec_module(h)

def send(self, text):
    for value in text.encode('ascii'):
        self.socket.sendall(bytes([value])); time.sleep(0.05)
h.Machine.send = send

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('keep', type=Path, nargs='?', default=Path('obj/install-smoke'))
    parser.add_argument('--check-duplicates', type=Path, help='Only run the two-clone negative check using this installed image')
    args = parser.parse_args(); checks = 0
    def check(ok, label):
        nonlocal checks
        if not ok: raise RuntimeError(label)
        checks += 1; print('ok   ' + label, flush=True)
    def boot(label, disk=None, argument=None):
        m = h.Machine(args.keep / label, disk, argument)
        if not m.wait('headless ready', timeout=600): m.stop(); raise RuntimeError('boot: ' + label)
        check(True, label + ' no-GPU boot'); check('desktop ready' not in m.log(), 'no graphical initialization')
        m.wait('[console] '); time.sleep(0.3)
        return m
    def step(m, command, marker):
        offset = len(m.log()); m.send(command + '\r'); check(m.wait(marker, offset, timeout=300), marker); time.sleep(0.3)
    def off(m):
        step(m, 'sync; poweroff', 'all filesystems unmounted')
        check(m.process.wait(timeout=30) == 0, 'clean poweroff')
    def duplicate(image):
        m = h.Machine(args.keep / 'ambiguous', image, 'session=headless', extra_disks=(image,))
        try:
            check(m.wait('ambiguous root UUID', timeout=600), 'duplicate root identity rejected')
            check('headless ready' not in m.log() and 'root: RAM disk (live)' not in m.log(), 'no wrong-root or live fallback')
        finally: m.stop()
    if args.check_duplicates:
        duplicate(args.check_duplicates)
        print(f'{checks}/{checks} checks passed', flush=True)
        return
    m = boot('live')
    try:
        step(m, 'echo installer-persistent > /home/user/install-proof; install sata0 --yes', 'Zenith installed and UEFI bootable.')
        off(m)
    finally: m.stop()
    inventory = boot_image.verify(m.disk, ROOT / 'output-x64/Zenith.elf')
    installed = m.disk
    m = boot('installed', installed)
    try:
        step(m, 'cat /proc/mounts | grep " / ext2 " | logger', ' / ext2 rw')
        step(m, 'cat /proc/mounts | grep /boot/efi | logger', '/boot/efi fat rw')
        step(m, 'boot status | logger', 'root=' + inventory['root'])
        check('running-kernel=' + inventory['current'] in h.efi.smoke.COSMOS_NOISE.sub('', m.log()), 'loaded kernel matches installed inventory')
        step(m, 'cat /home/user/install-proof | logger', 'user: installer-persistent')
        step(m, 'echo disk-only-persistent > /home/user/reboot-proof', '[console] ')
        off(m)
    finally: m.stop()
    installed = m.disk
    m = boot('reboot', installed)
    try:
        step(m, 'cat /home/user/reboot-proof | logger', 'user: disk-only-persistent')
        off(m)
    finally: m.stop()
    root = next(p for p in boot_image.partitions(m.disk) if str(p[1]) == inventory['root'])
    volume = args.keep / 'checked-root.img'
    with m.disk.open('rb') as source, volume.open('wb') as target:
        source.seek(root[2] * 512); remaining = (root[3] - root[2] + 1) * 512
        while remaining:
            data = source.read(min(remaining, 1024 * 1024))
            if not data: raise RuntimeError('truncated root')
            target.write(data); remaining -= len(data)
    result = subprocess.run(['e2fsck', '-f', '-n', str(volume)], capture_output=True, text=True)
    (args.keep / 'root-check.log').write_text(result.stdout + result.stderr)
    check(result.returncode == 0, 'independent clean ext2 after installed reboot')
    record = subprocess.run(['debugfs', '-R', 'cat /var/lib/zenith/boot/installed.tsv', str(volume)], capture_output=True)
    check(record.stdout == boot_image.read(installed, '/boot/zenith/manifest.tsv'), 'root and ESP installation inventory agree')
    m = boot('rescue', installed, 'root=live session=headless')
    try:
        check('root: RAM disk (live)' in m.log(), 'rescue ignores attached installed root')
        step(m, 'test ! -e /home/user/install-proof && logger rescue-isolated', 'user: rescue-isolated')
        off(m)
    finally: m.stop()
    duplicate(installed)
    print(f'{checks}/{checks} checks passed', flush=True)
if __name__ == '__main__': main()
