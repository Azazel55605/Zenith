#!/usr/bin/env python3
"""Disposable COM1-only S1 guest tests: no VGA/GPU, keyboard or mouse device."""
import argparse
import importlib.util
import os
from pathlib import Path
import shutil
import socket
import struct
import subprocess
import threading
import time

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("efi", ROOT / "tools/test-installed-boot.py")
efi = importlib.util.module_from_spec(spec); spec.loader.exec_module(efi)

class Machine:
    def __init__(self, directory, disk=None, argument=None, network=None, extra_disks=(), monitor=None, vga="none", verify=True, cpus=1, iso=None):
        if not isinstance(cpus, int) or not 1 <= cpus <= 8: raise ValueError("test CPU count must be 1..8")
        directory.mkdir(parents=True, exist_ok=True)
        self.directory = directory
        self.disk = directory / "disk.img"
        self.serial = directory / "serial.log"
        if disk:
            shutil.copyfile(disk, self.disk)
            import boot_image
            if verify: boot_image.verify(self.disk)
            else: boot_image.partitions(self.disk)
            if argument is not None:
                boot_image.override(self.disk, argument, directory)
        else:
            with self.disk.open("wb") as file: file.truncate(256 * 1024 * 1024)
        args = [os.environ.get("QEMU", "qemu-system-x86_64"), "-M", "q35", "-m", "512M",
                "-accel", "kvm", "-accel", "tcg", "-cpu", "max", "-smp", str(cpus), "-display", "none", "-vga", vga, "-no-reboot",
                "-chardev", f"socket,id=console,path={directory / 'serial.sock'},server=on,wait=off", "-serial", "chardev:console",
                "-monitor", "unix:" + str(monitor) + ",server,nowait" if monitor else "none", "-drive", f"file={self.disk},format=raw,if=none,id=disk0", "-device", "ide-hd,drive=disk0,bus=ide.0,unit=0"]
        for index, image in enumerate(extra_disks):
            if index > 0: raise ValueError("only one secondary test disk supported")
            other = directory / "secondary.img"; shutil.copyfile(image, other)
            args += ["-drive", f"file={other},format=raw,if=none,id=secondary", "-device", "ide-hd,drive=secondary,bus=ide.1,unit=0"]
        if network is not None: args += ["-nic", "none"] + network
        if disk:
            code, variables = efi.firmware(); private = directory / "OVMF_VARS.fd"; shutil.copyfile(variables, private)
            args += ["-drive", f"if=pflash,format=raw,readonly=on,file={code}", "-drive", f"if=pflash,format=raw,file={private}"]
        else: args += ["-cdrom", str(iso or ROOT / "output-x64/Zenith.iso"), "-boot", "order=d"]
        self.process = subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=(directory / "qemu.log").open("wb"))
        self.socket = socket.socket(socket.AF_UNIX)
        for _ in range(200):
            try: self.socket.connect(str(directory / "serial.sock")); break
            except (FileNotFoundError, ConnectionRefusedError): time.sleep(0.05)
        else: raise RuntimeError("serial socket did not open")
        def collect():
            with self.serial.open("wb") as file:
                while True:
                    try: data = self.socket.recv(65536)
                    except OSError: break
                    if not data: break
                    file.write(data); file.flush()
        self.reader = threading.Thread(target=collect, daemon=True); self.reader.start()
    def log(self): return self.serial.read_text(errors="replace") if self.serial.exists() else ""
    def wait(self, marker, offset=0, timeout=180):
        deadline = time.monotonic() + timeout
        position = 0; pending = ''; first = True
        while time.monotonic() < deadline:
            # Large downloads produce megabytes of upstream debug output. Scan new
            # bytes instead of decoding and stripping the entire log every 50 ms.
            if self.serial.exists():
                with self.serial.open('rb') as stream:
                    stream.seek(position); data=stream.read(); position=stream.tell()
                raw=data.decode(errors='replace')
                if first: raw=raw[offset:]; first=False
                pending+=raw
                if marker in pending or marker in efi.smoke.COSMOS_NOISE.sub('', pending): return True
                pending=pending[-65536:]
            if self.process.poll() is not None: break
            time.sleep(0.05)
        return False
    def send(self, text):
        # 16-byte UART FIFO and timer polling: pace bytes, including during GC.
        for value in text.encode("ascii"):
            self.socket.sendall(bytes([value])); time.sleep(0.01)
    def stop(self):
        if self.process.poll() is None:
            self.process.terminate()
            try: self.process.wait(timeout=10)
            except subprocess.TimeoutExpired: self.process.kill(); self.process.wait()
        self.socket.close(); self.reader.join(timeout=2)

