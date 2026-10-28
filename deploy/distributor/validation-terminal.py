"""Observe the public Settings-to-Terminal handoff before injecting its next script.

Worker completion statuses precede native-window retirement. A fixed delay can
type into that retiring window on slower hosts. Never retry a command or inspect
hidden input: this hook applies only to public one-letter fixture script launches.
"""
import re
import time
from pathlib import Path


def caret_changed(before, after):
    from PIL import Image, ImageChops
    import io
    first = Image.open(io.BytesIO(before)).convert('RGB')
    second = Image.open(io.BytesIO(after)).convert('RGB')
    if first.size != second.size: return False
    box = ImageChops.difference(first, second).getbbox()
    if box is None: return False
    left, top, right, bottom = box
    if right-left != 2 or not 8 <= bottom-top <= 32: return False
    # A focused Terminal caret is a solid two-pixel rectangle. Require a real
    # blink, not a partially rendered character or a static sidebar indicator.
    return any(frame.crop(box).getcolors(maxcolors=1) is not None for frame in (first, second))


def install(login, machines, policy):
    original = login.monitor_type
    acknowledged = {}
    def monitor_type(path, text, *args, **kwargs):
        if re.fullmatch(r'sh /root/[a-z]\n', text):
            matches = [machine for machine in machines
                       if machine.directory.parent / 'monitor.sock' == Path(path)]
            if len(matches) != 1: raise RuntimeError('Settings Terminal handoff VM unavailable')
            machine = matches[0]
            raw = machine.log()
            offset = raw.rfind('graphics: native surface opened ')
            # The first launch, and consecutive shell-only commands after an
            # acknowledged exit, have no native focus handoff to observe.
            if offset < 0 or offset <= acknowledged.get(id(machine), -1):
                return original(path, text, *args, **kwargs)
            if not machine.wait('graphics: native surface retired', offset, timeout=15):
                raise RuntimeError('Settings native window did not retire before public script')
            observer = policy.RenderedMouse(path, policy.monitor_text)
            previous = observer.capture(observer.snapshot)
            deadline = time.monotonic()+15
            while True:
                time.sleep(.1)
                current = observer.capture(observer.snapshot)
                if caret_changed(previous, current): break
                previous = current
                if time.monotonic() >= deadline:
                    raise RuntimeError('Terminal caret did not acknowledge Settings handoff')
            if machine.log().rfind('graphics: native surface opened ') != offset:
                raise RuntimeError('Native window changed during Terminal handoff')
            acknowledged[id(machine)] = offset
            print('PASS rendered Terminal handoff before public fixture '+text.strip(), flush=True)
        return original(path, text, *args, **kwargs)
    login.monitor_type = monitor_type
