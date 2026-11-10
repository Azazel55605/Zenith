#!/usr/bin/env python3
"""Equinox (zenith-native-disk-abi) on a disposable installed disk: the signed package installs, the
snapshot lists the disks, a plan runs through the ABI on a blank scratch disk (new table, create,
create, delete, grow) and a non-root caller is refused; the window opens on the desktop and
closes on Esc. The host checks the scratch disk's GPT copies and ext2 independently."""
import functools, hashlib, http.server, importlib.util, subprocess, sys, threading, time
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
import boot_image
spec = importlib.util.spec_from_file_location('h', ROOT / 'tools/headless-smoke.py'); h = importlib.util.module_from_spec(spec); spec.loader.exec_module(h)
spec = importlib.util.spec_from_file_location('login', ROOT / 'tools/login-session-smoke.py'); login = importlib.util.module_from_spec(spec); spec.loader.exec_module(login)
GIB = 1024 ** 3


def main():
    spec = importlib.util.spec_from_file_location('equinox_input', ROOT / 'deploy/distributor/validation-input.py')
    profile = importlib.util.module_from_spec(spec); spec.loader.exec_module(profile); profile.install(h.Machine)
    import recovery_console
    profile.install_monitor(recovery_console)
    monitor_command = recovery_console.monitor_command
    keep = Path(sys.argv[1]).resolve(); keep.mkdir(parents=True, exist_ok=False)
    tool = ROOT / 'tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll'
    subprocess.run(['dotnet', 'build', str(ROOT / 'tools/Zenith.PackageTool'), '-c', 'Release'], check=True, stdout=subprocess.DEVNULL)
    def package(*args): subprocess.run(['dotnet', str(tool), *map(str, args)], check=True, capture_output=True)
    served = keep / 'served'; served.mkdir(); package('keygen', keep / 'keys')
    key = (keep / 'keys/public.pem').read_bytes(); (served / 'key.pem').write_bytes(key)
    subprocess.run([sys.executable, str(ROOT / 'tools/build-userland.py'), str(keep / 'equinox'), '--program', 'equinox', '--version', '0.1.0'], check=True)
    package('build-native', 'zenith-equinox', '0.1.0', keep / 'equinox', keep / 'bundle-equinox')
    manifest = (keep / 'bundle-equinox/manifest.tsv').read_text()
    assert 'requires\tzenith-native-disk-abi\t1.0\n' in manifest, manifest
    package('seal', keep / 'bundle-equinox', keep / 'keys/private.pem', served / 'equinox.zpk')
    # The desktop types only short script paths: long commands are staged as files.
    gui_texts = [('native run equinox | logger & sleep 8; true', 'E_WINDOW'), ('echo TERMINAL_ALIVE | logger', 'E_ALIVE'), ('poweroff', None)]
    # Run as another account inside a script, so the console stays root.
    (served / 'user-apply').write_text('su user; export USER=user; native run equinox --apply-test sata1 | logger\n')
    scripts = {}
    for i, (text, label) in enumerate(gui_texts):
        full = text + ('; echo ' + label + '=$? | logger' if label else '')
        scripts[full] = 'e' + str(i)
        (served / scripts[full]).write_text(full + '\n')
    class Handler(http.server.SimpleHTTPRequestHandler):
        def log_message(self, *args): pass
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(Handler, directory=str(served)))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    url = 'http://10.0.2.2:' + str(server.server_port)
    net = ['-netdev', 'user,id=net0', '-device', 'e1000e,netdev=net0']
    monitor = keep / 'monitor.sock'
    scratch = keep / 'scratch.img'
    with scratch.open('wb') as stream: stream.truncate(2 * GIB)
    m = None; checks = 0; gui = False

    def command(text, marker, timeout=300):
        nonlocal checks
        off = len(m.log())
        if gui:
            time.sleep(.5); login.monitor_type(monitor, 'sh /root/' + scripts[text] + '\n')
        else:
            m.send(text + '\r')
        assert m.wait(marker, off, timeout), marker + '; serial=' + str(m.serial)
        checks += 1; print('PASS ' + marker, flush=True)
        return m.log()[off:]

    def status(text, label, code=0, timeout=300): return command(text + '; echo ' + label + '=$? | logger', 'user: ' + label + '=' + str(code), timeout)

    try:
        m = h.Machine(keep / 'live', network=net); assert m.wait('headless ready', timeout=180)
        assert m.wait('network: online address=10.0.2.15', timeout=90)
        status('session set-default headless', 'E_SESSION'); status('install sata0 --yes', 'E_INSTALL', timeout=900)
        command('poweroff', 'all filesystems unmounted'); m.process.wait(timeout=30); disk = m.disk; m.stop(); m = None

        m = h.Machine(keep / 'setup', disk, network=net, extra_disks=(scratch,)); assert m.wait('headless ready', timeout=180)
        status('fetch ' + url + '/key.pem /root/key.pem', 'E_KEY')
        status('pkg trust /root/key.pem ' + hashlib.sha256(key).hexdigest(), 'E_TRUST')
        status('fetch ' + url + '/equinox.zpk /root/equinox.zpk', 'E_FETCH')
        for name in [*scripts.values(), 'user-apply']: status('fetch ' + url + '/' + name + ' /root/' + name, 'E_SCRIPT_' + name.replace('-', '_'))
        status('pkg install /root/equinox.zpk', 'E_PACKAGE')
        output = status('native run equinox --probe | logger', 'E_PROBE')
        assert 'EQUINOX_DISKS count=2 root=yes' in output and 'EQUINOX_DISK sata0' in output and ' system' in output, output[-800:]
        assert 'EQUINOX_DISK sata1 2.0 GiB parts=0' in output, output[-800:]
        checks += 1; print('PASS snapshot lists the system disk and the blank scratch disk', flush=True)
        output = status('native run equinox --apply-test sata1 | logger', 'E_APPLY', timeout=900)
        for step in ('init done', 'create-a done', 'create-b done', 'delete-b done', 'grow-a done'):
            assert 'EQUINOX_STEP ' + step in output, step + ': ' + output[-1200:]
        assert 'check clean' in output and 'EQUINOX_APPLIED' in output, output[-1200:]
        checks += 1; print('PASS plan applied through the disk ABI', flush=True)
        output = status('native run equinox --probe | logger', 'E_PROBE_AFTER')
        assert 'EQUINOX_PART sata1p0 2.0 GiB fs=1 label=EQX' in output, output[-800:]
        # A refused edit is refused before anything runs: the system disk is in use, and non-root is denied.
        output = status('native run equinox --apply-test sata0 | logger', 'E_SYSTEM_REFUSED')
        assert 'EQUINOX_STEP init failed' in output, output[-600:]
        output = status('sh /root/user-apply', 'E_USER_REFUSED')
        assert 'refused=13' in output, output[-600:]
        command('poweroff', 'all filesystems unmounted'); m.process.wait(timeout=30); disk = m.disk
        scratch_after = keep / 'scratch-after.img'
        import shutil; shutil.copyfile(keep / 'setup/secondary.img', scratch_after); m.stop(); m = None

        m = h.Machine(keep / 'desktop', disk, 'session=de', network=net + ['-device', 'virtio-gpu-pci', '-device', 'virtio-keyboard-pci'], monitor=monitor)
        assert m.wait('desktop ready (installed)', timeout=180); gui = True; time.sleep(2)
        off = len(m.log())
        status('native run equinox | logger & sleep 8; true', 'E_WINDOW')
        assert 'user: EQUINOX_READY' in m.log()[off:], 'Equinox did not open: ' + m.log()[off:][-400:]
        assert m.wait('user: EQUINOX_FOCUSED', off, 60), 'Equinox never took focus'
        monitor_command(monitor, 'screendump ' + str(keep / 'equinox.ppm'))
        closed = len(m.log()); monitor_command(monitor, 'sendkey esc 30')
        assert m.wait('native surface retired', closed, 60), 'Equinox did not close on Esc'
        checks += 1; print('PASS Equinox window opens, takes focus and closes', flush=True)
        status('echo TERMINAL_ALIVE | logger', 'E_ALIVE')
        command('poweroff', 'all filesystems unmounted'); m.process.wait(timeout=30)

        parts = boot_image.partitions(scratch_after)
        assert len(parts) == 1 and (parts[0][3] - parts[0][2] + 1) * 512 > 1.9 * GIB, parts
        volume = keep / 'scratch-volume.img'
        with scratch_after.open('rb') as src, volume.open('wb') as out:
            src.seek(parts[0][2] * 512); remaining = (parts[0][3] - parts[0][2] + 1) * 512
            while remaining:
                data = src.read(min(remaining, 4 << 20)); assert data; out.write(data); remaining -= len(data)
        audit = subprocess.run(['e2fsck', '-f', '-n', str(volume)], capture_output=True, text=True)
        (keep / 'e2fsck.log').write_text(audit.stdout + audit.stderr); assert audit.returncode == 0, 'scratch ext2 audit'
        checks += 1
        print(f'PASS Equinox: {checks} checks; host GPT and ext2 audits passed', flush=True)
    finally:
        if m is not None: m.stop()
        server.shutdown(); server.server_close()


if __name__ == '__main__': main()
