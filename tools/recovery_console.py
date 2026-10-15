"""Recovery acceptance through the real selected console, without bypassing login."""
import socket
import time
from pathlib import Path


def monitor_command(path, text):
    # Read a full HMP prompt: connecting/issuing another key must not race the
    # previous command. Errors fail rather than silently dropping menu input.
    def response(sock):
        data = b''
        while b'(qemu) ' not in data:
            chunk = sock.recv(65536)
            if not chunk: raise RuntimeError('QEMU monitor closed before command completed')
            data += chunk
        if b'Error:' in data or b'unknown command:' in data:
            raise RuntimeError('QEMU monitor command failed')
    with socket.socket(socket.AF_UNIX) as sock:
        sock.settimeout(10); sock.connect(str(path)); response(sock)
        sock.sendall((text+'\n').encode()); response(sock)


def keyboard_type(machine, monitor, text):
    for char in text:
        key = machine.efi.smoke.KEYS.get(char)
        if key is None:
            key = ('shift-'+machine.efi.smoke.SHIFTED[char] if char in machine.efi.smoke.SHIFTED
                   else 'shift-'+char.lower() if char.isupper() else char)
        monitor_command(monitor, 'sendkey '+key+' 30')
        time.sleep(.05)


class RecoveryConsole:
    def __init__(self, machine, monitor, headless):
        self.machine, self.monitor, self.headless = machine, monitor, headless
        self.graphical = False
        self.ready = False
        self.state = 'waiting for startup'

    def wait_startup(self, timeout=60):
        deadline = time.monotonic()+timeout
        while time.monotonic() < deadline:
            raw = self.machine.log()
            raw += '\n'+self.headless.efi.smoke.COSMOS_NOISE.sub('', raw)
            # The greeter is a boot result, never an authenticated console.
            if 'session: login ready;' in raw:
                self.graphical = True; self.state = 'graphical login'; return True
            if 'headless ready' in raw:
                self.state = 'serial console'; return True
            if 'ZENITH_BROKEN_KERNEL_ENTRY' in raw:
                self.state = 'broken kernel selected'; return False
            if 'FATAL:' in raw or 'panic:' in raw:
                self.state = 'guest startup failure'; return False
            if self.machine.process.poll() is not None:
                self.state = 'QEMU exited'; return False
            time.sleep(.05)
        self.state = 'startup deadline reached'
        return False

    def login(self, user, password, timeout=180):
        if self.state not in ('graphical login', 'serial console'):
            raise RuntimeError('recovery login requires a recognized startup state')
        if self.graphical:
            # Saved Headless is asserted from the greeter's own state. Do not
            # toggle blindly or mistake desktop startup for a text console.
            if 'login ready; selection=headless' not in self.machine.log():
                raise RuntimeError('recovery greeter did not select saved Headless mode')
            keyboard_type(self.headless, self.monitor, user+'\t')
            offset = len(self.machine.log())
            keyboard_type(self.headless, self.monitor, password+'\n')
            self.ready = self.machine.wait('headless ready (local text console)', offset, timeout)
        else:
            offset = len(self.machine.log()); self.machine.send('login '+user+'\r')
            if not self.machine.wait('Password: ', offset, timeout): return False
            offset = len(self.machine.log()); self.machine.send(password+'\r')
            self.ready = self.machine.wait('authenticated as '+user, offset, timeout)
        return self.ready

    def send(self, text):
        if not self.ready: raise RuntimeError('recovery command refused before authentication')
        if self.graphical: keyboard_type(self.headless, self.monitor, text.replace('\r','\n'))
        else: self.machine.send(text)
