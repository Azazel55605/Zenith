#!/usr/bin/env python3
"""Horizon (zenith-native-files-abi 1.0) on a disposable installed disk. Headless, through the real ABI: the
signed package installs and requires the files ABI; a folder lists with item counts, sizes, local times and
owners; transfer jobs copy a multi-chunk file and a folder, keep both / skip / replace when a name exists,
refuse a folder copied into itself, merge folders and ask again for each clashing name inside, move within the root (rename) and across filesystems (copy, then remove),
and delete a tree; another account's copy into /etc ends with permission errors, not changes. On the desktop:
the window opens on a folder; a right click on empty space opens the context menu; tabs open, switch and close; the typed location, select all, copy and paste run
a job; a second paste asks and Enter replaces; a new folder is created, renamed and deleted from the keyboard;
the last tab closes the window. The host compares the copied file byte for byte."""
import boot_image, functools, hashlib, http.server, importlib.util, os, subprocess, sys, threading, time
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
spec = importlib.util.spec_from_file_location('h', ROOT / 'tools/headless-smoke.py'); h = importlib.util.module_from_spec(spec); spec.loader.exec_module(h)
spec = importlib.util.spec_from_file_location('login', ROOT / 'tools/login-session-smoke.py'); login = importlib.util.module_from_spec(spec); spec.loader.exec_module(login)


