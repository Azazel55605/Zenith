"""Gate the immutable login fixture's Terminal shortcut on the rendered session.

Desktop-ready precedes its first input drain and welcome fade. Do not submit the
shortcut into that boundary; send it once after the fade and observe the public
shell prompt before returning to the fixture's unchanged command assertions.
"""
import json
from pathlib import Path
import time

READY = 'desktop ready (installed)'
FADE = 'motion: SessionFade frames='


def install_monitor(module, machines, policy, terminal):
    original = module.monitor_command
    if getattr(original, '_zenith_login_handoff', False) is True: return

    def command(path, text):
        if text != 'sendkey ctrl-alt-t 30': return original(path, text)
        phase = 'registry'; sent = False; machine = None; observer = None
        try:
            matches = [m for m in machines if m.directory.parent/'monitor.sock' == Path(path)
                       and (not hasattr(m, 'process') or m.process.poll() is None)]
            if len(matches) != 1: raise RuntimeError('Login Terminal VM unavailable; matches='+str(len(matches)))
            machine = matches[0]
            raw = machine.log(); ready = raw.rfind(READY)
            if ready < 0 or raw.rfind('login ready') > ready:
                raise RuntimeError('Terminal shortcut requires the authenticated desktop')
            phase = 'session-fade'
            if not machine.wait(FADE, ready, timeout=30):
                raise RuntimeError('Desktop fade did not finish before Terminal shortcut')
            if machine.log().rfind(READY) != ready or machine.log().rfind('login ready') > ready:
                raise RuntimeError('Session changed before Terminal shortcut')
            print('PASS desktop fade completed before Terminal shortcut', flush=True)
            result = original(path, text); sent = True  # Exactly one shortcut, never replayed.
            phase = 'public-terminal-prompt'
            observer = policy.RenderedMouse(path, policy.monitor_text)
            previous = observer.capture(observer.snapshot); deadline = time.monotonic()+15
            while True:
                time.sleep(.1); current = observer.capture(observer.snapshot)
                box = terminal.caret_box(previous, current)
                if box is not None and terminal.public_prompt(current, box): break
                previous = current
                if time.monotonic() >= deadline:
                    raise RuntimeError('Focused public Terminal prompt did not follow login shortcut')
            raw = machine.log()
            if raw.rfind(READY) != ready or raw.rfind('login ready') > ready:
                raise RuntimeError('Session changed during Terminal opening')
            print('PASS focused public Terminal prompt after login shortcut', flush=True)
            return result
        except BaseException:
            report = dict(policy='desktop-fade-public-terminal-v1', phase=phase,
                          shortcut_sent=sent, serial=str(machine.serial) if machine is not None else None,
                          observation_only=True)
            try:
                Path(path).with_name('login-terminal-handoff.json').write_text(json.dumps(report,indent=2)+'\n')
                if machine is not None:
                    observer = observer or policy.RenderedMouse(path, policy.monitor_text)
                    observer.capture(Path(path).with_name('login-terminal-failure.ppm'))
            except (OSError, RuntimeError, ValueError): pass
            print('Login Terminal handoff failure: '+json.dumps(report), flush=True)
            raise
    command._zenith_login_handoff = True
    module.monitor_command = command
    print('Validation login input policy: desktop-fade-public-terminal-v1', flush=True)
