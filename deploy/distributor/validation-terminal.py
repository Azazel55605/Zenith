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
        # Recovery Terminal backgrounds can themselves be blue-tinted. Only
        # foreground-bright pixels are evidence of the public cwd text.
        if b > 96 and b-g > 15 and b-r > 15: blue.append(x)
    return (len(green) >= 12 and len(blue) >= 3 and
            max(green)-min(green) >= 2*height and
            min(blue) > max(green) and min(blue) >= width-4*height)


def install(login, machines, policy, *, protocol='settings', replay_endpoint=None):
    if protocol not in ('settings','gallery'): raise ValueError('unknown Terminal handoff protocol')
    scripts = r'sh /root/[a-z]\n' if protocol == 'settings' else r'sh /root/ui[0-9]{3,30}-[up]\n'
    commands = ('passwd root\n','echo S_SECRET_CANCELLED | logger\n') if protocol == 'settings' else ()
    context = 'Settings' if protocol == 'settings' else 'UI gallery'
    original = login.monitor_type
    acknowledged = {}
    refreshed = set()
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
            if replay_endpoint is not None and protocol == 'settings' and text == 'sh /root/u\n' and id(machine) not in refreshed:
                endpoint = replay_endpoint()
                if not re.fullmatch(r'http://10\.0\.2\.2:[1-9][0-9]{0,4}',endpoint) or int(endpoint.rsplit(':',1)[1]) > 65535:
                    raise RuntimeError('Invalid prepared Settings fixture endpoint')
                # /root/io on the saved disk points at the previous run's temporary
                # server. Refresh it through the guest's normal fetch service, on
                # the disposable clone, before starting the unchanged UI workload.
                refreshed.add(id(machine)); start = len(machine.log())
                original(path,'fetch --replace '+endpoint+'/io /root/io; echo S_REPLAY_IO_REFRESH=$? | logger\n',*args,**kwargs)
                if not machine.wait('user: S_REPLAY_IO_REFRESH=0',start,timeout=60):
                    raise RuntimeError('Prepared Settings contention script refresh failed; not repeated')
                print('PASS prepared Settings contention endpoint refreshed through guest',flush=True)
                # Observe the new public prompt before submitting the original
                # launch once. No key, download, save or UI operation is retried.
                return monitor_type(path,text,*args,**kwargs)
        return original(path, text, *args, **kwargs)
    login.monitor_type = monitor_type


