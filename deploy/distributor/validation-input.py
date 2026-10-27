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
