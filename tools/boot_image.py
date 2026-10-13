"""Read-only installed boot verification; optional session overrides on disposable clones."""
import hashlib
from pathlib import Path
import re
import struct
import subprocess
import uuid
import zlib

ESP = uuid.UUID('c12a7328-f81f-11d2-ba4b-00a0c93ec93b')

def partitions(image):
    with Path(image).open('rb') as f:
        f.seek(0, 2); sectors = f.tell() // 512
        def header(lba):
            f.seek(lba * 512); raw = f.read(512)
            if raw[:8] != b'EFI PART': raise ValueError('missing GPT')
            size, crc = struct.unpack_from('<II', raw, 12)
            if not 92 <= size <= 512: raise ValueError('GPT header size')
            data = bytearray(raw[:size]); data[16:20] = bytes(4)
            if zlib.crc32(data) != crc: raise ValueError('GPT header checksum')
            table, count, stride, crc = struct.unpack_from('<QIII', raw, 72)
            if not 1 <= count <= 1024 or not 128 <= stride <= 1024 or count * stride > 1024 * 1024: raise ValueError('GPT bounds')
            f.seek(table * 512); entries = f.read(count * stride)
            if zlib.crc32(entries) != crc: raise ValueError('GPT entries checksum')
            return entries, stride
        entries, stride = header(1)
        backup, backup_stride = header(sectors - 1)
        if backup != entries or backup_stride != stride: raise ValueError('GPT copies differ')
        result = []
        for off in range(0, len(entries), stride):
            e = entries[off:off+stride]
            if e[:16] == bytes(16): continue
            start, end = struct.unpack_from('<QQ', e, 32)
            if start < 34 or end < start or end >= sectors - 33: raise ValueError('partition bounds')
            result.append((uuid.UUID(bytes_le=e[:16]), uuid.UUID(bytes_le=e[16:32]), start, end))
        return result

def esp(image):
    matches = [p for p in partitions(image) if p[0] == ESP]
    if len(matches) != 1: raise ValueError('expected one ESP')
    return matches[0]

def read(image, path):
    start = esp(image)[2]
    return subprocess.check_output(['mcopy', '-i', f'{image}@@{start * 512}', '::' + path, '-'])

def verify(image, kernel=None):
    parts = partitions(image)
    raw = read(image, '/boot/zenith/manifest.tsv').decode('ascii')
    lines = raw.splitlines()
    if lines[0] != 'ZENITH-BOOT\t1': raise ValueError('boot inventory version')
    rows = [line.split('\t') for line in lines[1:]]
    if any(len(row) != 2 for row in rows): raise ValueError('inventory fields')
    values = dict(rows)
    if len(values) != len(rows) or values['architecture'] != 'x86_64': raise ValueError('inventory identity')
    generation = values['current']
    if not re.fullmatch('[a-f0-9]{64}', generation): raise ValueError('generation')
    if str(esp(image)[1]) != values['esp'] or not any(str(p[1]) == values['root'] for p in parts): raise ValueError('partition binding')
    payload = read(image, '/boot/zenith/' + generation + '/kernel.elf')
    if len(payload) != int(values['kernel-bytes']) or hashlib.sha256(payload).hexdigest() != generation: raise ValueError('kernel integrity')
    if kernel and payload != Path(kernel).read_bytes(): raise ValueError('installer kernel differs from ISO build')
    efi = read(image, '/EFI/BOOT/BOOTX64.EFI')
    if len(efi) != int(values['efi-bytes']) or hashlib.sha256(efi).hexdigest() != values['efi-sha256']: raise ValueError('EFI integrity')
    config = read(image, '/limine.conf').decode('ascii')
    if 'path: boot():/boot/zenith/' + generation + '/kernel.elf' not in config or 'root=PARTUUID=' + values['root'] + ' kernel=' + generation not in config: raise ValueError('boot menu binding')
    previous = values['previous']
    if previous != '-':
        if not re.fullmatch('[a-f0-9]{64}', previous) or previous == generation: raise ValueError('previous generation')
        old = read(image, '/boot/zenith/' + previous + '/kernel.elf')
        if hashlib.sha256(old).hexdigest() != previous: raise ValueError('previous kernel integrity')
        if '/Zenith previous kernel' not in config or 'root=PARTUUID=' + values['root'] + ' kernel=' + previous not in config: raise ValueError('previous menu binding')
        secondary = read(image, '/boot/limine/limine.conf').decode('ascii')
        if 'root=PARTUUID=' + values['root'] not in secondary or 'root=live session=headless' not in secondary: raise ValueError('secondary menu binding')
    if 'root=live session=headless' not in config: raise ValueError('rescue entry missing')
    print('ok   guest-written GPT, EFI, kernel SHA256, root UUID and rescue menu', flush=True)
    return values

def override(image, argument, directory):
    # Test harness only: preserve the selected kernel and root identity.
    if argument not in ('session=de', 'session=headless', 'session=invalid', 'root=live session=headless', 'session=headless kernel-test-stop=staged', 'session=headless kernel-test-stop=menu-gap'):
        raise ValueError('unsupported test override')
    text = read(image, '/limine.conf').decode('ascii')
    def change(match):
        tokens = match.group(1).split()
        tokens = [t for t in tokens if not t.startswith('session=')]
        if argument.startswith('root=live'): tokens = [t for t in tokens if not t.startswith(('root=', 'kernel='))]
        return '    cmdline: ' + ' '.join(tokens + argument.split())
    text = re.sub(r'    cmdline: ([^\n]*)', change, text, count=1).replace('timeout: 5', 'timeout: 5' if 'kernel-test-stop' in argument else 'timeout: 0')
    target = Path(directory) / 'limine.conf'; target.write_text(text)
    subprocess.run(['mcopy', '-i', f'{image}@@{esp(image)[2] * 512}', '-o', str(target), '::/limine.conf'], check=True)

if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser(description='Verify guest-installed boot artifacts without writing the disk.')
    parser.add_argument('image', type=Path); parser.add_argument('--kernel', type=Path)
    args = parser.parse_args(); verify(args.image, args.kernel)