def install_settings_monitor(module, machines, policy):
    """Do not navigate a shared-settings page while its host is still opening it.

    The immutable fixture's first Shift+Tab follows MERIDIAN_FOCUSED, which is
    admission rather than presentation. Keep every key and result assertion; fence
    only that first navigation key on the completed opening and a stable frame.
    """
    original = module.monitor_command
    if getattr(original, '_zenith_settings_opening', False) is True: return
    pages = ('input.keyboard', 'locale.time', 'windows.taskbar', 'system.session')
    def command(path, text):
        if text != 'sendkey shift-tab 30': return original(path, text)
        matches = [m for m in machines if m.directory.parent/'monitor.sock' == Path(path)
                   and (not hasattr(m,'process') or m.process.poll() is None)]
        if len(matches) != 1: raise RuntimeError('Settings opening VM unavailable')
        machine = matches[0]; raw = machine.log()
        # Older fixtures do not expose the animation contract. Their input path
        # remains unchanged; this fence is for the observed animated handoff.
        motion = re.findall(r'user: MERIDIAN_MOTION percent=(\d+)',raw)
        if not motion or int(motion[-1]) == 0: return original(path,text)
        opened = raw.rfind('graphics: native surface opened ')
        page = raw.rfind('user: MERIDIAN_PAGE ')
        if opened < 0 or page < 0: raise RuntimeError('Shared Settings page admission missing')
        current = raw[page:].splitlines()[0].removeprefix('user: MERIDIAN_PAGE ')
        if current not in pages: return original(path,text)
        phase = 'opening'; observer = None; attempted = False
        try:
            if not machine.wait('motion: Opened ',opened,timeout=30):
                raise RuntimeError('Shared Settings opening did not complete')
            phase = 'rendered-frame'
            observer = policy.RenderedMouse(path,policy.monitor_text)
            previous = observer.capture(observer.snapshot); deadline = time.monotonic()+15
            while True:
                time.sleep(.1); frame = observer.capture(observer.snapshot)
                if frame == previous: break
                previous = frame
                if time.monotonic() >= deadline: raise RuntimeError('Shared Settings frame did not settle')
            after = machine.log()
            if after.rfind('graphics: native surface opened ') != opened or after.rfind('user: MERIDIAN_PAGE ') != page or 'native surface retired' in after[opened:]:
                raise RuntimeError('Shared Settings window changed before navigation')
            print('PASS shared Settings opening completed before keyboard navigation',flush=True)
            phase = 'navigation'; attempted = True
            return original(path,text)  # Once only. Never repeat a key or apply.
        except BaseException:
            report = dict(phase=phase,page=current,observation_only=True,key_attempted=attempted)
            try:
                Path(path).with_name('settings-opening.json').write_text(json.dumps(report,indent=2)+'\n')
                if observer: observer.capture(Path(path).with_name('settings-opening-failure.ppm'))
            except (OSError,RuntimeError,ValueError): pass
            raise
    print('Validation shared Settings input policy: opening-frame-v1',flush=True)
    command._zenith_settings_opening = True
    command._zenith_rendered_mouse = getattr(original,'_zenith_rendered_mouse',False)
    module.monitor_command = command


def process_command(text):
    """Exact public command lines; never classify payloads or credentials by prefix."""
    for label in ('N_GUI','N_GUI_EARLY'):
        commands = {
            'logger '+label+'_READY; sunbeam cat | logger; echo '+label+'_EOF=$? | logger\n':'canonical',
            'logger '+label+'_CANCEL_READY; sunbeam cat | sunbeam cat\n':'cancel',
            'echo '+label+'_CANCEL=$? | logger\n':'cancel-status',
            'sunbeam --tty-raw | logger; echo '+label+'_RAW=$? | logger\n':'raw',
            'sunbeam --tty-fault; echo '+label+'_RAW_FAULT=$? | logger\n':'fault',
            'sunbeam --tty-bounds; echo '+label+'_TTY_RESTORED=$? | logger\n':'bounds',
        }
        if text in commands: return label,commands[text]
    return None


def caret_progress(box, prior):
    height = prior[3]-prior[1]
    if box[3]-box[1] != height: return False
    if box[1] == prior[1]:
        # At the bottom of a scrolled Terminal a completed wrap keeps the row.
        return 0 < box[0]-prior[0] <= height or prior[0]-box[0] > 2*height
    return 0 < box[1]-prior[1] <= 2*height and box[0] < prior[0]


