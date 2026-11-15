#!/usr/bin/env python3
"""Sunset and the lock screen on a disposable installed disk with passwords. After signing in at the greeter:
Meta+Shift+L opens Sunset with focus on Lock and nothing in the way; Esc closes it; Ctrl+Alt+Delete opens it too;
L locks the screen; a wrong password is refused and shortcuts do not reach the desktop behind it; the right one
unlocks. A running command is named as a blocker and log out is refused while it runs; once it ends, O logs out
to the greeter. After signing in again, S shuts the machine down cleanly."""
import importlib.util, secrets, sys, time
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
spec = importlib.util.spec_from_file_location('h', ROOT / 'tools/headless-smoke.py'); h = importlib.util.module_from_spec(spec); spec.loader.exec_module(h)
spec = importlib.util.spec_from_file_location('login', ROOT / 'tools/login-session-smoke.py'); login = importlib.util.module_from_spec(spec); spec.loader.exec_module(login)


def main():
    spec = importlib.util.spec_from_file_location('sunset_input', ROOT / 'deploy/distributor/validation-input.py')
    profile = importlib.util.module_from_spec(spec); spec.loader.exec_module(profile); profile.install(h.Machine)
    import recovery_console
    profile.install_monitor(recovery_console)
    monitor_command = recovery_console.monitor_command
    keep = Path(sys.argv[1]).resolve(); keep.mkdir(parents=True, exist_ok=False)
    net = ['-netdev', 'user,id=net0', '-device', 'e1000e,netdev=net0']
    monitor = keep / 'monitor.sock'
    secret = 'S!' + secrets.token_hex(8)
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

    def sign_in():
        check(m.wait('login ready', 0, 180), 'the greeter is ready')
        time.sleep(1)
        typed('user\t' + secret + '\n', 'desktop ready (installed)', 'user signs in', timeout=180)
        time.sleep(2)

    try:
        m = h.Machine(keep / 'live', network=net); assert m.wait('headless ready', timeout=180)
        status('session set-default headless', 'S_SESSION'); status('install sata0 --yes', 'S_INSTALL', timeout=900)
        command('poweroff', 'all filesystems unmounted'); m.process.wait(timeout=30); disk = m.disk; m.stop(); m = None

        m = h.Machine(keep / 'setup', disk, 'session=headless', network=net); assert m.wait('headless ready', timeout=180)
        for user in ('root', 'user'):
            command('passwd ' + user, 'New password: '); command(secret, 'Repeat password: '); command(secret, 'password updated for ' + user)
        # Transparency on, so the popup, the lock card and Prism are checked on the desktop material.
        status('settings set materials background-blur', 'S_MATERIALS')
        command('poweroff', 'all filesystems unmounted'); m.process.wait(timeout=30); disk = m.disk; m.stop(); m = None

        m = h.Machine(keep / 'desktop', disk, 'session=de', network=net + ['-device', 'virtio-gpu-pci', '-device', 'virtio-keyboard-pci', '-device', 'virtio-mouse-pci'], monitor=monitor)
        sign_in()

        press('meta_l-shift-l', 'sunset: opened blockers=0 focus=Lock', 'Meta+Shift+L opens Sunset on Lock with nothing in the way')
        check(m.wait('motion: SunsetOpened frames=', 0, 30), 'it fades and scales in')
        shot('sunset')
        press('esc', 'sunset: closed', 'Esc closes it')
        time.sleep(1)
        press('meta_l', 'prism: opened palette', 'Meta opens the Prism palette')
        shot('prism')
        monitor_command(monitor, 'sendkey esc 30')
        time.sleep(1)
        press('ctrl-alt-delete', 'sunset: opened blockers=0', 'Ctrl+Alt+Delete opens it too')
        off = len(m.log()); monitor_command(monitor, 'sendkey l 30')
        check(m.wait('sunset: chose Lock', off, 30) and m.wait('session: locked (user)', off, 30), 'L locks the screen')
        shot('locked')
        typed('wrong\n', 'session: unlock failed (1)', 'a wrong password is refused', timeout=120)
        off = len(m.log())
        for keys in ('ctrl-alt-l', 'ctrl-alt-t', 'meta_l'): monitor_command(monitor, 'sendkey ' + keys + ' 30'); time.sleep(1)
        time.sleep(2)
        later = m.log()[off:]
        check('session ended' not in later and 'prism: opened' not in later and 'logout' not in later, 'shortcuts do not reach the desktop behind the lock')
        typed(secret + '\n', 'session: unlocked', 'the right password unlocks', timeout=120)
        time.sleep(1); shot('unlocked')

        # A running command is named, and log out waits for it.
        monitor_command(monitor, 'sendkey ctrl-alt-t 30'); time.sleep(3)
        off = len(m.log()); login.monitor_type(monitor, 'sleep 30\n'); time.sleep(2)
        press('ctrl-alt-delete', 'sunset: opened blockers=1', 'a running command is named as a blocker')
        shot('blocked')
        press('o', 'sunset: refused LogOut', 'log out is refused while it runs')
        press('esc', 'sunset: closed', 'and Sunset closes')
        monitor_command(monitor, 'sendkey ctrl-c 30'); time.sleep(2)
        press('ctrl-alt-delete', 'sunset: opened blockers=0', 'once it ends nothing is in the way')
        off = len(m.log()); monitor_command(monitor, 'sendkey o 30')
        check(m.wait('sunset: chose LogOut', off, 30), 'O logs out')
        check(m.wait('login ready', off, 180), 'and the greeter returns')
        check('motion: SessionFade frames=' in m.log()[off:], 'with the logout fade')

        time.sleep(1); typed('user\t' + secret + '\n', 'desktop ready (installed)', 'user signs in again', timeout=180); time.sleep(2)
        press('ctrl-alt-delete', 'sunset: opened blockers=0', 'Sunset opens in the new session')
        off = len(m.log()); monitor_command(monitor, 'sendkey s 30')
        check(m.wait('sunset: shut down', off, 60), 'S shuts down')
        check(m.wait('all filesystems unmounted', off, 120), 'after the disks are safe')
        m.process.wait(timeout=30); m.stop(); m = None
        print(f'PASS Sunset: {checks} checks', flush=True)
    finally:
        if m is not None: m.stop()


if __name__ == '__main__': main()
