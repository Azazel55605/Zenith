"""Explicit synthetic mouse transport for the protected graphical acceptance gate.

This changes the disposable QEMU test profile, never the tagged kernel/harness.
Legacy PS/2 hardware coverage remains separate.
"""
from pathlib import Path
import re
import socket


def monitor_text(path, command):
    def response(sock):
        data = b''
        while b'(qemu) ' not in data:
            chunk = sock.recv(4096)
            if not chunk: raise RuntimeError('QEMU monitor closed during mouse selection')
            data += chunk
            if len(data) > 65536: raise RuntimeError('QEMU mouse monitor response too large')
        if b'Error:' in data or b'unknown command:' in data:
            raise RuntimeError('QEMU rejected mouse monitor command')
        return data.decode(errors='replace')
    with socket.socket(socket.AF_UNIX) as sock:
        sock.settimeout(10); sock.connect(str(path)); response(sock)
        sock.sendall((command+'\n').encode()); return response(sock)


def mouse_index(text):
    mice = re.findall(r'(?m)^\s*(\*)?\s*Mouse #(\d+): QEMU Virtio Mouse\s*$', text)
    if len(mice) != 1: raise RuntimeError('expected exactly one QEMU Virtio Mouse')
    return int(mice[0][1]), bool(mice[0][0])


def select_mouse(monitor):
    index, _ = mouse_index(monitor_text(monitor, 'info mice'))
    monitor_text(monitor, 'mouse_set '+str(index))
    selected, active = mouse_index(monitor_text(monitor, 'info mice'))
    if selected != index or not active: raise RuntimeError('QEMU Virtio Mouse was not selected')
    print('Validation graphical mouse: virtio, explicitly selected index='+str(index), flush=True)
    return index


def install(machine):
    if getattr(machine, '_zenith_graphics_input_profile', False): return
    original = machine.__init__
    def initialize(self, *args, **kwargs):
        network = kwargs.get('network') or []
        graphical = 'virtio-gpu-pci' in network and kwargs.get('monitor') is not None
        if graphical:
            network = list(network)
            if not any(str(value).split(',')[0] == 'virtio-mouse-pci' for value in network):
                network += ['-device', 'virtio-mouse-pci']
            kwargs['network'] = network
        original(self, *args, **kwargs)
        if graphical:
            try: select_mouse(Path(kwargs['monitor']))
            except BaseException:
                self.stop(); raise
    machine.__init__ = initialize
    machine._zenith_graphics_input_profile = True


# Read-only cursor observation: a new rendered position proves the desktop has
# sampled input after that movement. This is stronger than waiting 150 ms while
# press/release edges can collapse between two desktop polls on a slow host.
CURSOR = (
    '#           ', '##          ', '#.#         ', '#..#        ',
    '#...#       ', '#....#      ', '#.....#     ', '#......#    ',
    '#.......#   ', '#........#  ', '#.........# ', '#......#####',
    '#...#..#    ', '#..##..#    ', '#.#  #..#   ', '##   #..#   ',
    '#     #..#  ', '      #..#  ', '       ##   ',
)


class SnapshotIncomplete(RuntimeError):
    pass


def frame_pixels(ppm):
    parts = ppm.split(b'\n', 3)
    if len(parts) < 4: raise SnapshotIncomplete('QEMU snapshot header incomplete')
    if parts[0] != b'P6' or parts[2] != b'255':
        raise RuntimeError('unsupported QEMU snapshot')
    width, height = map(int, parts[1].split()); pixels = parts[3]
    if not (1 <= width <= 4096 and 1 <= height <= 2160):
        raise RuntimeError('invalid QEMU snapshot dimensions')
    if len(pixels) < width * height * 3: raise SnapshotIncomplete('QEMU snapshot pixels incomplete')
    if len(pixels) != width * height * 3: raise RuntimeError('invalid QEMU snapshot byte count')
    return width, height, pixels


def probe_visible(ppm):
    _, _, pixels = frame_pixels(ppm)
    color = bytes.fromhex('6b8cff'); offset = count = 0
    while True:
        offset = pixels.find(color, offset)
        if offset < 0: return False
        if offset % 3 == 0:
            count += 1
            if count >= 5000: return True
        offset += 1


