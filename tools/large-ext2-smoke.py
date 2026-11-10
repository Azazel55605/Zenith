#!/usr/bin/env python3
"""The 4 KiB ext2 profile in the guest: format and use a volume over 512 MiB, write and read a
sparse file past 4 GiB (triple-indirect blocks), check it with fsck.ext2 and the host e2fsprogs,
then install onto a 2 GiB disk (a 4 KiB root using the whole disk) and boot it."""
import argparse
import importlib.util
from pathlib import Path
import re
import shutil
import struct
import subprocess
import time

import boot_image

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('headless', ROOT / 'tools/headless-smoke.py')
h = importlib.util.module_from_spec(spec); spec.loader.exec_module(h)

GIB = 1024 ** 3
VOLUME = 1 * GIB             # over 512 MiB: the 4 KiB profile
FILE_BLOCK = 1310720         # 4096-byte block 1310720 starts at 5 GiB
MARK = 'zenith-past-four-gib'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('keep', type=Path, nargs='?', default=Path('obj/large-ext2-smoke'))
    args = parser.parse_args()
    keep = args.keep.resolve()
    if keep.exists():
        raise SystemExit('use a new evidence directory')
    for tool in ('e2fsck', 'debugfs'):
        if not shutil.which(tool):
            raise SystemExit('missing host tool: ' + tool)
    keep.mkdir(parents=True)
    checks = 0

    def check(ok, label):
        nonlocal checks
        if not ok:
            raise RuntimeError(label)
        checks += 1
        print('ok   ' + label, flush=True)

    def step(m, command, marker, timeout=600):
        offset = len(m.log())
        m.send(command + '\r')
        check(m.wait(marker, offset, timeout), marker)
        return h.efi.smoke.COSMOS_NOISE.sub('', m.log()[offset:])

    def status(m, command, label, code=0, timeout=600):
        return step(m, command + '; echo ' + label + '=$? | logger', 'user: ' + label + '=' + str(code), timeout)

    def off(m):
        step(m, 'sync; poweroff', 'all filesystems unmounted')
        check(m.process.wait(timeout=60) == 0, 'clean poweroff')

    def extract(disk, start, sectors, target):
        with disk.open('rb') as source, target.open('wb') as out:
            source.seek(start * 512)
            remaining = sectors * 512
            while remaining:
                data = source.read(min(remaining, 4 * 1024 * 1024))
                if not data:
                    raise RuntimeError('truncated partition')
                out.write(data)
                remaining -= len(data)

    def host_fsck(volume, label):
        result = subprocess.run(['e2fsck', '-f', '-n', str(volume)], capture_output=True, text=True)
        (keep / (label + '-e2fsck.log')).write_text(result.stdout + result.stderr)
        check(result.returncode == 0, label + ': independent e2fsck clean')

    # Phase 1: a 1 GiB secondary volume (one MBR partition) on the live system.
    secondary = keep / 'volume-disk.img'
    with secondary.open('wb') as stream:
        sector = bytearray(512)
        sector[446:462] = struct.pack('<B3sB3sII', 0, b'\0' * 3, 0x83, b'\0' * 3, 2048, VOLUME // 512)
        sector[510:512] = b'\x55\xaa'
        stream.write(sector)
        stream.truncate(2048 * 512 + VOLUME)
    m = h.Machine(keep / 'live', extra_disks=(secondary,), network=[])
    try:
        check(m.wait('headless ready', timeout=600), 'live boot')
        m.wait('[console] ')
        time.sleep(0.3)
        output = status(m, 'mkfs.ext2 sata1p0 --yes -L LARGE', 'L_FORMAT', timeout=900)
        check('Formatted sata1p0' in output, 'format reported')
        status(m, 'mkdir -p /mnt/l && mount -t ext2 sata1p0 /mnt/l', 'L_MOUNT')
        output = status(m, 'df | grep /mnt/l | logger', 'L_DF')
        check('/mnt/l' in output, 'df lists the mounted volume')
        status(m, 'echo ' + MARK + ' > /tmp/mark', 'L_MARK')
        status(m, 'dd if=/tmp/mark of=/mnt/l/big bs=4096 seek=' + str(FILE_BLOCK) + ' count=1', 'L_SPARSE_WRITE')
        output = status(m, 'stat /mnt/l/big | grep Size | logger', 'L_STAT')
        size = 5 * GIB + len(MARK) + 1
        check('Size: ' + str(size) in output, 'file size past 4 GiB')
        status(m, 'umount /mnt/l && mount -t ext2 sata1p0 /mnt/l', 'L_REMOUNT')
        status(m, 'dd if=/mnt/l/big of=/tmp/back bs=4096 skip=' + str(FILE_BLOCK) + ' count=1', 'L_SPARSE_READ')
        step(m, 'cat /tmp/back | logger', 'user: ' + MARK)
        status(m, 'echo small > /mnt/l/small.txt && mkdir /mnt/l/dir && echo nested > /mnt/l/dir/n.txt', 'L_FILES')
        status(m, 'umount /mnt/l', 'L_UMOUNT')
        step(m, 'fsck.ext2 sata1p0 | logger', 'user: clean:')
        off(m)
    finally:
        m.stop()
    volume = keep / 'volume.img'
    extract(keep / 'live/secondary.img', 2048, VOLUME // 512, volume)
    host_fsck(volume, 'volume')
    stat = subprocess.run(['debugfs', '-R', 'stat /big', str(volume)], capture_output=True, text=True).stdout
    check('Size: ' + str(5 * GIB + len(MARK) + 1) in stat, 'debugfs agrees on the size past 4 GiB')
    features = subprocess.run(['debugfs', '-R', 'stats', str(volume)], capture_output=True, text=True).stdout
    check('Block size:               4096' in features and 'sparse_super' in features and 'large_file' in features,
          '4 KiB blocks, sparse_super and large_file')

    # Phase 2: install onto a blank 2 GiB disk: the root takes the rest of the disk.
    blank = keep / 'install-disk.img'
    with blank.open('wb') as stream:
        stream.truncate(2 * GIB)
    m = h.Machine(keep / 'installer', extra_disks=(blank,), network=[])
    try:
        check(m.wait('headless ready', timeout=600), 'installer boot')
        m.wait('[console] ')
        time.sleep(0.3)
        step(m, 'install sata1 --yes', 'Zenith installed and UEFI bootable.', 1200)
        off(m)
    finally:
        m.stop()
    installed = keep / 'installed.img'
    shutil.copyfile(keep / 'installer/secondary.img', installed)
    inventory = boot_image.verify(installed, ROOT / 'output-x64/Zenith.elf')
    root = next(p for p in boot_image.partitions(installed) if str(p[1]) == inventory['root'])
    check((root[3] - root[2] + 1) * 512 > 1.5 * GIB, 'root partition uses the rest of the disk')
    m = h.Machine(keep / 'installed', installed, 'session=headless')
    try:
        check(m.wait('headless ready', timeout=600), 'installed 4 KiB root boots')
        m.wait('[console] ')
        time.sleep(0.3)
        step(m, 'cat /proc/mounts | grep " / ext2 " | logger', ' / ext2 rw')
        status(m, 'echo persisted > /root/large-root.txt', 'L_ROOT_WRITE')
        off(m)
    finally:
        m.stop()
    root_volume = keep / 'root.img'
    extract(keep / 'installed/disk.img', root[2], root[3] - root[2] + 1, root_volume)
    host_fsck(root_volume, 'root')
    print(f'PASS large ext2: {checks} checks', flush=True)


if __name__ == '__main__':
    main()