def main():
    spec = importlib.util.spec_from_file_location('horizon_input', ROOT / 'deploy/distributor/validation-input.py')
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
    subprocess.run([sys.executable, str(ROOT / 'tools/build-userland.py'), str(keep / 'horizon'), '--program', 'horizon', '--version', '0.1.0'], check=True)
    package('build-native', 'zenith-horizon', '0.1.0', keep / 'horizon', keep / 'bundle-horizon')
    manifest = (keep / 'bundle-horizon/manifest.tsv').read_text()
    assert 'requires\tzenith-native-files-abi\t1.0\n' in manifest, manifest
    package('seal', keep / 'bundle-horizon', keep / 'keys/private.pem', served / 'horizon.zpk')
    big = os.urandom(3 * 1024 * 1024 + 12345); (served / 'big.bin').write_bytes(big)
    (served / 'user-copy').write_text('sudo -n -u user native run horizon --job copy skip /etc /home/user/Documents/plan.md | logger\n')
    gui = {'g0': 'native run horizon /home/user/Documents | logger & sleep 6; true',
           'g1': 'poweroff'}
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

    try:
        m = h.Machine(keep / 'live', network=net); assert m.wait('headless ready', timeout=180)
        assert m.wait('network: online address=10.0.2.15', timeout=90)
        status('session set-default headless', 'H_SESSION'); status('install sata0 --yes', 'H_INSTALL', timeout=900)
        command('poweroff', 'all filesystems unmounted'); m.process.wait(timeout=30); disk = m.disk; m.stop(); m = None

        m = h.Machine(keep / 'setup', disk, network=net); assert m.wait('headless ready', timeout=180)
        status('fetch ' + url + '/key.pem /root/key.pem', 'H_KEY')
        status('pkg trust /root/key.pem ' + hashlib.sha256(key).hexdigest(), 'H_TRUST')
        status('fetch ' + url + '/horizon.zpk /root/horizon.zpk', 'H_FETCH')
        status('pkg install /root/horizon.zpk', 'H_PACKAGE')
        for name in [*gui, 'user-copy']: status('fetch ' + url + '/' + name + ' /root/' + name, 'H_SCRIPT_' + name.replace('-', '_'))
        # A small tree in the installer's home, owned by that account.
        for folder in ('Documents', 'Documents/Notes', 'Documents/Notes/Old', 'Archive', 'Archive2'):
            status('mkdir /home/user/' + folder, 'H_MKDIR_' + folder.replace('/', '_'))
        status('echo "# plan" > /home/user/Documents/plan.md', 'H_PLAN')
        status('echo note > /home/user/Documents/Notes/a.txt', 'H_NOTE')
        status('fetch ' + url + '/big.bin /home/user/Documents/big.bin', 'H_BIG', timeout=600)
        for i, path in enumerate(('Documents', 'Documents/Notes', 'Documents/Notes/Old', 'Documents/plan.md', 'Documents/Notes/a.txt', 'Documents/big.bin', 'Archive', 'Archive2')):
            status('chown 1000:1000 /home/user/' + path, 'H_CHOWN' + str(i))

        output = status('native run horizon --list /home/user/Documents | logger', 'H_LIST')
        check('HORIZON_ENTRY Notes folder items=2 ' in output, 'a folder lists with its item count')
        check('HORIZON_ENTRY big.bin file size=' + str(len(big)) + ' ' in output, 'a file lists with its size')
        check('owner=user mode=rw' in output and 'HORIZON_FS / ext2 free=' in output and ' writable' in output, 'owners, modes and the filesystem')
        check(output.index('HORIZON_ENTRY Notes') < output.index('HORIZON_ENTRY big.bin'), 'folders come first')

        output = status('native run horizon --job copy skip /home/user/Archive /home/user/Documents/big.bin /home/user/Documents/Notes | logger', 'H_COPY', timeout=600)
        check('done 4/4 errors=0' in output, 'a file and a folder copy in one job')
        check('files: job ' in m.log() and ' copy of 2 items to /home/user/Archive for pid ' in m.log(), 'the kernel logs the job')
        output = status('native run horizon --list /home/user/Archive | logger', 'H_LIST_COPY')
        check('HORIZON_ENTRY big.bin file size=' + str(len(big)) + ' ' in output and 'HORIZON_ENTRY Notes folder items=2 ' in output, 'the copies are complete')
        output = status('native run horizon --job copy keep /home/user/Archive /home/user/Documents/plan.md | logger', 'H_FIRST')
        output = status('native run horizon --job copy keep /home/user/Archive /home/user/Documents/plan.md | logger', 'H_KEEP')
        check('asking' in output and 'question=plan.md' in output, 'a name that exists asks')
        output = status('native run horizon --list /home/user/Archive | logger', 'H_LIST_KEEP')
        check('HORIZON_ENTRY plan (2).md file' in output, 'keep both names the copy "plan (2).md"')
        output = status('native run horizon --job copy skip /home/user/Archive /home/user/Documents/plan.md | logger', 'H_SKIP')
        check('done 1/1 errors=0' in output, 'skip leaves the existing item')
        status('echo changed > /home/user/Documents/plan.md', 'H_CHANGE')
        status('native run horizon --job copy replace /home/user/Archive /home/user/Documents/plan.md | logger', 'H_REPLACE')
        output = status('cat /home/user/Archive/plan.md | logger', 'H_REPLACED')
        check('user: changed' in output, 'replace overwrites the existing item')
        output = status('native run horizon --job copy skip /home/user/Documents/Notes/Old /home/user/Documents/Notes | logger', 'H_INTO_ITSELF')
        check('HORIZON_JOB refused 22' in output, 'a folder cannot be copied into itself')
        output = status('native run horizon --job move skip /home/user/Archive2 "/home/user/Archive/plan (2).md" | logger', 'H_MOVE')
        check('done 1/1 errors=0' in output, 'a move within the root renames')
        output = status('native run horizon --list /home/user/Archive2 | logger', 'H_LIST_MOVED')
        check('HORIZON_ENTRY plan (2).md file' in output, 'the moved item is in its new folder')
        output = status('native run horizon --job move skip /tmp /home/user/Archive/Notes | logger', 'H_MOVE_TMP')
        check('done 3/3 errors=0' in output, 'a move to another filesystem copies')
        output = status('native run horizon --list /home/user/Archive | logger', 'H_LIST_AFTER_MOVE')
        check('HORIZON_ENTRY Notes' not in output, 'and removes the source afterwards')
        output = status('cat /tmp/Notes/a.txt | logger', 'H_TMP_NOTE')
        check('user: note' in output, 'the moved folder arrived whole')
        output = status('native run horizon --job delete skip - /tmp/Notes | logger', 'H_DELETE')
        check('done 3/3 errors=0' in output, 'delete removes a tree')
        output = status('sh /root/user-copy', 'H_USER_COPY', code=0)
        check('errors=1' in output, "another account's copy into /etc ends with a permission error")
        output = status('ls /etc | logger', 'H_ETC')
        check('user: plan.md' not in output, '/etc is unchanged')
        command('poweroff', 'all filesystems unmounted'); m.process.wait(timeout=30); disk = m.disk; m.stop(); m = None
        # Host audit: the copied file is byte for byte the original.
        start, end = next((p[2], p[3]) for p in boot_image.partitions(disk) if str(p[0]) == '0fc63daf-8483-4772-8e79-3d69d8477de4')
        volume = keep / 'root-volume.img'
        with Path(disk).open('rb') as src, volume.open('wb') as out:
            src.seek(start * 512); remaining = (end - start + 1) * 512
            while remaining:
                data = src.read(min(remaining, 4 << 20)); assert data; out.write(data); remaining -= len(data)
        subprocess.run(['debugfs', '-R', 'dump /home/user/Archive/big.bin ' + str(keep / 'big-copy.bin'), str(volume)], check=True, capture_output=True)
        check((keep / 'big-copy.bin').read_bytes() == big, 'the copied file is byte for byte the original (host debugfs)'); volume.unlink()

        m = h.Machine(keep / 'desktop', disk, 'session=de', network=net + ['-device', 'virtio-gpu-pci', '-device', 'virtio-keyboard-pci', '-device', 'virtio-mouse-pci'], monitor=monitor)
        check(m.wait('desktop ready (installed)', timeout=180), 'the desktop starts')
        observer = profile.RenderedMouse(monitor, monitor_command)
        def point(x, y):
            (cx, cy), _ = observer.position(); monitor_command(monitor, f'mouse_move {x - cx} {y - cy}'); time.sleep(.5)
        def click(x, y, button=1):
            point(x, y); monitor_command(monitor, f'mouse_button {button}'); time.sleep(.15); monitor_command(monitor, 'mouse_button 0')
        time.sleep(2)
        output = typed('sh /root/g0\n', 'user: HORIZON_READY', 'Horizon opens')
        check(m.wait('user: HORIZON_PATH /home/user/Documents 3', 0, 30), 'on the folder it was given')
        check(m.wait('user: HORIZON_FOCUSED', 0, 60), 'and takes focus')
        time.sleep(2); monitor_command(monitor, 'screendump ' + str(keep / 'icons.ppm'))
        off = len(m.log()); click(900, 600, 2)
        check(m.wait('user: HORIZON_MENU open', off, 30), 'a right click on empty space opens the context menu')
        time.sleep(1); monitor_command(monitor, 'screendump ' + str(keep / 'menu.ppm'))
        monitor_command(monitor, 'sendkey esc 30'); time.sleep(1)
        press('ctrl-t', 'user: HORIZON_TABS 2 active=1', 'Ctrl+T opens a tab')
        press('ctrl-tab', 'user: HORIZON_TABS 2 active=0', 'Ctrl+Tab switches tabs')
        press('ctrl-w', 'user: HORIZON_TABS 1 active=0', 'Ctrl+W closes a tab')
        press('ctrl-2', 'user: HORIZON_VIEW details', 'Ctrl+2 shows the details view')
        time.sleep(1); monitor_command(monitor, 'screendump ' + str(keep / 'details.ppm'))
        press('ctrl-a', 'user: HORIZON_SELECTED 3', 'Ctrl+A selects everything')
        press('ctrl-c', 'user: HORIZON_CLIP copy 3', 'Ctrl+C copies the selection')
        monitor_command(monitor, 'sendkey ctrl-l 30'); time.sleep(.5)
        for _ in range(40): monitor_command(monitor, 'sendkey backspace 10')
        typed('/home/user/Archive2\n', 'user: HORIZON_PATH /home/user/Archive2 1', 'the typed location opens a folder')
        output = press('ctrl-v', 'user: HORIZON_JOB 1 done', 'Ctrl+V pastes through a transfer job', timeout=300)
        time.sleep(1); monitor_command(monitor, 'screendump ' + str(keep / 'pasted.ppm'))
        off = len(m.log()); monitor_command(monitor, 'sendkey ctrl-v 30')
        check(m.wait('user: HORIZON_JOB 2 asking', off, 120), 'a second paste asks about the existing names')
        time.sleep(1); monitor_command(monitor, 'screendump ' + str(keep / 'question.ppm'))
        # Merging a folder asks again for each name inside it that exists too.
        for _ in range(10):
            if 'user: HORIZON_JOB 2 done' in m.log()[off:]: break
            monitor_command(monitor, 'sendkey ret 30'); time.sleep(2)
        check('user: HORIZON_ANSWER 4' in m.log()[off:] and 'user: HORIZON_ANSWER 2' in m.log()[off:], 'Enter merges folders and replaces files')
        check(m.wait('user: HORIZON_JOB 2 done', off, 120), 'Enter answers the question and the job finishes')
        press('ctrl-shift-n', 'user: HORIZON_ASK folder', 'Ctrl+Shift+N asks for a folder name')
        time.sleep(.5); monitor_command(monitor, 'screendump ' + str(keep / 'prompt.ppm'))
        press('ret', 'user: HORIZON_CREATED New folder', 'the folder is created')
        press('f2', 'user: HORIZON_ASK rename', 'F2 renames the new folder')
        typed('s\n', 'user: HORIZON_RENAMED New folders', 'and the new name is used')
        press('delete', 'user: HORIZON_ASK delete', 'Delete asks first')
        time.sleep(.5); monitor_command(monitor, 'screendump ' + str(keep / 'delete.ppm'))
        press('ret', 'user: HORIZON_JOB 3 done 1/1 errors=0', 'and deletes after the answer')
        off = len(m.log()); monitor_command(monitor, 'sendkey ctrl-w 30')
        check(m.wait('native surface retired', off, 60), 'closing the last tab closes Horizon')
        time.sleep(2)
        typed('sh /root/g1\n', 'all filesystems unmounted', 'the desktop powers off', timeout=120)
        m.process.wait(timeout=30); disk = m.disk
        m.stop(); m = None
        print(f'PASS Horizon: {checks} checks', flush=True)
    finally:
        if m is not None: m.stop()
        server.shutdown(); server.server_close()


if __name__ == '__main__': main()
