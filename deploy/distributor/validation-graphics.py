"""Observe the Observatory-to-Polaris handoff in immutable graphics fixtures.

READY and the launch script's status precede host window retirement. Do not send
another public script until the focused shell prompt has actually rendered.
This policy neither rebuilds a tagged client nor repeats an input/operation.
"""
import json
from pathlib import Path
import time

READY = 'user: OBSERVATORY_READY'
PACKAGE = 'user: G_OBSERVATORY_PKG=0'
LAUNCHED = 'user: G_OBSERVATORY=0'
PROCESSES = 'user: G_OBSERVATORY_PROCESSES=0'
OPEN_PREFIX = 'graphics: native surface opened '
OPENED = OPEN_PREFIX+'640x480'
RETIRED = 'graphics: native surface retired'


def install_monitor(module, machines, policy, terminal):
    original = module.monitor_command
    if getattr(original, '_zenith_observatory_handoff', False): return
    acknowledged = {}

    def command(path, text):
        if text != 'sendkey esc 30': return original(path, text)
        matches = [m for m in machines if m.directory.parent/'monitor.sock' == Path(path)
                   and (not hasattr(m, 'process') or m.process.poll() is None)]
        active = []
        for machine in matches:
            raw = machine.log(); ready = raw.rfind(READY); package = raw.rfind(PACKAGE)
            boundary = max(package, raw.rfind(RETIRED, 0, max(0, ready)))
            launched = max(raw.rfind(LAUNCHED), raw.rfind(PROCESSES))
            if package >= 0 and ready > package and launched > boundary and ready > acknowledged.get(id(machine), -1):
                active.append((machine, raw, ready, boundary))
        if not active: return original(path, text)
        if len(active) != 1: raise RuntimeError('Ambiguous Observatory handoff VM; Escape was not sent')
        machine, raw, ready, boundary = active[0]
        observer = None; phase = 'focused-admission'
        try:
            if RETIRED in raw[ready:]: raise RuntimeError('Observatory retired before Escape')
            # In the tagged host, ClientHost logs this only after Windows.Open
            # has focused the new client. Unlike a new client FOCUSED print, this
            # acknowledgement is available in the unchanged 0.0.45 binary.
            if not machine.wait(OPENED, boundary, timeout=60):
                raise RuntimeError('Observatory host admission did not acknowledge focus')
            before = machine.log()
            if before[boundary:].count(OPEN_PREFIX) != 1 or RETIRED in before[ready:]:
                raise RuntimeError('Observatory window changed before Escape')
            admission = before.rfind(OPENED)
            result = original(path, text)  # One deliberate key; never replay it.
            phase = 'retirement'
            if not machine.wait(RETIRED, ready, timeout=60):
                raise RuntimeError('Observatory did not retire after Escape')
            phase = 'focused-public-prompt'
            observer = policy.RenderedMouse(path, policy.monitor_text)
            previous = observer.capture(observer.snapshot)
            deadline = time.monotonic()+15
            while True:
                time.sleep(.1)
                current = observer.capture(observer.snapshot)
                box = terminal.caret_box(previous, current)
                if box is not None and terminal.public_prompt(current, box): break
                previous = current
                if time.monotonic() >= deadline:
                    raise RuntimeError('Polaris public prompt did not acknowledge Observatory retirement')
            after = machine.log()
            if after.rfind(OPEN_PREFIX) != admission or after[ready:].count(RETIRED) != 1:
                raise RuntimeError('Native window changed during Observatory handoff')
            acknowledged[id(machine)] = ready
            print('PASS Observatory retired and focused public Polaris prompt rendered', flush=True)
            return result
        except BaseException:
            report = dict(phase=phase, serial=str(machine.serial), observation_only=True)
            target = Path(path).with_name('observatory-handoff.json')
            try: target.write_text(json.dumps(report, indent=2)+'\n')
            except OSError: pass
            print('Observatory handoff failure: '+json.dumps(report), flush=True)
            if observer is not None:
                try: observer.capture(Path(path).with_name('observatory-handoff-failure.ppm'))
                except (OSError, RuntimeError): pass
            raise
    command._zenith_observatory_handoff = True
    # Preserve the mouse policy's marker so a tagged helper's idempotent
    # install_monitor call cannot accidentally remove this outer observation.
    command._zenith_rendered_mouse = getattr(original, '_zenith_rendered_mouse', False)
    module.monitor_command = command
