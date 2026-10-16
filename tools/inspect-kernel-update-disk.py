#!/usr/bin/env python3
"""Inspect a stopped kernel-update fixture without booting or modifying its disk."""
import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

import boot_image

ANCESTORS = ('/boot', '/boot/efi', '/var', '/var/lib', '/var/lib/zenith', '/var/lib/zenith/boot')
FILES = ('/var/lib/zenith/boot/installed.tsv', '/var/lib/zenith/boot/generations.tsv')


def inspect(image):
    # Debian login shells may omit /usr/sbin despite e2fsprogs being installed.
    debugfs = shutil.which('debugfs')
    if debugfs is None:
        debugfs = next((path for path in ('/usr/sbin/debugfs', '/sbin/debugfs')
                        if Path(path).is_file() and os.access(path, os.X_OK)), None)
    if debugfs is None: raise ValueError('debugfs executable unavailable (checked PATH, /usr/sbin and /sbin)')
    state = boot_image.verify(image)
    root = next(part for part in boot_image.partitions(image) if str(part[1]) == state['root'])
    # debugfs only supports filesystem images, so extract a private temporary
    # copy. No guest mounts, repairs, password files or signing keys are accessed.
    with tempfile.TemporaryDirectory(prefix='zenith-boot-inspect-') as directory:
        volume = Path(directory) / 'root.ext2'
        with image.open('rb') as source, volume.open('wb') as target:
            source.seek(root[2] * 512); remaining = (root[3] - root[2] + 1) * 512
            while remaining:
                data = source.read(min(1048576, remaining))
                if not data: raise ValueError('truncated root partition')
                target.write(data); remaining -= len(data)
        invalid = []
        for path in (*ANCESTORS, *FILES):
            result = subprocess.run([debugfs, '-R', 'stat ' + path, str(volume)],
                                    capture_output=True, text=True, check=True)
            match = re.search(r'Inode:\s*(\d+)\s+Type:\s*(\w+)', result.stdout)
            kind = match[2] if match else 'unavailable'
            print(path + ': ' + kind + (' inode=' + match[1] if match else ''), flush=True)
            if path in ANCESTORS and kind != 'directory': invalid.append(path)
            if path == FILES[0] and kind != 'regular': invalid.append(path)
        if invalid: raise ValueError('invalid root boot paths: ' + ', '.join(invalid))
    print('Required on-disk boot ancestors intact; a live VFS lookup failure is not ruled out.', flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('image', type=Path, help='retained disk from a stopped test VM')
    args = parser.parse_args()
    try: inspect(args.image)
    except (OSError, ValueError, StopIteration, subprocess.SubprocessError) as error:
        raise SystemExit('Boot disk inspection failed: ' + str(error))
