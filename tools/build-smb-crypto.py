#!/usr/bin/env python3
"""Build the native SMB client's crypto object from the same pinned source used by TLS."""
from pathlib import Path
import argparse
import hashlib
import subprocess
import tarfile
import tempfile

ROOT = Path(__file__).resolve().parents[1]
EXPECTED = '807f0a1cc7c63b571180e9efeb6236b677e550869eb0442ace33aa9c69055fcc'

def build(output):
    archive = ROOT / 'vendor/mbedtls/source-3.6.7.tar.gz'
    if hashlib.sha256(archive.read_bytes()).hexdigest() != EXPECTED:
        raise ValueError('Mbed TLS archive checksum mismatch')
    smb = ROOT / 'userland/smb'
    resource = Path(subprocess.check_output(['clang', '-print-resource-dir'], text=True).strip())
    with tempfile.TemporaryDirectory(prefix='zenith-smb-crypto-') as directory:
        temp = Path(directory)
        with tarfile.open(archive) as source:
            source.extractall(temp, filter='data')
        source = temp / 'mbedtls-mbedtls-3.6.7'
        flags = ['--target=x86_64-unknown-none', '-nostdinc', '-isystem', resource / 'include',
                 '-I', ROOT / 'src/Native/Tls/libc', '-I', smb, '-I', smb / 'vendor',
                 '-I', source / 'include', '-I', source / 'library', '-DMBEDTLS_CONFIG_FILE="crypto-config.h"',
                 '-ffreestanding', '-fno-stack-protector', '-fno-builtin', '-fno-pie', '-mno-red-zone',
                 '-mno-sse', '-mno-sse2', '-Os', '-ffunction-sections', '-fdata-sections']
        sources = [source / 'library' / (name + '.c') for name in ('aes', 'md5', 'sha256')]
        objects = []
        for path in [*sources, smb / 'vendor/md4.c', smb / 'crypto.c']:
            obj = temp / (path.stem + '.o')
            subprocess.run(['clang', *flags, '-c', path, '-o', obj], check=True)
            objects.append(obj)
        subprocess.run(['ld', '-r', *objects, '-o', output], check=True)

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', type=Path)
    build(parser.parse_args().output)
