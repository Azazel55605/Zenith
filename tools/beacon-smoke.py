#!/usr/bin/env python3
"""Beacon gate (docs/components/BEACON.md): the signed zenith-beacon package installs and requires the network-center
ABI (call 667, kernel 0.0.69). Headless, as root: status, the four traffic ranges, diagnostics from the adapter outward,
ping, a manual configuration, one whose gateway does not answer (the previous settings come back), automatic again,
disconnect and connect, and the history of all of it. An ordinary user may read but not change. On the desktop the
window opens on the answer, its pages, diagnostics and the editor work by keyboard, and the history survives a restart."""
import functools, hashlib, http.server, importlib.util, re, subprocess, sys, threading, time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
spec = importlib.util.spec_from_file_location('h', ROOT / 'tools/headless-smoke.py'); h = importlib.util.module_from_spec(spec); spec.loader.exec_module(h)
spec = importlib.util.spec_from_file_location('login', ROOT / 'tools/login-session-smoke.py'); login = importlib.util.module_from_spec(spec); spec.loader.exec_module(login)


def main():
    spec = importlib.util.spec_from_file_location('beacon_input', ROOT / 'deploy/distributor/validation-input.py')
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
    subprocess.run([sys.executable, str(ROOT / 'tools/build-userland.py'), str(keep / 'beacon'), '--program', 'beacon', '--version', '0.1.0'], check=True)
    package('build-native', 'zenith-beacon', '0.1.0', keep / 'beacon', keep / 'bundle-beacon')
    manifest = (keep / 'bundle-beacon/manifest.tsv').read_text()
    assert 'requires\tzenith-native-network-center-abi\t1.0\n' in manifest and 'requires\tzenith-native-portal-abi\t1.0\n' in manifest, manifest
    package('seal', keep / 'bundle-beacon', keep / 'keys/private.pem', served / 'beacon.zpk')
    gui = {'g0': 'native run beacon | logger & sleep 6; true', 'g1': 'poweroff'}
    for name, text in gui.items(): (served / name).write_text(text + '\n')
    class Handler(http.server.SimpleHTTPRequestHandler):
        def log_message(self, *args): pass
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(Handler, directory=str(served)))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    url = 'http://10.0.2.2:' + str(server.server_port)
    net = ['-netdev', 'user,id=net0', '-device', 'e1000e,netdev=net0']
    monitor = keep / 'monitor.sock'
    m = None; checks = 0

    def check(ok, label):
        nonlocal checks
        if not ok:
            if m is not None and monitor.exists():
                try: monitor_command(monitor, 'screendump ' + str(keep / 'failure.ppm'))
                except (OSError, RuntimeError): pass
            raise AssertionError(label + '; log tail: ' + (m.log()[-2500:] if m else ''))
        checks += 1; print('PASS ' + label, flush=True)

    def command(text, marker, timeout=300, label=None):
        off = len(m.log()); m.send(text + '\r')
        check(m.wait(marker, off, timeout), label or marker)
        return m.log()[off:]

    def status(text, label, code=0, timeout=300):
        return command(text + '; echo ' + label + '=$? | logger', 'user: ' + label + '=' + str(code), timeout)

    def press(keys, marker, label, timeout=60):
        off = len(m.log()); monitor_command(monitor, 'sendkey ' + keys + ' 30')
        check(m.wait(marker, off, timeout), label)
        return m.log()[off:]

    def typed(text, marker, label, timeout=60):
        off = len(m.log()); login.monitor_type(monitor, text)
        check(m.wait(marker, off, timeout), label)
        return m.log()[off:]

    def shot(name):
        time.sleep(1); monitor_command(monitor, 'screendump ' + str(keep / (name + '.ppm')))

    try:
        m = h.Machine(keep / 'live', network=net); assert m.wait('headless ready', timeout=180)
        assert m.wait('network: online address=10.0.2.15', timeout=90)
        status('settings set windows compact; session set-default headless', 'B_SESSION'); status('install sata0 --yes', 'B_INSTALL', timeout=900)
        command('poweroff', 'all filesystems unmounted'); m.process.wait(timeout=30); disk = m.disk; m.stop(); m = None

        m = h.Machine(keep / 'setup', disk, network=net); assert m.wait('headless ready', timeout=180)
        check(m.wait('network: online address=10.0.2.15', 0, 90), 'the installed system is online through DHCP')
        status('fetch ' + url + '/key.pem /root/key.pem', 'B_KEY')
        status('pkg trust /root/key.pem ' + hashlib.sha256(key).hexdigest(), 'B_TRUST')
        status('fetch ' + url + '/beacon.zpk /root/beacon.zpk', 'B_FETCH')
        status('pkg install /root/beacon.zpk', 'B_PACKAGE')
        for name in gui: status('fetch ' + url + '/' + name + ' /root/' + name, 'B_SCRIPT_' + name)
        output = status('native run beacon --version | logger', 'B_VERSION')
        check('user: beacon 0.1.0' in output, 'the installed program answers --version')
        out = status('native run beacon --status | logger', 'B_STATUS')
        check('state=4 mode=0 address=10.0.2.15/24 gateway=10.0.2.2' in out, 'the status: online, automatic, address and gateway')
        check(re.search(r'connected=\d+', out) is not None and 'Connected' in out, 'the headline says connected and how long')
        out = status('sleep 3; native run beacon --history | logger', 'B_HISTORY')
        check(re.search(r'BEACON_HISTORY 0 [1-9]\d* x 1 s', out) is not None, 'the minute range has per-second samples')
        check('BEACON_HISTORY 3 ' in out and ' x 3600 s' in out, 'the week range is offered (hourly)')
        off = len(m.log()); m.send('native run beacon --check | logger; echo B_CHECK=done | logger\r')
        check(m.wait('user: B_CHECK=done', off, 300), 'diagnostics run')
        out = m.log()[off:]
        for step in ('Adapter ok', 'Address ok', 'Gateway ok'):
            check('BEACON_CHECK ' + step in out, 'diagnostics: ' + step)
        check(re.search(r'BEACON_CHECK DNS (ok|slow)', out) is not None, 'diagnostics: DNS answers')
        out = status('native run beacon --ping 10.0.2.2 | logger', 'B_PING')
        check(len(re.findall(r'BEACON_PING 10\.0\.2\.2 \d+ ms', out)) >= 3, 'ping the gateway: replies with times')
        out = status('native run beacon --apply manual 10.0.2.15/24 10.0.2.2 10.0.2.3 | logger', 'B_MANUAL')
        check('BEACON_APPLIED' in out, 'manual settings apply')
        out = status('native run beacon --status | logger', 'B_STATUS_MANUAL')
        check('state=4 mode=1 address=10.0.2.15/24' in out, 'and are active (manual)')
        # The pipe's status is logger's: Beacon's own markers say what happened.
        out = status('native run beacon --apply manual 10.0.2.16/24 10.0.2.99 10.0.2.3 | logger', 'B_BROKEN')
        check('BEACON_KEPT' in out and 'BEACON_APPLIED' not in out, 'a gateway that does not answer is refused')
        check('previous settings are back' in out, 'and the previous settings come back')
        out = status('native run beacon --status | logger', 'B_STATUS_KEPT')
        check('state=4 mode=1 address=10.0.2.15/24' in out, 'the connection still works with the previous settings')
        out = status('native run beacon --apply auto | logger', 'B_AUTO')
        check('BEACON_APPLIED' in out, 'automatic (DHCP) again')
        out = status('native run beacon --connect off | logger', 'B_OFF')
        out = status('native run beacon --status | logger', 'B_STATUS_OFF')
        check('state=1 ' in out, 'disconnect: the state says so')
        status('native run beacon --connect on | logger', 'B_ON')
        out = status('native run beacon --status | logger', 'B_STATUS_ON')
        check('state=4 mode=0 address=10.0.2.15' in out, 'connect: online again with the saved settings')
        out = status('native run beacon --events | logger', 'B_EVENTS')
        for words in ('Settings changed: manual 10.0.2.15', 'Kept the previous settings', 'Disconnected', 'Connected, address 10.0.2.15'):
            check(words in out, 'the history records: ' + words)
        out = status('sudo -n -u user native run beacon --apply auto | logger', 'B_USER')
        check('BEACON_DENIED' in out, 'an ordinary user cannot change the connection')
        out = status('sudo -n -u user native run beacon --status | logger', 'B_USER_STATUS')
        check('state=4' in out, 'but may read it')
        command('poweroff', 'all filesystems unmounted'); m.process.wait(timeout=30); disk = m.disk; m.stop(); m = None

        # An HD Audio device (silent backend) so the taskbar's sound center has an output to change.
        audio = ['-audiodev', 'none,id=sound', '-device', 'intel-hda', '-device', 'hda-duplex,audiodev=sound']
        m = h.Machine(keep / 'desktop', disk, 'session=de', network=net + ['-device', 'virtio-gpu-pci', '-device', 'virtio-keyboard-pci', '-device', 'virtio-mouse-pci'] + audio, monitor=monitor)
        check(m.wait('desktop ready (installed)', timeout=180), 'the desktop starts')
        time.sleep(2)
        typed('sh /root/g0\n', 'user: BEACON_READY', 'Beacon opens')
        check(m.wait('user: BEACON_STATE 4 10.0.2.15', 0, 30), 'on the answer: online at 10.0.2.15')
        check(m.wait('user: BEACON_FOCUSED', 0, 60), 'and takes focus')
        shot('overview')
        press('ctrl-5', 'user: BEACON_PAGE Diagnostics', 'Ctrl+5 shows Diagnostics')
        press('ctrl-r', 'user: BEACON_CHECKS start', 'Ctrl+R runs the checks again')
        check(m.wait('user: BEACON_CHECKS done', 0, 300), 'and they finish')
        shot('diagnostics')
        press('ctrl-4', 'user: BEACON_PAGE Traffic', 'Ctrl+4 shows Traffic'); shot('traffic')
        press('ctrl-6', 'user: BEACON_PAGE History', 'Ctrl+6 shows the history, kept across the restart'); shot('history')
        press('ctrl-2', 'user: BEACON_PAGE Wi-Fi', 'Wi-Fi says there is no adapter'); shot('wifi')
        press('ctrl-1', 'user: BEACON_PAGE Wired', 'back to Wired')
        press('ctrl-e', 'user: BEACON_EDIT open', 'Ctrl+E opens the editor'); shot('editor')
        press('esc', 'user: BEACON_EDIT closed', 'Esc closes it unchanged')
        off = len(m.log()); monitor_command(monitor, 'sendkey esc 30')
        check(m.wait('native surface retired', off, 60), 'Esc closes Beacon')
        time.sleep(2)
        # The taskbar's network and sound centers, by pointer (the desktop logs where their buttons are).
        # The latest position: the buttons move when the clock's width changes.
        found = re.findall(r'taskbar: centers network=(\d+),(\d+) sound=(\d+),(\d+)', m.log())
        check(len(found) > 0, 'the taskbar has network and sound buttons beside the clock')
        nx, ny, sx, sy = map(int, found[-1])
        observer = profile.RenderedMouse(monitor, monitor_command)
        def point(x, y):
            (cx, cy), _ = observer.position(); monitor_command(monitor, f'mouse_move {x - cx} {y - cy}'); time.sleep(.5)
        def click(x, y):
            point(x, y); monitor_command(monitor, 'mouse_button 1'); time.sleep(.15); monitor_command(monitor, 'mouse_button 0')
        off = len(m.log()); click(nx, ny)
        check(m.wait('taskbar: network center opened', off, 30), 'the network center opens'); shot('network-center')
        off = len(m.log()); click(sx, sy)
        check(m.wait('taskbar: sound center opened', off, 30), 'the sound center opens in its place'); shot('sound-center')
        off = len(m.log()); point(sx, sy); monitor_command(monitor, 'mouse_move 0 0 -1')
        check(m.wait('taskbar: volume ', off, 30), 'the wheel over the sound button changes the volume')
        off = len(m.log()); monitor_command(monitor, 'sendkey esc 30')
        check(m.wait('taskbar: centers closed', off, 30), 'Esc closes it')
        typed('sh /root/g1\n', 'all filesystems unmounted', 'the desktop powers off', timeout=120)
        m.process.wait(timeout=30); m.stop(); m = None
        print(f'PASS Beacon: {checks} checks', flush=True)
    finally:
        if m is not None: m.stop()
        server.shutdown(); server.server_close()


if __name__ == '__main__': main()
