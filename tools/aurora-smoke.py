#!/usr/bin/env python3
"""Aurora (zenith-aurora, the text editor) on a disposable installed disk. The signed package installs and requires
the clipboard ABI (calls 639/659). On the desktop with whole-app transparency on: Aurora opens a CRLF C# file on
the window material; Ctrl+G goes to a line; typed text is saved (staged, then renamed over the file); Ctrl+A and
Ctrl+C copy through call 639 and Ctrl+V pastes through 659, which the kernel answers only after that key; Ctrl+Z
undoes the paste; Ctrl+F finds and Ctrl+H replaces; Ctrl+Shift+S saves a copy under a new name; a binary file is
refused; closing a changed document asks and Don't save ends Aurora. No frame needed repairing for the desktop's
material rule. The host then reads both files from the disk (debugfs): CRLF line ends kept, the edits present."""
import boot_image, functools, hashlib, http.server, importlib.util, re, subprocess, sys, threading, time
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
spec = importlib.util.spec_from_file_location('h', ROOT / 'tools/headless-smoke.py'); h = importlib.util.module_from_spec(spec); spec.loader.exec_module(h)
spec = importlib.util.spec_from_file_location('login', ROOT / 'tools/login-session-smoke.py'); login = importlib.util.module_from_spec(spec); spec.loader.exec_module(login)

SOURCE = ('// Bounded session restore; a damaged record is skipped, never fatal.\r\n'
          'internal sealed class SessionStore : IDisposable\r\n'
          '{\r\n'
          '    private const int MaximumEntries = 128;\r\n'
          '    public bool TryRestore(string path) { return path.Length > 0; }\r\n'
          '}\r\n').encode()


