#!/usr/bin/env python3
"""P1 signed script/data install/update/recovery on a disposable installed ext2 guest."""
import argparse
import functools
import hashlib
import http.server
import importlib.util
import os
from pathlib import Path
import struct
import subprocess
import threading

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('headless', ROOT / 'tools/headless-smoke.py')
h = importlib.util.module_from_spec(spec); spec.loader.exec_module(h)

class Server(http.server.ThreadingHTTPServer): daemon_threads = True
class Handler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args): pass

def main():
    parser = argparse.ArgumentParser(description=__doc__); parser.add_argument('keep', type=Path); args = parser.parse_args()
    keep = args.keep.resolve(); keep.mkdir(parents=True, exist_ok=True); served = keep / 'served'; served.mkdir()
    subprocess.run(['dotnet', 'build', str(ROOT / 'tools/Zenith.PackageTool'), '-c', 'Release'], check=True, stdout=subprocess.DEVNULL)
    tool = ['dotnet', str(ROOT / 'tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll')]
    def run(*args): subprocess.run([*tool, *map(str, args)], check=True, stdout=subprocess.DEVNULL)
    run('keygen', keep / 'keys'); key = (keep / 'keys/public.pem').read_bytes(); (served / 'public.pem').write_bytes(key)
    def package(label, name, version, data, path, records=''):
        source = keep / ('source-' + label); payload = source / path.lstrip('/'); payload.parent.mkdir(parents=True); payload.write_bytes(data)
        bundle = keep / ('bundle-' + label); run('build', name, version, source, bundle, '--repo', 'extra')
        manifest = (bundle / 'manifest.tsv').read_text().replace('file\t', records + 'file\t').replace('\t0644\t', '\t0755\t' if path.endswith('.sh') else '\t0644\t')
        (bundle / 'manifest.tsv').write_text(manifest); run('seal', bundle, keep / 'keys/private.pem', served / (label + '.zpk'))
    script = '/usr/share/zenith/scripts/hello.sh'
    package('library', 'hello-data', '1.0.0', b'library-data\n', '/usr/share/doc/hello-data.txt')
    package('hello', 'hello-tools', '1.0.0', b'echo hello-package-v1\n', script, 'requires\thello-data\t>=1.0,<2.0\n')
    package('upgrade', 'hello-tools', '1.1.0', b'echo hello-package-v2\n', script, 'requires\thello-data\t>=1.0,<2.0\n')
    package('conflict', 'conflicting-data', '1.0.0', b'conflict\n', '/usr/share/doc/conflicting.txt', 'conflicts\thello-tools\n')
    tampered = bytearray((served / 'hello.zpk').read_bytes()); tampered[-1] ^= 1; (served / 'tampered.zpk').write_bytes(tampered)
    (served / 'invalid.pem').write_text('-----BEGIN PUBLIC KEY-----\nbad\n-----END PUBLIC KEY-----\n')
    server = Server(('127.0.0.1', 0), functools.partial(Handler, directory=str(served))); threading.Thread(target=server.serve_forever, daemon=True).start()
    url = f'http://10.0.2.2:{server.server_port}'; checks = 0; m = None; secret = 'P1!' + os.urandom(8).hex()
    def check(passed, label):
        nonlocal checks
        checks += 1; print(('ok   ' if passed else 'FAIL ') + label, flush=True)
        if not passed: raise RuntimeError(label)
    def command(text, marker, timeout=120):
        offset = len(m.log()); m.send(text + '\r'); check(m.wait(marker, offset, timeout), marker)
        raw = m.log()[offset:]
        return raw + '\n' + h.efi.smoke.COSMOS_NOISE.sub('', raw)
    def status(text, label, code=0, timeout=120): return command(text + '; echo ' + label + '=$?', label + '=' + str(code), timeout)
    def boot(label, disk=None):
        machine = h.Machine(keep / label, disk, 'session=headless' if disk else None,
            network=['-netdev', 'user,id=net0', '-device', 'e1000e,netdev=net0'])
        if not machine.wait('headless ready', timeout=600): machine.stop(); raise RuntimeError('boot failed ' + label)
        check(machine.wait('network: online address=10.0.2.15', timeout=90), label + ' DHCP'); return machine
    def shutdown(label):
        command('poweroff', 'all filesystems unmounted'); check(m.process.wait(timeout=60) == 0, label + ' clean poweroff'); disk = m.disk; m.stop(); return disk
    try:
        m = boot('live'); status('pkg install /missing.zpk', 'P1_LIVE_DENIED', 1)
        status('install sata0 --yes', 'P1_INSTALL_ROOT', timeout=300); disk = shutdown('live'); m = boot('installed', disk)
        output = status('pkg repos', 'P1_REPOS'); check('core priority=10' in output and 'extra priority=20' in output, 'default core/extra')
        for name in ['public.pem', 'invalid.pem', 'library.zpk', 'hello.zpk', 'upgrade.zpk', 'conflict.zpk', 'tampered.zpk']:
            status(f'fetch {url}/{name} /tmp/{name}', 'P1_FETCH_' + name.replace('.', '_'))
        status('pkg trust /tmp/public.pem ' + '0' * 64, 'P1_WRONG_FINGERPRINT', 1)
        status('pkg trust /tmp/public.pem ' + hashlib.sha256(key).hexdigest(), 'P1_TRUST')
        status('pkg trust /tmp/invalid.pem ' + hashlib.sha256((served / 'invalid.pem').read_bytes()).hexdigest(), 'P1_INVALID_KEY', 1)
        status('pkg check /tmp/hello.zpk', 'P1_SIGNED_CHECK')
        status('pkg install /tmp/tampered.zpk', 'P1_TAMPERED', 1)
        status('pkg install /tmp/hello.zpk', 'P1_MISSING_DEPENDENCY', 1)
        status('echo unowned-staging > /usr/share/doc/hello-data.txt.pkg-new', 'P1_STAGE_COLLISION')
        status('pkg install /tmp/hello.zpk /tmp/library.zpk', 'P1_STAGE_DENIED', 1)
        output = status('cat /usr/share/doc/hello-data.txt.pkg-new', 'P1_STAGE_PRESERVED')
        check('unowned-staging' in output, 'unowned staging file preserved')
        status('rm /usr/share/doc/hello-data.txt.pkg-new', 'P1_STAGE_CLEAR')
        status('pkg install /tmp/hello.zpk /tmp/library.zpk', 'P1_ATOMIC_DEPENDENCIES')
        output = status('sh ' + script, 'P1_EXECUTE'); check('hello-package-v1' in output, 'installed script executes')
        status('pkg remove hello-data', 'P1_DEPENDENT_REMOVE_DENIED', 1)
        status('pkg install /tmp/conflict.zpk', 'P1_CONFLICT_DENIED', 1)
        status('pkg verify', 'P1_VERIFY')
        status('pkg install /tmp/upgrade.zpk', 'P1_UPGRADE')
        output = status('sh ' + script, 'P1_UPDATED_EXECUTE'); check('hello-package-v2' in output, 'updated script without kernel rebuild')
        status('pkg install /tmp/hello.zpk', 'P1_DOWNGRADE_DENIED', 1)
        # Preserve modified owned data rather than clobber it on upgrade/removal.
        status('cp ' + script + ' /tmp/original-script; echo local-change > ' + script, 'P1_LOCAL_EDIT')
        status('pkg verify', 'P1_VERIFY_CHANGED', 1); status('pkg remove hello-tools', 'P1_MODIFIED_REMOVE_DENIED', 1)
        status('cp /tmp/original-script ' + script + '; chmod 755 ' + script, 'P1_RESTORE_SCRIPT')
        # Simulate an interrupted prepared transaction on disk, then cleanly reboot.
        status('cp /var/lib/zenith/pkg/installed.tsv /var/lib/zenith/pkg/old-db; cp ' + script + ' /var/lib/zenith/pkg/undo-0', 'P1_UNDO_BACKUP')
        digest = hashlib.sha256(b'echo hello-package-v2\n').hexdigest()
        journal = 'ZENITH-UNDO\\t1\\t' + '0' * 64 + '\\n' + script + '\\t493\\t' + digest + '\\n'
        status("printf '" + journal + "' > /var/lib/zenith/pkg/journal.tsv; echo partial > " + script + '; sync', 'P1_PREPARED_UNDO')
        disk = shutdown('prepared'); m = boot('recovered', disk)
        status('pkg list', 'P1_RECOVERY_REQUIRED', 1); status('pkg recover', 'P1_RECOVERY')
        output = status('sh ' + script, 'P1_RECOVERED_SCRIPT'); check('hello-package-v2' in output, 'undo survives reboot and restores exact script')
        output = status('pkg list', 'P1_PERSISTENT_DB'); check('hello-tools 1.1.0 [extra]' in output and 'hello-data 1.0.0 [extra]' in output, 'installed DB survives reboot')
        status('pkg verify', 'P1_RECOVERED_VERIFY')
        command('passwd root', 'New password: ')
        offset = len(m.log()); m.send(secret + '\r'); check(m.wait('Repeat password: ', offset), 'hidden root password confirmation')
        offset = len(m.log()); m.send(secret + '\r'); check(m.wait('password updated for root', offset), 'root test password set')
        status('su user', 'P1_USER'); status('pkg remove hello-tools', 'P1_USER_REMOVE_DENIED', 1)
        status('pkg trust /etc/zenith/package-key.pem ' + hashlib.sha256(key).hexdigest(), 'P1_USER_TRUST_DENIED', 1)
        output = status('sh ' + script, 'P1_USER_SCRIPT'); check('hello-package-v2' in output, 'ordinary user can run script')
        command('login root', 'Password: ')
        offset = len(m.log()); m.send(secret + '\r'); check(m.wait('authenticated as root', offset), 'root authenticated')
        status('pkg remove hello-tools', 'P1_REMOVE'); status('pkg remove hello-data', 'P1_REMOVE_DATA')
        status('test ! -e ' + script, 'P1_REMOVED_FILE'); status('pkg verify', 'P1_EMPTY_VERIFY')
        disk = shutdown('removed'); m = None
        volume = keep / 'root.ext2'
        with disk.open('rb') as source:
            source.seek(512); header = source.read(512); entries, _, stride = struct.unpack_from('<QII', header, 72)
            source.seek(entries * 512 + stride); entry = source.read(stride); start, end = struct.unpack_from('<QQ', entry, 32)
            if end < start or end - start > 512 * 1024 * 1024 // 512: raise RuntimeError('invalid root partition')
            source.seek(start * 512)
            with volume.open('wb') as target:
                remaining = (end - start + 1) * 512
                while remaining:
                    data = source.read(min(remaining, 1048576))
                    if not data: raise RuntimeError('short root partition')
                    target.write(data); remaining -= len(data)
        fsck = subprocess.run(['e2fsck', '-f', '-n', str(volume)], capture_output=True); (keep / 'e2fsck.log').write_bytes(fsck.stdout + fsck.stderr)
        check(secret not in ''.join(p.read_text(errors='replace') for p in keep.glob('*/serial.log')), 'credentials absent from transcripts')
        check(fsck.returncode == 0, 'independent clean e2fsck')
        content = subprocess.run(['debugfs', '-R', 'cat /var/lib/zenith/pkg/installed.tsv', str(volume)], capture_output=True)
        check(content.stdout == b'ZENITH-INSTALLED\t1\n', 'independent empty committed DB')
        print(f'RESULT: {checks}/{checks} package checks passed', flush=True)
    finally:
        if m: m.stop()
        server.shutdown(); server.server_close()

if __name__ == '__main__': main()
