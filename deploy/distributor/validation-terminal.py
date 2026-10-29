"""Observe the public native-client-to-Terminal handoff before its next script.

Worker completion and cancellation precede GUI input cleanup. Observe a focused
public shell prompt before known public fixture commands. Never repeat a command,
submit credentials, or change the application's result assertions.
"""
import re
import json
import time
from pathlib import Path


def caret_box(before, after):
    from PIL import Image, ImageChops
    import io
    first = Image.open(io.BytesIO(before)).convert('RGB')
    second = Image.open(io.BytesIO(after)).convert('RGB')
    if first.size != second.size: return None
    box = ImageChops.difference(first, second).getbbox()
    if box is None: return None
    left, top, right, bottom = box
    if right-left != 2 or not 8 <= bottom-top <= 32: return None
    # A focused Terminal caret is a solid two-pixel rectangle. Require a real
    # blink, not a partially rendered character or a static sidebar indicator.
    return box if any(frame.crop(box).getcolors(maxcolors=1) is not None for frame in (first, second)) else None


def caret_changed(before, after):
    return caret_box(before, after) is not None


def public_prompt(ppm, box):
    """Recognize ANSI user/host then cwd immediately before the blinking caret.

    The fixed public fixture has a green user/host and blue cwd in both modes.
    Use color dominance to tolerate antialiased glyphs on a material background.
    The password prompt is neutral, and a previous row's prompt cannot qualify.
    This observes only the small rendered row, never terminal memory or input.
    """
    from PIL import Image
    import io
    image = Image.open(io.BytesIO(ppm)).convert('RGB')
    left, top, _, bottom = box
    height = bottom-top
    start = max(0,left-8*height)
    row = image.crop((start,top,left,bottom))
    width = row.width
    if not width: return False
    green = []; blue = []
    pixels = row.tobytes()
    for offset in range(0,len(pixels),3):
        r,g,b = pixels[offset:offset+3]; x = offset//3 % width
        if g-r > 20 and g-b > 10: green.append(x)
        if b-g > 15 and b-r > 15: blue.append(x)
    return (len(green) >= 12 and len(blue) >= 3 and
            max(green)-min(green) >= 2*height and
            min(blue) > max(green) and min(blue) >= width-4*height)


def install(login, machines, policy, *, protocol='settings'):
    if protocol not in ('settings','gallery'): raise ValueError('unknown Terminal handoff protocol')
    scripts = r'sh /root/[a-z]\n' if protocol == 'settings' else r'sh /root/ui[0-9]{3,30}-[up]\n'
    commands = ('passwd root\n','echo S_SECRET_CANCELLED | logger\n') if protocol == 'settings' else ()
    context = 'Settings' if protocol == 'settings' else 'UI gallery'
    original = login.monitor_type
    acknowledged = {}
    def monitor_type(path, text, *args, **kwargs):
        public = re.fullmatch(scripts, text) or text in commands
        if public:
            matches = [machine for machine in machines
                       if machine.directory.parent / 'monitor.sock' == Path(path)
                       and (not hasattr(machine,'process') or machine.process.poll() is None)]
            if len(matches) != 1:
                rows = []
                for candidate in machines:
                    process = getattr(candidate, 'process', None)
                    rows.append(dict(directory=str(candidate.directory),
                                     pid=getattr(process, 'pid', None),
                                     exit=process.poll() if process is not None else None,
                                     requested_monitor=getattr(candidate, '_validation_requested_monitor', None),
                                     expected_monitor=str(candidate.directory.parent/'monitor.sock')))
                helper=getattr(getattr(login,'h',None),'__file__',None)
                report = dict(monitor=str(path), registered=rows, matches=len(matches), login_headless_source=helper, observation_only=True)
                print(context+' Terminal VM registry: '+json.dumps(report), flush=True)
                try: Path(path).with_name('terminal-handoff.json').write_text(json.dumps(report,indent=2)+'\n')
                except OSError: pass
                raise RuntimeError(context+' Terminal handoff VM unavailable; registered='+str(len(rows))+'; matches='+str(len(matches)))
            machine = matches[0]
            raw = machine.log()
            offset = raw.rfind('graphics: native surface opened ')
            if offset > acknowledged.get(id(machine), -1) and not machine.wait('graphics: native surface retired', offset, timeout=15):
                raise RuntimeError(context+' native window did not retire before public script')
            observer = policy.RenderedMouse(path, policy.monitor_text)
            previous = observer.capture(observer.snapshot)
            deadline = time.monotonic()+15
            while True:
                time.sleep(.1)
                current = observer.capture(observer.snapshot)
                box = caret_box(previous,current)
                if box is not None and public_prompt(current,box): break
                previous = current
                if time.monotonic() >= deadline:
                    raise RuntimeError('Public Terminal prompt did not acknowledge '+context+' handoff')
            if machine.log().rfind('graphics: native surface opened ') != offset:
                raise RuntimeError('Native window changed during Terminal handoff')
            acknowledged[id(machine)] = offset
            print('PASS rendered public Terminal prompt before fixture command', flush=True)
        return original(path, text, *args, **kwargs)
    login.monitor_type = monitor_type