def main():
    spec = importlib.util.spec_from_file_location('aurora_input', ROOT / 'deploy/distributor/validation-input.py')
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
    subprocess.run([sys.executable, str(ROOT / 'tools/build-userland.py'), str(keep / 'aurora'), '--program', 'aurora', '--version', '0.1.0'], check=True)
    package('build-native', 'zenith-aurora', '0.1.0', keep / 'aurora', keep / 'bundle-aurora')
    manifest = (keep / 'bundle-aurora/manifest.tsv').read_text()
    assert 'requires\tzenith-native-clipboard-abi\t1.0\n' in manifest and 'requires\tzenith-native-files-abi\t1.0\n' in manifest, manifest
    package('seal', keep / 'bundle-aurora', keep / 'keys/private.pem', served / 'aurora.zpk')
    (served / 'session.cs').write_bytes(SOURCE)
    (served / 'image.bin').write_bytes(b'\x7fELF\x02\x01\x01\x00' + bytes(range(256)))
    # Whole-app transparency on: Aurora draws its chrome on the window material; code stays opaque.
    gui = {'g0': 'settings set materials background-blur | logger; native run aurora /root/work/session.cs | logger & sleep 6; true',
           'g1': 'poweroff',
           # AU3: the process list, to end Aurora as a crash would; then Aurora again to find the recovery copy.
           'g2': 'ps | logger',
           # Window opening: the gate installs with compact windows; this start opens sized to the screen.
           'g3': 'settings set windows fit | logger; native run aurora | logger & sleep 6; true'}
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
        status('settings set windows compact; session set-default headless', 'A_SESSION'); status('install sata0 --yes', 'A_INSTALL', timeout=900)
        command('poweroff', 'all filesystems unmounted'); m.process.wait(timeout=30); disk = m.disk; m.stop(); m = None

        m = h.Machine(keep / 'setup', disk, network=net); assert m.wait('headless ready', timeout=180)
        status('fetch ' + url + '/key.pem /root/key.pem', 'A_KEY')
        status('pkg trust /root/key.pem ' + hashlib.sha256(key).hexdigest(), 'A_TRUST')
        status('fetch ' + url + '/aurora.zpk /root/aurora.zpk', 'A_FETCH')
        status('pkg install /root/aurora.zpk', 'A_PACKAGE')
        status('mkdir /root/work', 'A_WORK')
        for name in ('session.cs', 'image.bin'): status('fetch ' + url + '/' + name + ' /root/work/' + name, 'A_FILE_' + name.replace('.', '_'))
        for name in gui: status('fetch ' + url + '/' + name + ' /root/' + name, 'A_SCRIPT_' + name)
        output = status('native run aurora --version | logger', 'A_VERSION')
        check('user: aurora 0.1.0' in output, 'the installed program answers --version')
        command('poweroff', 'all filesystems unmounted'); m.process.wait(timeout=30); disk = m.disk; m.stop(); m = None

        m = h.Machine(keep / 'desktop', disk, 'session=de', network=net + ['-device', 'virtio-gpu-pci', '-device', 'virtio-keyboard-pci', '-device', 'virtio-mouse-pci'], monitor=monitor)
        check(m.wait('desktop ready (installed)', timeout=180), 'the desktop starts')
        time.sleep(2)
        typed('sh /root/g0\n', 'user: AURORA_READY', 'Aurora opens')
        check('user: AURORA_MATERIAL window' in m.log(), 'Aurora draws on the window material when app backgrounds are transparent')
        check('user: AURORA_OPENED /root/work/session.cs bytes=' + str(len(SOURCE.replace(b'\r\n', b'\n'))) + ' lines=7 crlf' in m.log(),
              'the file opens with its CRLF line ends recognised')
        check(m.wait('user: AURORA_FOCUSED', 0, 60), 'and takes focus')
        shot('editor')
        monitor_command(monitor, 'sendkey ctrl-g 30')
        time.sleep(.5)
        typed('3\n', 'user: AURORA_GOTO 3', 'Ctrl+G goes to line 3')
        monitor_command(monitor, 'sendkey end 30'); monitor_command(monitor, 'sendkey ret 30')
        login.monitor_type(monitor, '    // added by the gate')
        press('ctrl-s', 'user: AURORA_SAVED /root/work/session.cs bytes=', 'Ctrl+S saves')
        monitor_command(monitor, 'sendkey ctrl-a 30')
        output = press('ctrl-c', 'user: AURORA_COPIED ', 'Ctrl+C copies through the clipboard call')
        copied = output.split('user: AURORA_COPIED ', 1)[1].split()[0]
        monitor_command(monitor, 'sendkey ctrl-end 30')
        press('ctrl-v', 'user: AURORA_PASTED ' + copied, 'Ctrl+V pastes the copied text (the kernel grants the read for that key)')
        shot('pasted')
        press('ctrl-z', 'user: AURORA_UNDO', 'Ctrl+Z undoes the paste')
        monitor_command(monitor, 'sendkey ctrl-f 30'); time.sleep(.5)
        typed('SessionStore', 'user: AURORA_FIND 1 SessionStore', 'Ctrl+F finds the class name')
        shot('find')
        monitor_command(monitor, 'sendkey ctrl-h 30'); time.sleep(.5)
        typed('Store2\n', 'user: AURORA_REPLACED 1', 'Enter in the replace field replaces the match')
        monitor_command(monitor, 'sendkey esc 30'); monitor_command(monitor, 'sendkey esc 30')
        press('ctrl-s', 'user: AURORA_SAVED /root/work/session.cs', 'the replacement is saved')
        press('ctrl-shift-s', 'user: AURORA_FILE_DIALOG save', 'Ctrl+Shift+S asks for a name')
        shot('save-as')
        for _ in range(20): monitor_command(monitor, 'sendkey backspace 10')
        typed('copy.cs\n', 'user: AURORA_SAVED /root/work/copy.cs', 'Save as writes a copy under the new name')
        press('ctrl-o', 'user: AURORA_FILE_DIALOG open', 'Ctrl+O shows the open dialog')
        typed('/root/work/image.bin\n', 'user: AURORA_REFUSED /root/work/image.bin', 'a binary file is refused')
        shot('refused')
        monitor_command(monitor, 'sendkey ret 30'); time.sleep(.5)
        # AU2: regular expressions in find (Alt+R) and overwrite mode (Insert).
        monitor_command(monitor, 'sendkey ctrl-f 30'); time.sleep(.5)
        press('alt-r', 'user: AURORA_REGEX on', 'Alt+R turns on regular expressions')
        for _ in range(30): monitor_command(monitor, 'sendkey backspace 10')
        typed('Max[a-z]+', 'user: AURORA_FIND 1 Max[a-z]+', 'a regular expression finds MaximumEntries')
        shot('regex')
        monitor_command(monitor, 'sendkey esc 30'); monitor_command(monitor, 'sendkey esc 30'); time.sleep(.3)
        press('insert', 'user: AURORA_OVERWRITE on', 'Insert switches to overwrite')
        press('insert', 'user: AURORA_OVERWRITE off', 'and back')
        # AU4: folding (Ctrl+Shift+- and Ctrl+Shift+=) and several cursors (Ctrl+Alt+Down, Esc), in copy.cs.
        monitor_command(monitor, 'sendkey ctrl-g 30'); time.sleep(.5)
        typed('3\n', 'user: AURORA_GOTO 3', 'to the opening brace')
        press('ctrl-shift-minus', 'user: AURORA_FOLD 3 hides 3 lines', 'Ctrl+Shift+- folds the class body')
        shot('folded')
        press('ctrl-shift-equal', 'user: AURORA_UNFOLD 3', 'Ctrl+Shift+= unfolds it')
        monitor_command(monitor, 'sendkey ctrl-g 30'); time.sleep(.5)
        typed('4\n', 'user: AURORA_GOTO 4', 'to the first line of the body')
        press('ctrl-alt-down', 'user: AURORA_CURSORS 2', 'Ctrl+Alt+Down adds a cursor below')
        press('ctrl-alt-down', 'user: AURORA_CURSORS 3', 'and another')
        login.monitor_type(monitor, '//'); shot('cursors')
        press('esc', 'user: AURORA_CURSORS 1', 'Esc keeps one cursor')
        press('ctrl-z', 'user: AURORA_UNDO', 'one undo takes back the text typed at all three')
        # AU3: unsaved text gets a recovery copy; Aurora ends without its own cleanup (kill), and the next start offers it.
        off = len(m.log()); login.monitor_type(monitor, 'x')
        check(m.wait('user: AURORA_RECOVERY_SAVED copy.cs', off, 60), 'unsaved text gets a recovery copy')
        # The terminal shows beside Aurora's window on the left: a click gives it the keyboard.
        observer = profile.RenderedMouse(monitor, monitor_command)
        (cx, cy), _ = observer.position(); monitor_command(monitor, f'mouse_move {330 - cx} {420 - cy}'); time.sleep(.5)
        monitor_command(monitor, 'mouse_button 1'); time.sleep(.15); monitor_command(monitor, 'mouse_button 0'); time.sleep(1)
        output = typed('sh /root/g2\n', ' aurora', 'ps lists Aurora')
        pid = next(line.split('user: ', 1)[1].split()[0] for line in m.log().splitlines() if line.rstrip().endswith(' aurora') and 'user: ' in line)
        off = len(m.log()); login.monitor_type(monitor, 'kill ' + pid + '\n')
        check(m.wait('native surface retired', off, 60), 'kill ends Aurora as a crash would')
        check('AURORA_CLOSED' not in m.log()[off:], 'without Aurora closing itself')
        time.sleep(2)
        off = len(m.log())
        typed('sh /root/g3\n', 'user: AURORA_RECOVERY_FOUND copy.cs', 'the next Aurora finds the recovery copy')
        fitted = re.search(r'native surface fitted 640x480 to (\d+)x(\d+)', m.log()[off:])
        check(fitted is not None and int(fitted[1]) > 640 and int(fitted[2]) > 480, 'with windows=fit Aurora opens larger on a big screen')
        check(fitted is not None and m.wait('native surface opened ' + fitted[1] + 'x' + fitted[2], off, 30), 'its first shown frame already has that size')
        shot('recovery')
        press('ret', 'user: AURORA_RECOVERED copy.cs', 'Restore brings the unsaved text back')
        press('ctrl-w', 'user: AURORA_DIALOG Save changes to', 'closing a changed document asks first')
        shot('unsaved')
        monitor_command(monitor, 'sendkey shift-tab 30'); time.sleep(.3)
        off = len(m.log()); monitor_command(monitor, 'sendkey ret 30')
        check(m.wait('user: AURORA_CLOSED', off, 60), "Don't save closes the last document and Aurora")
        check(m.wait('native surface retired', off, 60), 'the window goes away')
        check('AURORA_FRAME_REPAIRED' not in m.log(), 'no frame needed repairing for the material rule')
        time.sleep(2)
        typed('sh /root/g1\n', 'all filesystems unmounted', 'the desktop powers off', timeout=120)
        m.process.wait(timeout=30); disk = m.disk; m.stop(); m = None
        # Host audit: the saved files, read from the disk.
        start, end = next((p[2], p[3]) for p in boot_image.partitions(disk) if str(p[0]) == '0fc63daf-8483-4772-8e79-3d69d8477de4')
        volume = keep / 'root-volume.img'
        with Path(disk).open('rb') as src, volume.open('wb') as out:
            src.seek(start * 512); remaining = (end - start + 1) * 512
            while remaining:
                data = src.read(min(remaining, 4 << 20)); assert data; out.write(data); remaining -= len(data)
        for name in ('session.cs', 'copy.cs'):
            subprocess.run(['debugfs', '-R', 'dump /root/work/' + name + ' ' + str(keep / name), str(volume)], check=True, capture_output=True)
        saved = (keep / 'session.cs').read_bytes()
        expected = SOURCE.replace(b'{\r\n', b'{\r\n    // added by the gate\r\n', 1).replace(b'class SessionStore', b'class Store2', 1)
        check(saved == expected, 'the saved file has the edits and keeps CRLF (host debugfs)')
        check((keep / 'copy.cs').read_bytes() == expected, 'the copy saved under the new name is the same text')
        volume.unlink()
        print(f'PASS Aurora: {checks} checks', flush=True)
    finally:
        if m is not None: m.stop()
        server.shutdown(); server.server_close()


if __name__ == '__main__': main()