# One semantic policy for every direct harness importing this helper.
auth_spec = importlib.util.spec_from_file_location('guest_auth', Path(__file__).with_name('guest_auth.py'))
auth = importlib.util.module_from_spec(auth_spec); auth_spec.loader.exec_module(auth)
auth.install(Machine)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("keep", type=Path, nargs="?", default=Path("/tmp/zenith-headless"))
    args = parser.parse_args(); args.keep.mkdir(parents=True, exist_ok=True)
    checks = failures = 0
    def assert_(passed, label):
        nonlocal checks, failures
        checks += 1; failures += not passed; print(("ok   " if passed else "FAIL ") + label, flush=True)
        if not passed: raise RuntimeError(label)
    def step(machine, command, marker):
        offset = len(machine.log()); machine.send(command + "\r")
        assert_(machine.wait(marker, offset), marker)
    def boot(label, disk=None, argument=None):
        machine = Machine(args.keep / label, disk, argument)
        if not machine.wait("headless ready", timeout=600):
            machine.stop(); raise RuntimeError("headless boot failed: " + label)
        assert_(True, label + " COM1 ready without GPU")
        assert_("desktop ready" not in machine.log() and "Initializing KernelConsole" not in machine.log()
                and "fonts:" not in machine.log() and "canvas:" not in machine.log(), label + " no graphical session/console")
        assert_(machine.wait("[console] "), label + " prompt")
        return machine
    # Random disposable credentials are never included in shell command lines/logs.
    secrets = ["S1!" + os.urandom(8).hex(), "S1!" + os.urandom(8).hex()]
    def password(machine, command, value, marker):
        offset = len(machine.log()); machine.send(command + "\r")
        assert_(machine.wait("New password: ", offset), command + " hidden prompt")
        offset = len(machine.log()); machine.send(value + "\r")
        assert_(machine.wait("Repeat password: ", offset), "hidden confirmation")
        offset = len(machine.log()); machine.send(value + "\r")
        assert_(machine.wait(marker, offset), marker)
    machine = boot("live")
    try:
        step(machine, "session status | logger", "active=headless (capability fallback)")
        step(machine, "echo live-console | logger", "user: live-console")
        machine.send("echo discarded\x03");
        step(machine, "echo fresh-after-idle-cancel | logger", "user: fresh-after-idle-cancel")
        step(machine, "man headless | head -1 | logger", "user: SERIAL HEADLESS SESSION")
        step(machine, "echo headless-file > /home/user/headless-file; cat /home/user/headless-file | logger", "user: headless-file")
        step(machine, "printf 'for i in one two; do echo script-$i; done\\n' > /tmp/headless.sh; sh /tmp/headless.sh | logger", "user: script-two")
        step(machine, "if true; then\necho multiline | logger\nfi", "user: multiline")
        # Keep the prompt absent from input echo; wait for the actual read callback.
        offset = len(machine.log()); machine.send("read -p 'Read in''put: ' VALUE; echo got=$VALUE | logger\r")
        assert_(machine.wait("Read input: ", offset), "read prompt")
        step(machine, "serial-answer", "user: got=serial-answer")
        machine.send("sleep 30\r"); time.sleep(1); machine.send("\x03")
        step(machine, "echo cancelled=$? | logger", "user: cancelled=130")
        step(machine, "open files; echo gui=$? | logger", "user: gui=1")
        step(machine, "pkg list | grep zenith-base | logger", "user: zenith-base")
        step(machine, "session set-default wm; echo wm=$? | logger", "user: wm=1")
        step(machine, "settings set timezone Europe/Berlin; session set-default headless; install sata0 --yes", "Zenith installed and UEFI bootable.")
        step(machine, "sync; poweroff", "all filesystems unmounted")
        assert_(machine.process.wait(timeout=30) == 0, "live clean poweroff")
    finally: machine.stop()
    installed = machine.disk
    machine = boot("installed", installed)
    try:
        step(machine, "session status | logger", "active=headless (saved default)")
        step(machine, "cat /proc/mounts | grep ' / ext2 ' | logger", " / ext2 rw")
        step(machine, "cat /home/user/headless-file | logger", "user: headless-file")
        step(machine, "settings show | logger", "timezone=Europe/Berlin")
        password(machine, "passwd root", secrets[0], "password updated for root")
        password(machine, "passwd user", secrets[1], "password updated for user")
        step(machine, "PRIVATE=prior-session; echo env-seeded | logger", "user: env-seeded")
        offset = len(machine.log()); machine.send("login user\r"); assert_(machine.wait("Password: ", offset), "login hidden prompt")
        step(machine, secrets[1], "authenticated as user")
        step(machine, "whoami | logger", "user: user")
        step(machine, "test -z \"$PRIVATE\" && logger private-cleared", "user: private-cleared")
        offset = len(machine.log()); machine.send("login root\r"); assert_(machine.wait("Password: ", offset), "failed login hidden prompt")
        step(machine, secrets[1], "authentication failed")
        step(machine, "whoami | logger", "user: user")
        step(machine, "cat /etc/shadow; echo shadow=$? | logger", "user: shadow=1")
        step(machine, "settings set timezone UTC; echo denied=$? | logger", "user: denied=1")
        step(machine, "echo owned > /home/user/serial-owned; stat /home/user/serial-owned | logger", "Uid: 1000  Gid: 1000")
        step(machine, "exit", "[console] session ended")
        step(machine, "id | logger", "uid=65534(nobody)")
        offset = len(machine.log()); machine.send("login root\r"); assert_(machine.wait("Password: ", offset), "root hidden prompt")
        step(machine, secrets[0], "authenticated as root")
        # Invalid future schema retains the file; next no-GPU boot must still offer a console.
        step(machine, "printf 'VERSION=2\\nSESSION=de\\n' > /etc/zenith/session.conf; sync; poweroff", "all filesystems unmounted")
        assert_(machine.process.wait(timeout=30) == 0, "installed clean poweroff")
        assert_(all(secret not in machine.log() for secret in secrets), "serial transcript has no passwords")
    finally: machine.stop()
    installed = machine.disk
    machine = boot("fallback", installed, "session=invalid")
    try:
        step(machine, "session status | logger", "active=headless (capability fallback)")
        step(machine, "id | logger", "uid=65534(nobody)")
        offset = len(machine.log()); machine.send("login root\r"); assert_(machine.wait("Password: ", offset), "persisted root login prompt")
        step(machine, secrets[0], "authenticated as root")
        step(machine, "session set-default headless; echo future=$? | logger", "user: future=1")
        step(machine, "cat /etc/zenith/session.conf | grep VERSION | logger", "user: VERSION=2")
        step(machine, "grep 'S1!' /var/log/boot.log; echo logged=$? | logger", "user: logged=1")
        step(machine, "printf 'VERSION=1\\nSESSION=de\\n' > /etc/zenith/session.conf; sync; poweroff", "all filesystems unmounted")
        assert_(machine.process.wait(timeout=30) == 0, "fallback clean poweroff")
        assert_(all(secret not in machine.log() for secret in secrets), "reboot transcript has no passwords")
    finally: machine.stop()
    # Independent check of root after persistent serial writes/authentication/shutdown.
    disk = machine.disk; volume = args.keep / "root-check.img"
    with disk.open("rb") as file:
        file.seek(512); header = file.read(512); file.seek(struct.unpack_from("<Q", header, 72)[0] * 512 + 128)
        entry = file.read(128); start, end = struct.unpack_from("<QQ", entry, 32)
        file.seek(start * 512); volume.write_bytes(file.read((end-start+1)*512))
    result = subprocess.run(["e2fsck", "-f", "-n", str(volume)], capture_output=True, text=True)
    (args.keep / "e2fsck.log").write_text(result.stdout + result.stderr)
    assert_(result.returncode == 0, "independent root e2fsck clean")
    machine = boot("override-panic", disk, "session=headless")
    try:
        step(machine, "session status | logger", "active=headless (boot argument)")
        offset = len(machine.log()); machine.send("login root\r"); assert_(machine.wait("Password: ", offset), "panic test root login")
        step(machine, secrets[0], "authenticated as root")
        step(machine, "crash", "[console] Zenith stopped. Press R on serial to restart.")
        assert_("panic: InvalidOperationException: panic requested: crash command" in machine.log(), "serial panic diagnostic")
        machine.send("r"); machine.process.wait(timeout=30)
        assert_(machine.process.returncode == 0, "serial panic restart requested")
    finally: machine.stop()
    print(f"{checks-failures}/{checks} checks passed", flush=True)
    return bool(failures)
if __name__ == "__main__": raise SystemExit(main())
