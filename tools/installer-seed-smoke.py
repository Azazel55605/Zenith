#!/usr/bin/env python3
"""Installer ISO with a seed (tools/seed-iso.py): the live boot pins the seeded package key, TLS CA
and HTTPS mirror; an installation carries that trust and installs every seeded core package; the
installed system refreshes from the mirror and verifies its packages with no manual trust step,
and starts the graphical login. Packages are built from this checkout and served like the
distributor publishes them; the seed is made from that served root."""
import argparse, functools, hashlib, http.server, importlib.util, shutil, subprocess, sys, threading, time
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
import boot_image
from tls_fixture import certificates, context
spec = importlib.util.spec_from_file_location('h', ROOT / 'tools/headless-smoke.py'); h = importlib.util.module_from_spec(spec); spec.loader.exec_module(h)
spec = importlib.util.spec_from_file_location('seed_iso', ROOT / 'tools/seed-iso.py'); seed_iso = importlib.util.module_from_spec(spec); spec.loader.exec_module(seed_iso)
TOOL = ROOT / 'tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll'


def main():
    parser = argparse.ArgumentParser(description=__doc__); parser.add_argument('keep', type=Path); args = parser.parse_args()
    profile_spec = importlib.util.spec_from_file_location('seed_input', ROOT / 'deploy/distributor/validation-input.py')
    profile = importlib.util.module_from_spec(profile_spec); profile_spec.loader.exec_module(profile); profile.install(h.Machine)
    keep = args.keep.resolve(); keep.mkdir(parents=True, exist_ok=False)
    def run(*command): subprocess.run(['dotnet', str(TOOL), *map(str, command)], check=True, stdout=subprocess.DEVNULL)
    run('keygen', keep / 'keys')
    ca, ca_sha = certificates(keep / 'tls')
    served = keep / 'served'
    (keep / 'build').mkdir()
    built = seed_iso.build_recipes('core', keep / 'keys', keep / 'build')
    names = sorted(name for name, _, _ in built)
    for name, version, archive in built:
        target = served / 'core' / name / version / (name + '.zpk'); target.parent.mkdir(parents=True); shutil.copyfile(archive, target)
    (served / 'extra').mkdir(parents=True)
    now = int(time.time())
    for repository in ('core', 'extra'):
        run('index-seal', repository, 1, now - 30, now + 86400, served, keep / 'keys/private.pem', served / repository / 'index.zri')

    class Handler(http.server.SimpleHTTPRequestHandler):
        def log_message(self, *a): pass
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(Handler, directory=str(served)))
    server.socket = context(keep / 'tls').wrap_socket(server.socket, server_side=True)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    mirror = 'https://10.0.2.2:' + str(server.server_port)
    iso = keep / 'installer.iso'
    subprocess.run([sys.executable, str(ROOT / 'tools/seed-iso.py'), str(ROOT / 'output-x64/Zenith.iso'), str(iso),
                    '--package-key', str(keep / 'keys/public.pem'), '--tls-ca', str(keep / 'tls/ca.pem'),
                    '--mirror', mirror, '--packages-root', str(served)], check=True)
    key_sha = hashlib.sha256((keep / 'keys/public.pem').read_bytes()).hexdigest()
    net = ['-netdev', 'user,id=net0', '-device', 'e1000e,netdev=net0']
    m = None; checks = 0

    def check(ok, label):
        nonlocal checks
        if not ok: raise RuntimeError(label + '; serial=' + str(m.serial if m else ''))
        checks += 1; print('PASS ' + label, flush=True)

    def status(text, label, code=0, timeout=300):
        off = len(m.log()); m.send(text + '; echo ' + label + '=$? | logger\r')
        check(m.wait('user: ' + label + '=' + str(code), off, timeout), label)
        return m.log()[off:]

    try:
        m = h.Machine(keep / 'live', network=net, iso=iso); check(m.wait('headless ready', timeout=180), 'live ISO boots with its seed')
        log = m.log()
        check('installer seed applied: package key ' + key_sha[:16] + ', mirror ' + mirror
              + '; packages=' + str(len(names)) in log, 'live boot pinned the seeded key and mirror')
        check(m.wait('installer seed TLS CA ' + ca_sha[:16] + ' applied', timeout=120), 'live boot trusts the seeded TLS CA')
        check('not applied' not in log and 'seed ignored' not in log, 'no seed part refused')
        output = status('pkg repos | logger', 'S_REPOS')
        check('core priority=10 mirror=' + mirror in output, 'live repositories use the seeded mirror')
        status('session set-default headless', 'S_SESSION')
        output = status('install sata0 --yes', 'S_INSTALL', timeout=1200)
        check('installer: seed packages installed=' + str(len(names)) in m.log(), 'Sunrise installed every seeded package')
        off = len(m.log()); m.send('poweroff\r'); check(m.wait('all filesystems unmounted', off, 120), 'live poweroff')
        m.process.wait(timeout=30); disk = m.disk; m.stop(); m = None

        m = h.Machine(keep / 'installed', disk, network=net); check(m.wait('headless ready', timeout=180), 'installed disk boots')
        check(m.wait('network: online address=10.0.2.15', timeout=90), 'installed network online')
        output = status('pkg list | logger', 'S_LIST')
        missing = [name for name in names if name + ' ' not in output]
        check(not missing, 'installed system lists every seeded package (missing: ' + ', '.join(missing) + ')')
        output = status('pkg verify | logger', 'S_VERIFY')
        check('package verification errors=0' in output, 'seeded packages verify against the pinned key')
        status('pkg refresh', 'S_REFRESH', timeout=600)
        output = status('pkg search zenith-settings | logger', 'S_SEARCH')
        check('zenith-settings ' in output and '[core;' in output, 'refreshed signed index over the seeded TLS trust')
        off = len(m.log()); m.send('poweroff\r'); check(m.wait('all filesystems unmounted', off, 120), 'installed poweroff')
        m.process.wait(timeout=30); disk = m.disk; m.stop(); m = None

        m = h.Machine(keep / 'desktop', disk, 'session=de', network=net + ['-device', 'virtio-gpu-pci', '-device', 'virtio-keyboard-pci'])
        check(m.wait('desktop ready (installed)', timeout=240), 'desktop starts from the seeded session packages')
        m.stop(); m = None

        inventory = boot_image.verify(disk, ROOT / 'output-x64/Zenith.elf')
        part = next(p for p in boot_image.partitions(disk) if str(p[1]) == inventory['root'])
        volume = keep / 'root.img'
        with disk.open('rb') as source, volume.open('wb') as out:
            source.seek(part[2] * 512); remaining = (part[3] - part[2] + 1) * 512
            while remaining:
                data = source.read(min(remaining, 4 << 20)); assert data; out.write(data); remaining -= len(data)
        audit = subprocess.run(['e2fsck', '-f', '-n', str(volume)], capture_output=True, text=True)
        (keep / 'e2fsck.log').write_text(audit.stdout + audit.stderr); check(audit.returncode == 0, 'installed ext2 audit clean')
        print(f'PASS installer seed: {checks} checks', flush=True)
    finally:
        if m is not None: m.stop()
        server.shutdown(); server.server_close()


if __name__ == '__main__': main()
