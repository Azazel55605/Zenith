#!/usr/bin/env python3
"""sudo and the Desktop Portal on a disposable installed disk. Headless, as the installer's account:
the wheel group is created at startup with that account in it; sudo asks for the account's own
password, runs one command as root (a native program too) and leaves the terminal's identity alone,
remembers the password for this terminal, forgets it with -k, refuses three wrong passwords, and
refuses an account outside wheel until gpasswd adds it. On the desktop, signed in as that account:
Equinox --unlock asks the Desktop Portal; the desktop's own dialog takes a wrong password, then the
right one, and Equinox can edit disks; a second request dismissed with Esc is denied; sudo works in
the desktop terminal too."""
import functools, hashlib, http.server, importlib.util, secrets, subprocess, sys, threading, time
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
spec = importlib.util.spec_from_file_location('h', ROOT / 'tools/headless-smoke.py'); h = importlib.util.module_from_spec(spec); spec.loader.exec_module(h)
spec = importlib.util.spec_from_file_location('login', ROOT / 'tools/login-session-smoke.py'); login = importlib.util.module_from_spec(spec); spec.loader.exec_module(login)


def main():
    spec = importlib.util.spec_from_file_location('portal_input', ROOT / 'deploy/distributor/validation-input.py')
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
    subprocess.run([sys.executable, str(ROOT / 'tools/build-userland.py'), str(keep / 'equinox'), '--program', 'equinox', '--version', '0.4.0'], check=True)
    package('build-native', 'zenith-equinox', '0.4.0', keep / 'equinox', keep / 'bundle-equinox')
    package('seal', keep / 'bundle-equinox', keep / 'keys/private.pem', served / 'equinox.zpk')
    # The desktop types short script names; the account's home holds them.
    gui = {'p0': 'native run equinox --unlock | logger & sleep 2; true',
           'p1': 'native run equinox --unlock | logger & sleep 2; true',
           'p2': 'sudo -k; sudo id | logger; echo P_GUI_SUDO=$? | logger',
           'p3': 'sudo -n poweroff'}
    for name, text in gui.items(): (served / name).write_text(text + '\n')
    class Handler(http.server.SimpleHTTPRequestHandler):
        def log_message(self, *args): pass
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(Handler, directory=str(served)))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    url = 'http://10.0.2.2:' + str(server.server_port)
    net = ['-netdev', 'user,id=net0', '-device', 'e1000e,netdev=net0']
    monitor = keep / 'monitor.sock'
    secret = 'P!' + secrets.token_hex(8)
    m = None; checks = 0

    def check(ok, label):
        nonlocal checks
        if not ok:
            if m is not None and monitor.exists():
                try: monitor_command(monitor, 'screendump ' + str(keep / 'failure.ppm'))
                except (OSError, RuntimeError): pass
            raise AssertionError(label + '; log tail: ' + (m.log()[-1500:] if m else ''))
        checks += 1; print('PASS ' + label, flush=True)

    def command(text, marker, timeout=300, label=None):
        off = len(m.log()); m.send(text + '\r')
        check(m.wait(marker, off, timeout), label or marker)
        return m.log()[off:]

    def status(text, label, code=0, timeout=300):
        return command(text + '; echo ' + label + '=$? | logger', 'user: ' + label + '=' + str(code), timeout)

    def typed(text, marker, timeout=120, label=None):
        off = len(m.log()); login.monitor_type(monitor, text)
        check(m.wait(marker, off, timeout), label or marker)
        return m.log()[off:]

    try:
        m = h.Machine(keep / 'live', network=net); assert m.wait('headless ready', timeout=180)
        assert m.wait('network: online address=10.0.2.15', timeout=90)
        status('session set-default headless', 'P_SESSION'); status('install sata0 --yes', 'P_INSTALL', timeout=900)
        command('poweroff', 'all filesystems unmounted'); m.process.wait(timeout=30); disk = m.disk; m.stop(); m = None

        m = h.Machine(keep / 'setup', disk, network=net); assert m.wait('headless ready', timeout=180)
        check('accounts: created the wheel administrators group' in m.log(), 'wheel created at the first startup of this kernel')
        output = status('groups user | logger', 'P_GROUPS')
        check('wheel' in output.split('user: P_GROUPS')[0], "the installer's account is an administrator")
        for user in ('root', 'user'):
            command('passwd ' + user, 'New password: ')
            command(secret, 'Repeat password: '); command(secret, 'password updated for ' + user)
        status('fetch ' + url + '/key.pem /root/key.pem', 'P_KEY')
        status('pkg trust /root/key.pem ' + hashlib.sha256(key).hexdigest(), 'P_TRUST')
        status('fetch ' + url + '/equinox.zpk /root/equinox.zpk', 'P_FETCH')
        status('pkg install /root/equinox.zpk', 'P_PACKAGE')
        for name in gui:
            status('fetch ' + url + '/' + name + ' /home/user/' + name, 'P_SCRIPT_' + name)
            status('chown 1000:1000 /home/user/' + name, 'P_OWN_' + name)

        # sudo in the serial terminal, as the installer's account.
        command('su user', 'authenticated as user')
        output = command('sudo id | logger', '[sudo] password for user: ', label='sudo asks for the account\'s own password')
        output = command(secret, 'user: uid=0(root)', label='sudo runs the command as root')
        output = status('id | logger', 'P_SELF')
        check('uid=1000(user)' in output, 'the terminal keeps its own identity')
        output = status('sudo -n id | logger', 'P_TICKET')
        check('uid=0(root)' in output and 'password for' not in output, 'the password is remembered for this terminal')
        output = status('native run equinox --probe | logger', 'P_PLAIN')
        check('EQUINOX_DISKS count=' in output and ' root=no' in output, 'a native program started by the account is not root')
        output = status('sudo -n native run equinox --probe | logger', 'P_NATIVE')
        check('EQUINOX_DISKS count=' in output and ' root=yes' in output, 'a native program started through sudo is root')
        output = status('sudo -k; sudo -n id', 'P_FORGOTTEN', code=1)
        check('a password is required' in output, 'sudo -k forgets the password')
        command('sudo id', '[sudo] password for user: ')
        command('wrong-one', 'Sorry, try again.'); command('wrong-two', 'Sorry, try again.')
        command('wrong-three', '3 incorrect password attempts', label='three wrong passwords are refused')
        command('sudo useradd bob | logger', '[sudo] password for user: ')
        command(secret, 'user: created bob')
        output = status('sudo -n -u bob sudo -n id', 'P_NOT_ADMIN', code=1)
        check('bob is not an administrator' in output, 'an account outside wheel cannot use sudo')
        output = status('sudo -n gpasswd -a bob wheel | logger', 'P_GPASSWD')
        check('added bob to wheel' in output, 'gpasswd adds an account to wheel')
        output = status('sudo -n -u bob sudo -n id', 'P_BOB_ADMIN', code=1)
        check('a password is required' in output, 'the new administrator needs their own password (no shared ticket)')
        status('sudo -n gpasswd -d bob wheel', 'P_GPASSWD_D')
        log = m.log()
        check('sudo: user : USER=root ; PWD=' in log and 'COMMAND=id' in log, 'sudo logs who ran what')
        check('sudo: user : 3 incorrect password attempts' in log and 'sudo: bob : not in wheel' in log, 'refusals are logged')
        command('sudo -n poweroff', 'all filesystems unmounted'); m.process.wait(timeout=30); disk = m.disk; m.stop(); m = None

        # The desktop, signed in as the installer's account.
        m = h.Machine(keep / 'desktop', disk, 'session=de', network=net + ['-device', 'virtio-gpu-pci', '-device', 'virtio-keyboard-pci'], monitor=monitor)
        check(m.wait('login ready', timeout=180), 'greeter')
        typed('user\t' + secret + '\n', 'desktop ready (installed)', 180, 'the account signs in to the desktop')
        time.sleep(3); monitor_command(monitor, 'sendkey ctrl-alt-t 30'); time.sleep(3)
        typed('sh p0\n', 'portal: dialog shown: org.zenith.portal.Authorization.Authorize for equinox', 120, 'Equinox asks the Desktop Portal; the desktop shows its dialog')
        time.sleep(1); monitor_command(monitor, 'screendump ' + str(keep / 'portal.ppm'))
        typed('not-it\n', 'authorization password incorrect (1)', 60, 'a wrong password keeps the dialog open')
        output = typed(secret + '\n', 'user: EQUINOX_EDIT', 60)
        check('user: EQUINOX_UNLOCK approved authenticated as user' in output, 'the right password approves the request')
        check('user: EQUINOX_EDIT yes' in output, 'Equinox can now edit disks')
        time.sleep(1); monitor_command(monitor, 'screendump ' + str(keep / 'unlocked.ppm'))
        off = len(m.log()); monitor_command(monitor, 'sendkey esc 30')
        check(m.wait('native surface retired', off, 60), 'Equinox closes')
        time.sleep(2)
        typed('sh p1\n', 'portal: dialog shown: org.zenith.portal.Authorization.Authorize for equinox', 120, 'a new Equinox has to ask again')
        time.sleep(1); off = len(m.log()); monitor_command(monitor, 'sendkey esc 30')
        check(m.wait('user: EQUINOX_UNLOCK denied dismissed', off, 60), 'Esc dismisses the dialog and denies the request')
        check(m.wait('user: EQUINOX_EDIT no', off, 60), 'a denied Equinox stays read-only')
        off = len(m.log()); monitor_command(monitor, 'sendkey esc 30')
        check(m.wait('native surface retired', off, 60), 'Equinox closes again')
        time.sleep(2)
        login.monitor_type(monitor, 'sh p2\n'); time.sleep(3)
        typed(secret + '\n', 'user: P_GUI_SUDO=0', 120, 'sudo asks and runs in the desktop terminal')
        check('user: uid=0(root)' in m.log()[off:], 'sudo in the desktop terminal runs as root')
        typed('sh p3\n', 'all filesystems unmounted', 120, 'the desktop powers off through sudo')
        m.process.wait(timeout=30)
        print(f'PASS sudo and the Desktop Portal: {checks} checks', flush=True)
    finally:
        if m is not None: m.stop()
        server.shutdown(); server.server_close()


if __name__ == '__main__': main()
