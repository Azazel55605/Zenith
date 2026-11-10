#!/usr/bin/env python3
"""The 4 KiB ext2 profile in the guest: format and use a volume over 512 MiB, write and read a
sparse file past 4 GiB (triple-indirect blocks), check it with fsck.ext2 and the host e2fsprogs,
then install onto a 2 GiB disk (a 4 KiB root using the whole disk) and boot it; enlarge that disk and
grow the root from the live system; and create, delete and grow partitions with `disk`."""
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

    # Phase 3: the VM disk is enlarged to 4 GiB; the live system grows the installed root into it.
    enlarged = keep / 'enlarged.img'
    shutil.copyfile(keep / 'installed/disk.img', enlarged)
    with enlarged.open('r+b') as stream:
        stream.truncate(4 * GIB)
    # An enlarged disk keeps its backup GPT at the old end until the table is rewritten. Firmware and
    # Limine read the primary table; the harness's own parser insists on both copies, so for this one
    # boot it reads the primary only (checksums still verified).
    strict = boot_image.partitions
    def primary_only(image):
        import struct, uuid, zlib
        with Path(image).open('rb') as f:
            f.seek(512); raw = f.read(512)
            size, crc = struct.unpack_from('<II', raw, 12)
            data = bytearray(raw[:size]); data[16:20] = bytes(4)
            if raw[:8] != b'EFI PART' or zlib.crc32(data) != crc: raise ValueError('primary GPT')
            table, count, stride, crc = struct.unpack_from('<QIII', raw, 72)
            f.seek(table * 512); entries = f.read(count * stride)
            if zlib.crc32(entries) != crc: raise ValueError('primary GPT entries')
        result = []
        for off in range(0, len(entries), stride):
            e = entries[off:off + stride]
            if e[:16] == bytes(16): continue
            start, end = struct.unpack_from('<QQ', e, 32)
            result.append((uuid.UUID(bytes_le=e[:16]), uuid.UUID(bytes_le=e[16:32]), start, end))
        return result
    boot_image.partitions = primary_only
    try:
        m = h.Machine(keep / 'rescue', enlarged, 'root=live session=headless', verify=False, network=[])
    finally:
        boot_image.partitions = strict
    try:
        check(m.wait('headless ready', timeout=600), 'rescue system on the enlarged disk')
        m.wait('[console] ')
        time.sleep(0.3)
        output = status(m, 'disk list | logger', 'L_DISKS')
        check('sata0p1' in output and 'ext2 4K' in output and 'free' in output, 'disk list shows the root and the new free space')
        status(m, 'disk grow sata0p1 max', 'L_GROW_PREVIEW', 1)
        output = status(m, 'disk grow sata0p1 max --yes', 'L_GROW', timeout=1200)
        check('check clean' in output, 'root grown and checked')
        off(m)
    finally:
        m.stop()
    grown_disk = keep / 'grown.img'
    shutil.copyfile(keep / 'rescue/disk.img', grown_disk)
    # The harness pointed this copy's boot menu at the rescue entry; put the installed menu back
    # (the ESP did not move: only the root partition and the table's backup copy changed).
    menu = keep / 'limine.conf'
    menu.write_bytes(boot_image.read(keep / 'installed/disk.img', '/limine.conf'))
    subprocess.run(['mcopy', '-i', f'{grown_disk}@@{boot_image.esp(grown_disk)[2] * 512}', '-o', str(menu), '::/limine.conf'], check=True)
    inventory = boot_image.verify(grown_disk, ROOT / 'output-x64/Zenith.elf')
    root = next(p for p in boot_image.partitions(grown_disk) if str(p[1]) == inventory['root'])
    check((root[3] - root[2] + 1) * 512 > 3.5 * GIB, 'root partition grown to the disk end (backup GPT moved)')
    m = h.Machine(keep / 'grown', grown_disk, 'session=headless')
    try:
        check(m.wait('headless ready', timeout=600), 'grown root boots')
        m.wait('[console] ')
        time.sleep(0.3)
        step(m, 'cat /root/large-root.txt | logger', 'user: persisted')
        output = status(m, 'df | logger', 'L_ROOT_DF')
        sizes = [float(x) for x in re.findall(r'user: \S+\s+(\d+\.\d)G\s.*\s/\s*$', output, re.M)]
        check(bool(sizes) and sizes[0] > 3.5, 'df shows the larger root')
        off(m)
    finally:
        m.stop()
    root_volume = keep / 'grown-root.img'
    extract(keep / 'grown/disk.img', root[2], root[3] - root[2] + 1, root_volume)
    host_fsck(root_volume, 'grown root')

    # Phase 4: partition tools on a blank 3 GiB disk.
    tools_disk = keep / 'tools-disk.img'
    with tools_disk.open('wb') as stream:
        stream.truncate(3 * GIB)
    m = h.Machine(keep / 'tools', extra_disks=(tools_disk,), network=[])
    try:
        check(m.wait('headless ready', timeout=600), 'live system with a blank disk')
        m.wait('[console] ')
        time.sleep(0.3)
        status(m, 'disk create sata1 1G ext2 --yes', 'T_NO_TABLE', 1)
        status(m, 'disk init sata1 --yes', 'T_INIT')
        output = status(m, 'disk create sata1 1G ext2 -L DATA --yes', 'T_CREATE_A')
        check('sata1p0' in output, 'first partition created and formatted')
        status(m, 'disk create sata1 512M none --yes', 'T_CREATE_B')
        output = status(m, 'disk list | logger', 'T_LIST')
        check('sata1p1' in output and 'ext2 4K "DATA"' in output, 'disk list shows both partitions and the label')
        status(m, 'mkdir -p /mnt/d && mount -t ext2 sata1p0 /mnt/d && echo kept > /mnt/d/kept.txt', 'T_WRITE')
        status(m, 'disk delete sata1p1 --yes', 'T_DELETE_MOUNTED', 1)
        status(m, 'umount /mnt/d', 'T_UMOUNT')
        status(m, 'disk delete sata1p1 --yes', 'T_DELETE')
        output = status(m, 'disk grow sata1p0 max --yes', 'T_GROW', timeout=1200)
        check('check clean' in output, 'partition grown into the freed space')
        status(m, 'mount -t ext2 sata1p0 /mnt/d && cat /mnt/d/kept.txt | logger && umount /mnt/d', 'T_KEPT')
        step(m, 'fsck.ext2 sata1p0 | logger', 'user: clean:')
        off(m)
    finally:
        m.stop()
    parts = boot_image.partitions(keep / 'tools/secondary.img')
    check(len(parts) == 1 and (parts[0][3] - parts[0][2] + 1) * 512 > 2.9 * GIB, 'host GPT: one partition over the disk, both table copies valid')
    tools_volume = keep / 'tools-volume.img'
    extract(keep / 'tools/secondary.img', parts[0][2], parts[0][3] - parts[0][2] + 1, tools_volume)
    host_fsck(tools_volume, 'tools volume')
    print(f'PASS large ext2: {checks} checks', flush=True)


if __name__ == '__main__':
    main()