def caret_ink(first, second, box):
    """Calibrate the visible caret from its blink, independently of light/dark mode."""
    from PIL import Image
    import io
    for data in (first,second):
        img = Image.open(io.BytesIO(data)).convert('RGB')
        colors = img.crop(box).getcolors(maxcolors=1)
        if not colors or box[2]+1 >= img.width: continue
        color = colors[0][1]; background = img.getpixel((box[2]+1,(box[1]+box[3])//2))
        if sum(abs(a-b) for a,b in zip(color,background)) > 40: return color
    return None


def advanced_caret(data, prior, ink):
    """Observe a unique moved solid two-pixel caret without waiting for another blink."""
    from PIL import Image
    import io
    img = Image.open(io.BytesIO(data)).convert('RGB'); pixels = img.load()
    height = prior[3]-prior[1]; candidates = []
    for y in range(prior[1],min(img.height-height,prior[1]+2*height)+1):
        for x in range(1,img.width-2):
            box = (x,y,x+2,y+height)
            if not caret_progress(box,prior): continue
            if pixels[x,y] != ink or pixels[x+1,y] != ink: continue
            if pixels[x-1,y] == ink or pixels[x+2,y] == ink: continue
            if all(pixels[x,y+d] == ink and pixels[x+1,y+d] == ink for d in range(height)):
                candidates.append(box)
    return candidates[0] if len(candidates) == 1 else None


def install_process(login, machines, policy):
    """Render-acknowledged entry of the public GUI terminal-test commands.

    No input is repeated. Payloads, credentials, control bytes and the immediate
    EOF timing remain on the original transport, as do local-console commands.
    """
    original = login.monitor_type
    if getattr(original,'_zenith_process_fault_input',False) is True: return
    parked = set()
    def monitor_type(path, text, *args, **kwargs):
        selected = process_command(text)
        if selected is None: return original(path,text,*args,**kwargs)
        label,kind = selected
        matches = [m for m in machines if m.directory.parent/'gui-monitor.sock' == Path(path)
                   and (not hasattr(m,'process') or m.process.poll() is None)]
        if len(matches) != 1: raise RuntimeError('Process public input VM unavailable')
        observer = policy.RenderedMouse(path,policy.monitor_text)
        phase = 'public-prompt'; acknowledged = 0; ink = None
        def caret(prior=None):
            nonlocal ink
            previous = observer.capture(observer.snapshot); deadline = time.monotonic()+15
            while True:
                if prior is not None and ink is not None:
                    moved = advanced_caret(previous,prior,ink)
                    if moved is not None: return moved
                time.sleep(.05); current = observer.capture(observer.snapshot)
                box = caret_box(previous,current)
                if box is not None:
                    if prior is None and public_prompt(current,box):
                        ink = caret_ink(previous,current,box)
                        if ink is not None: return box
                    if prior is not None and caret_progress(box,prior): return box
                previous = current
                if time.monotonic() >= deadline: raise RuntimeError('Process public input '+phase+' was not rendered; input not repeated')
        try:
            position = caret()
            if id(matches[0]) not in parked:
                # Keyboard-only fixture: one planned move, no click or feedback
                # correction. The arrow must not obscure a later command caret.
                (_,y),_ = observer.position()
                if y > 32:
                    phase = 'pointer-parking'
                    # Only the top band matters. Legacy PS/2 may coalesce a
                    # large move; do not demand an irrelevant exact X coordinate.
                    policy.monitor_text(path,'mouse_move 0 '+str(-y))
                    deadline = time.monotonic()+10
                    while observer.position()[0][1] > 32:
                        if time.monotonic() >= deadline: raise RuntimeError('Pointer still obscures keyboard-test area; movement not repeated')
                        time.sleep(.05)
                    position = caret()
                parked.add(id(matches[0]))
            phase = 'character'
            for char in text[:-1]:
                original(path,char,*args,**kwargs)  # One key, never corrected or replayed.
                position = caret(position); acknowledged += 1
            observer.capture(Path(path).with_name('process-command-'+label+'-'+kind+'.ppm'))
            print('PASS rendered public process command before Enter: '+label+' '+kind,flush=True)
            phase = 'submit'
            return original(path,'\n',*args,**kwargs)
        except BaseException:
            report = dict(phase=phase,label=label,command=kind,acknowledged_characters=acknowledged,
                          input_repeated=False,observation_only=True)
            try:
                Path(path).with_name('process-input.json').write_text(json.dumps(report,indent=2)+'\n')
                observer.capture(Path(path).with_name('process-input-failure.ppm'))
            except (OSError,RuntimeError,ValueError): pass
            raise
    monitor_type._zenith_process_fault_input = True
    login.monitor_type = monitor_type
    print('Validation process input policy: public-command-caret-v2',flush=True)
