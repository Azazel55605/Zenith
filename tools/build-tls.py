#!/usr/bin/env python3
"""Build the checksum-pinned freestanding TLS objects, with no network/host library link."""
import argparse
import concurrent.futures
import hashlib
from pathlib import Path
import subprocess
import tarfile

ROOT = Path(__file__).resolve().parents[1]
SHA256 = '807f0a1cc7c63b571180e9efeb6236b677e550869eb0442ace33aa9c69055fcc'

def check_native_imports(objects):
    # Catch accidental host libc dependencies before the kernel link, including
    # Mbed TLS's header-detected inet_pton path. The kernel supplies only these.
    symbols = subprocess.check_output(['nm', '-g', '--format=posix', *map(str, objects)],
                                      text=True, stderr=subprocess.PIPE)
    defined, undefined = set(), set()
    for line in symbols.splitlines():
        parts = line.split()
        if len(parts) > 1:
            (undefined if parts[1] == 'U' else defined).add(parts[0])
    external = undefined - defined
    unexpected = external - {'memcpy', 'memset', 'zenith_rdseed_word'}
    if unexpected:
        raise RuntimeError('Unsupported native TLS imports: ' + ', '.join(sorted(unexpected)))
    print('Native TLS imports verified: ' + ', '.join(sorted(external)))

def build(out, arch, host=False):
    archive = ROOT / 'vendor/mbedtls/source-3.6.7.tar.gz'
    if hashlib.sha256(archive.read_bytes()).hexdigest() != SHA256:
        raise RuntimeError('Mbed TLS source checksum mismatch')
    out.mkdir(parents=True, exist_ok=True)
    source = out / 'mbedtls-mbedtls-3.6.7'
    if not source.exists():
        with tarfile.open(archive) as package:
            package.extractall(out, filter='data')
    flags = ['-D' + name + '=zenith_tls_' + name for name in ['memcpy', 'memmove', 'memset', 'memcmp', 'strlen', 'strcmp', 'strchr', 'strstr']] + ['-O2', '-std=c99', '-ffreestanding', '-fno-stack-protector', '-fno-builtin',
             '-I' + str(source / 'include'), '-I' + str(source / 'library'),
             '-I' + str(ROOT / 'src/Native/Tls'), '-DMBEDTLS_CONFIG_FILE="config.h"']
    if host:
        flags += ['-fPIC', '-DZENITH_TLS_HOST_TEST']
    else:
        # Some distro clang builds expose host socket headers even with an ELF
        # target. Only allow compiler builtin headers and our freestanding shim.
        resource = subprocess.check_output(['clang', '-print-resource-dir'], text=True).strip()
        flags += ['-nostdinc', '-isystem', str(Path(resource) / 'include'),
                  '-I' + str(ROOT / 'src/Native/Tls/libc')]
        flags += ['--target=' + ('x86_64-none-elf' if arch == 'x64' else 'aarch64-none-elf')]
        if arch == 'x64': flags += ['-mno-red-zone', '-mcmodel=kernel', '-fno-pic']
    stamp = out / '.tls-mode'
    mode = repr(flags)
    if not stamp.exists() or stamp.read_text() != mode:
        for obj in out.glob('tls-*.o'): obj.unlink()
        stamp.write_text(mode)
    sources = sorted((source / 'library').glob('*.c')) + [ROOT / 'src/Native/Tls/bridge.c']
    def compile_one(path):
        obj = out / ('tls-' + path.stem + '.o')
        inputs = [path, ROOT / 'src/Native/Tls/config.h', Path(__file__), *sorted((ROOT / 'src/Native/Tls/libc').glob('*.h'))]
        if obj.exists() and obj.stat().st_mtime >= max(p.stat().st_mtime for p in inputs): return obj
        test_flags = ['-DZENITH_TLS_SELFTEST'] if path.stem in ['aes', 'gcm', 'sha256', 'sha512', 'ctr_drbg', 'bridge'] else []
        subprocess.run(['clang', *flags, *test_flags, '-c', str(path), '-o', str(obj)], check=True)
        return obj
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        objects = list(pool.map(compile_one, sources))
    if host:
        subprocess.run(['clang', '-shared', *map(str, objects), '-o', str(out / 'libzenith-tls.so')], check=True)
    else:
        check_native_imports(objects)
    print('Built checksum-pinned Mbed TLS 3.6.7 (' + arch + ')')

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('output', type=Path)
    parser.add_argument('--arch', choices=['x64', 'arm64'], default='x64')
    parser.add_argument('--host', action='store_true')
    args = parser.parse_args()
    build(args.output.resolve(), args.arch, args.host)
