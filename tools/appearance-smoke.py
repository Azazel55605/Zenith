#!/usr/bin/env python3
"""Appearance, snapping and notifications (U14) on a disposable installed disk with passwords.

Setup: the boot style is saved and written into the boot menu (bootstyle=orbit on both installed entries).
Desktop: a wrong password before signing in is reported by a Security notification after sign-in. Window snapping:
a title dragged to the left edge snaps to the left half, to the top edge maximizes, to the top-right corner takes
that quarter. Notifications from the shell: a toast appears and a click dismisses it; a toast's button runs its
action (opens a Terminal); a sender switched off posts nothing; a long command finishing while its Terminal is not
in front posts "Command finished". The bell opens the history panel; Do Not Disturb switches on from it; Clear all
empties it. The desktop part runs with the Soft widget style, Outline buttons, Ribbon decorations and the Bold icon set."""
import importlib.util, re, secrets, sys, time
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
spec = importlib.util.spec_from_file_location('h', ROOT / 'tools/headless-smoke.py'); h = importlib.util.module_from_spec(spec); spec.loader.exec_module(h)
spec = importlib.util.spec_from_file_location('login', ROOT / 'tools/login-session-smoke.py'); login = importlib.util.module_from_spec(spec); spec.loader.exec_module(login)


def main():
    spec = importlib.util.spec_from_file_location('appearance_input', ROOT / 'deploy/distributor/validation-input.py')
    profile = importlib.util.module_from_spec(spec); spec.loader.exec_module(profile); profile.install(h.Machine)
    import recovery_console
    raw = recovery_console.monitor_command   # unobserved: drags must not wait for a pointer they hide at an edge
    profile.install_monitor(recovery_console)
    monitor_command = recovery_console.monitor_command
    keep = Path(sys.argv[1]).resolve(); keep.mkdir(parents=True, exist_ok=False)
    net = ['-netdev', 'user,id=net0', '-device', 'e1000e,netdev=net0']
    monitor = keep / 'monitor.sock'
    secret = 'A!' + secrets.token_hex(8)
    m = None; checks = 0

    def check(ok, label):
        nonlocal checks
        if not ok:
            if m is not None and monitor.exists():
                try: raw(monitor, 'screendump ' + str(keep / 'failure.ppm'))
                except (OSError, RuntimeError): pass
            raise AssertionError(label + '; log tail: ' + (m.log()[-2500:] if m else ''))
        checks += 1; print('PASS ' + label, flush=True)

    def command(text, marker, timeout=300, label=None):
        off = len(m.log()); m.send(text + '\r')
        check(m.wait(marker, off, timeout), label or marker)
        return m.log()[off:]

    def status(text, label, code=0, timeout=300):
        return command(text + '; echo ' + label + '=$? | logger', 'user: ' + label + '=' + str(code), timeout)

    def typed(text, marker, label, timeout=60):
        off = len(m.log()); login.monitor_type(monitor, text)
        check(m.wait(marker, off, timeout), label)
        return off

    def last(pattern, since=0):
        found = re.findall(pattern, m.log()[since:])
        return found[-1] if found else None

    observer = profile.RenderedMouse(monitor, raw)
    def goto(x, y):
        (cx, cy), _ = observer.position(); monitor_command(monitor, f'mouse_move {x - cx} {y - cy}'); time.sleep(.3)
    def click(x, y):
        goto(x, y); monitor_command(monitor, 'mouse_button 1'); time.sleep(.1); monitor_command(monitor, 'mouse_button 0'); time.sleep(.4)
    def drag(dx, dy, steps=16):
        raw(monitor, 'mouse_button 1'); time.sleep(.2)
        for _ in range(steps): raw(monitor, f'mouse_move {dx // steps} {dy // steps}'); time.sleep(.05)
        time.sleep(.4); raw(monitor, 'mouse_button 0'); time.sleep(.5)
        # Away from the edges, where the pointer is partly hidden and cannot be observed.
        raw(monitor, f'mouse_move {(-dx // abs(dx)) * 160 if dx else 0} {(-dy // abs(dy)) * 160 if dy else 0}'); time.sleep(.4)
    def terminal(label):
        off = len(m.log()); monitor_command(monitor, 'sendkey ctrl-alt-t 30')
        check(m.wait('windows: opened at', off, 60), label)
        return tuple(map(int, re.findall(r'windows: opened at (-?\d+),(-?\d+) (\d+)x(\d+)', m.log()[off:])[-1]))

    try:
        m = h.Machine(keep / 'live', network=net); assert m.wait('headless ready', timeout=180)
        status('settings set windows compact; session set-default headless', 'L_SESSION'); status('install sata0 --yes', 'L_INSTALL', timeout=900)
        command('poweroff', 'all filesystems unmounted'); m.process.wait(timeout=30); disk = m.disk; m.stop(); m = None

        m = h.Machine(keep / 'setup', disk, 'session=headless', network=net); assert m.wait('headless ready', timeout=180)
        for user in ('root', 'user'):
            command('passwd ' + user, 'New password: '); command(secret, 'Repeat password: '); command(secret, 'password updated for ' + user)
        off = len(m.log()); status('settings set boot orbit', 'L_BOOT')
        check(m.wait('boot: boot style orbit written to the boot menu', off, 60), 'the boot style is written into the boot menu')
        out = status('grep bootstyle=orbit /boot/efi/limine.conf | logger', 'L_MENU')
        check(len(re.findall(r'user: +cmdline: root=PARTUUID=\S+ .*bootstyle=orbit', out)) >= 1, 'the installed entries boot in the chosen style')
        status('settings set boot phases', 'L_BOOT_BACK')
        # Everything on the desktop below runs in non-default styles.
        status('settings set widgets soft; settings set buttons outline; settings set decoration ribbon; settings set icons bold', 'L_STYLES')
        command('poweroff', 'all filesystems unmounted'); m.process.wait(timeout=30); disk = m.disk; m.stop(); m = None

        m = h.Machine(keep / 'desktop', disk, 'session=de', network=net + ['-device', 'virtio-gpu-pci', '-device', 'virtio-keyboard-pci', '-device', 'virtio-mouse-pci'], monitor=monitor)
        check(m.wait('login ready', 0, 180), 'the greeter is ready'); time.sleep(1)
        typed('user\twrong-password\n', 'session: authentication failed', 'a wrong password is refused', 120)
        typed(secret + '\n', 'desktop ready (installed)', 'the right one signs in', 180)
        check(m.wait('from Security: 1 failed sign-in since your last sign-in', 0, 30), 'the failed sign-in is reported after signing in')
        time.sleep(2)

        # Snapping.
        x, y, w, _ = terminal('a Terminal opens')
        goto(x + w // 2, y + 14); off = len(m.log()); drag(-3000, 0)
        check(m.wait('windows: snapped left', off, 15), 'the left edge snaps to the left half')
        x, y, w, _ = terminal('a second Terminal opens')
        goto(x + w // 2, y + 14); off = len(m.log()); drag(0, -3000)
        check(m.wait('windows: snapped top', off, 15), 'the top edge maximizes')
        x, y, w, _ = terminal('a third Terminal opens')
        goto(x + w // 2, y + 14); off = len(m.log()); drag(5000, -3000, 20)
        check(m.wait('windows: snapped top-right', off, 15), 'the top-right corner takes that quarter')

        # Notifications from the shell in the focused Terminal.
        off = typed('notify --icon package Gate_toast A toast from the gate | logger\n', 'notify: toast', 'a toast appears')
        tx, ty, tw, th = map(int, last(r'toast \d+ shown at (\d+),(\d+) (\d+)x(\d+)', off))
        off = len(m.log()); click(tx + tw // 3, ty + th // 2)
        check(m.wait('dismissed', off, 15), 'a click dismisses it')
        off = typed('notify --action Open_terminal Terminal Gate_button Press_the_button | logger\n', 'buttons=1', 'a toast with a button appears')
        tx, ty, tw, th = map(int, last(r'toast \d+ shown at (\d+),(\d+) (\d+)x(\d+) buttons=1', off))
        off = len(m.log()); click(tx + tw - 14 - 70, ty + th - 14 - 14)
        check(m.wait('notify: action 0 of', off, 15), 'its button runs')
        check(m.wait('windows: opened at', off, 30), 'and opens a Terminal')
        time.sleep(1)
        off = typed('notify mute Shell on | logger\n', 'user: muted Shell', 'the shell can be switched off')
        typed('notify Quiet | logger\n', 'notify: muted Shell', 'and then posts nothing')
        typed('notify mute Shell off | logger\n', 'user: unmuted Shell', 'and switched on again')

        # A long command whose Terminal is not in front.
        off = len(m.log()); login.monitor_type(monitor, 'sleep 11\n'); time.sleep(1)
        terminal('another Terminal takes the front')
        check(m.wait('from Terminal: Command finished', off, 60), 'a long command finishing behind posts Command finished')

        # The bell and the history panel.
        bx, by = map(int, last(r'notifications=(\d+),(\d+)'))
        off = len(m.log()); click(bx, by)
        check(m.wait('notifications center opened', off, 15), 'the bell opens the history')
        time.sleep(1)
        px, py, pw, ph = map(int, last(r'panel notifications at (\d+),(\d+) (\d+)x(\d+)', off))
        off = len(m.log()); click(px + pw // 2, py + 14 + 28 + 17)
        check(m.wait('do not disturb on', off, 15), 'Do Not Disturb switches on from the panel')
        time.sleep(.5)
        px, py, pw, ph = map(int, last(r'panel notifications at (\d+),(\d+) (\d+)x(\d+)'))
        off = len(m.log()); click(px + pw - 14 - 40, py + 14 + 9)
        check(m.wait('notifications cleared', off, 15), 'Clear all empties it')
        time.sleep(.8)   # the empty panel is shorter: its top moved down
        px, py, pw, ph = map(int, last(r'panel notifications at (\d+),(\d+) (\d+)x(\d+)'))
        off = len(m.log()); click(px + pw // 2, py + 14 + 28 + 17)
        check(m.wait('do not disturb off', off, 15), 'and Do Not Disturb switches off again')
        click(400, 300)   # outside: the panel closes

        terminal('the desktop still opens windows')
        m.stop(); m = None
        print(f'PASS appearance: {checks} checks', flush=True)
    finally:
        if m is not None: m.stop()


if __name__ == '__main__': main()