def cursor_position(ppm):
    width, height, pixels = frame_pixels(ppm)
    pattern = [(x, y, bytes.fromhex('101320' if c == '#' else 'ffffff'))
               for y, row in enumerate(CURSOR) for x, c in enumerate(row) if c != ' ']
    found = []; offset = 0
    while True:
        # First white pixel in the arrow is at (1,2); avoid scanning every RGB
        # pixel in Python, and reject unaligned cross-pixel byte matches.
        offset = pixels.find(b'\xff\xff\xff', offset)
        if offset < 0: break
        i = offset; offset += 1
        if i % 3: continue
        x = (i // 3) % width - 1; y = (i // 3) // width - 2
        if x < 0 or y < 0 or x + 12 > width or y + 19 > height: continue
        if all(pixels[((y+dy)*width+x+dx)*3:((y+dy)*width+x+dx)*3+3] == color for dx,dy,color in pattern):
            found.append((x, y))
    if len(found) != 1: raise RuntimeError('expected one visible graphical cursor, found '+str(len(found)))
    return found[0], (width, height)


DIAGNOSTIC_ELF = None


class RenderedMouse:
    def __init__(self, monitor, command):
        self.monitor = Path(monitor); self.raw = command
        self.snapshot = self.monitor.parent / 'input-observation.ppm'
    def capture(self, path):
        import time
        path = Path(path);path.unlink(missing_ok=True)
        self.raw(self.monitor, 'screendump '+str(path))
        deadline = time.monotonic() + 10
        while True:
            try:
                data = path.read_bytes();frame_pixels(data);return data
            except (FileNotFoundError, SnapshotIncomplete):
                if time.monotonic() >= deadline: raise RuntimeError('QEMU snapshot did not complete: '+str(path))
                time.sleep(.05)
    def position(self):
        return cursor_position(self.capture(self.snapshot))
    def failure(self, target, actual):
        # Read-only evidence before the harness finally block stops QEMU. Never
        # send keyboard commands to test responsiveness or replay application input.
        import json, importlib.util
        from types import SimpleNamespace
        report = dict(target=target, observed=actual, correction_policy="disabled",
                      screenshot=str(self.snapshot), observation_only=True)
        try:
            (self.monitor.parent/'input-failure.json').write_text(json.dumps(report,indent=2)+'\n')
            if DIAGNOSTIC_ELF is not None:
                spec=importlib.util.spec_from_file_location('input_diagnostics',Path(__file__).with_name('validation-diagnostics.py'))
                diagnostics=importlib.util.module_from_spec(spec);spec.loader.exec_module(diagnostics)
                diagnostics.capture(SimpleNamespace(directory=self.monitor.parent),'MERIDIAN_INPUT',DIAGNOSTIC_ELF)
        except Exception as error:
            print('Input failure diagnostic unavailable: '+type(error).__name__,flush=True)
    def wait_position(self, target, correct_motion=False):
        import time
        # Kept as a compatibility parameter for old callers. Screenshot feedback
        # cannot safely correct relative motion: a stale frame accumulates deltas.
        deadline = time.monotonic()+10
        while True:
            actual, _ = self.position()
            if actual == target: return
            if time.monotonic() >= deadline:
                self.failure(target,actual)
                raise RuntimeError('rendered mouse did not reach '+str(target)+'; observed '+str(actual))
            time.sleep(.05)
    def command(self, text):
        fields = text.split()
        if fields[:1] == ['screendump'] and len(fields) == 2 and Path(fields[1]).name in ('client.ppm', 'close.ppm', 'minimized.ppm'):
            import time
            deadline = time.monotonic() + 10
            while True:
                data = self.capture(Path(fields[1]))
                if probe_visible(data): return None
                if time.monotonic() >= deadline:
                    raise RuntimeError('native probe pixels not presented in '+fields[1])
                time.sleep(.05)
        if fields[:1] == ['mouse_move'] and len(fields) in (3, 4):
            (x, y), (width, height) = self.position()
            target = (max(0, min(width-1, x+int(fields[1]))), max(0, min(height-1, y+int(fields[2]))))
            result = self.raw(self.monitor, text)
            if target != (x, y): self.wait_position(target)
            return result
        if fields[:1] == ['mouse_button'] and len(fields) == 2:
            (x, y), (width, _) = self.position()
            result = self.raw(self.monitor, text)
            step = 1 if x < width-13 else -1
            # Exactly one button transition. Move after it, observe a rendered
            # frame, then restore position. Button stays held throughout a press;
            # the same sequence also acknowledges release. No relative feedback
            # corrections: capture the running guest when acknowledgement fails.
            self.raw(self.monitor, 'mouse_move '+str(step)+' 0'); self.wait_position((x+step, y))
            self.raw(self.monitor, 'mouse_move '+str(-step)+' 0'); self.wait_position((x, y))
            return result
        return self.raw(self.monitor, text)


def install_monitor(module):
    original = module.monitor_command
    if getattr(original, '_zenith_rendered_mouse', False): return
    transports = {}
    def command(path, text):
        fields = text.split()
        frame = fields[:1] == ['screendump'] and len(fields) == 2 and Path(fields[1]).name in ('client.ppm', 'close.ppm', 'minimized.ppm')
        if not frame and fields[:1] not in (['mouse_move'], ['mouse_button']):
            return original(path, text)
        key = str(path)
        if key not in transports: transports[key] = RenderedMouse(path, original)
        return transports[key].command(text)
    command._zenith_rendered_mouse = True
    module.monitor_command = command
