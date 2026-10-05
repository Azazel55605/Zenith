#!/usr/bin/env python3
"""Boot-tests Zenith in QEMU.

Boots output-x64/Zenith.iso headless with a blank disk attached, types shell commands into the
Terminal through the QEMU monitor, and checks the kernel log on the serial port for the
results (commands report back with `logger`, which writes to the kernel log). Ends with
`crash` to exercise the panic screen.

  tools/smoke-test.py [--iso PATH] [--timeout SECONDS] [--step-timeout SECONDS] [--keep DIR]

Exit status 0 when every check passes. Uses KVM when available, TCG otherwise (slower, so the
timeouts are generous). Needs only qemu-system-x86_64 (or $QEMU) and Python 3.
"""
import argparse
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# (command typed into the Terminal, text expected in the serial log)
STEPS = [
    ("uname -a | logger", "user: Zenith zenith 0.1"),
    ("sleep 1 && logger slept", "user: slept"),
    # Ctrl+C (^C) stops the running sleep; the next line was typed ahead while it ran.
    ("sleep 30\n^Cecho st=$? | logger", "user: st=130"),
    ("echo smoke-ok > /tmp/t.txt; cat /tmp/t.txt | logger", "user: smoke-ok"),
    # Scripting: a script file, a loop typed over several lines, and `read` answered by the next line.
    ("echo 'for i in 1 2 3; do echo n$i; done' > /tmp/s.sh && sh /tmp/s.sh | tail -1 | logger", "user: n3"),
    ("for x in a b\ndo logger loop-$x\ndone", "user: loop-b"),
    ("read -p 'name? ' who && logger hi-$who\nZen", "user: hi-Zen"),
    ("ls / | grep etc | logger", "user: etc"),
    ("nope; echo status=$? | logger", "user: status=127"),
    ("then\necho syntax=$? | logger", "user: syntax=2"),   # (an open quote would ask for more lines)
    ("install sata0 --yes && logger INSTALL-OK", "user: INSTALL-OK"),
    ("mkdir /mnt/inst && mount sata0p1 /mnt/inst && cat /mnt/inst/etc/hostname | logger", "user: zenith"),
    ("umount /mnt/inst && sync && logger unmounted", "user: unmounted"),
    # Editor: open a new file, type, save (Ctrl+S), close (Ctrl+Q); focus returns to the terminal.
    ("edit /tmp/ed.txt\n%%DELAY%%written in editor^S^Qcat /tmp/ed.txt | logger", "user: written in editor"),
    ("lsblk | grep sata0p1 | logger", "sata0p1       190M  part"),   # serial is ASCII-only: no box glyphs
    # German layout: the key QEMU calls "y" types "z". Switch back by typing "loadkezs us".
    ("localectl set-keymap de\nlogger y", "user: z"),
    ("loadkezs us\ncat /etc/vconsole.conf | logger", "user: KEYMAP=de"),
    ("timedatectl set-timezone Europe/Berlin && timedatectl | grep zone | logger", "Time zone: Europe/Berlin"),
    ("crash", "panic: InvalidOperationException: panic requested: crash command"),
]

# Cosmos' own debug logging ("[Kernel] Run() returned", "[SCHED] ...") shares the serial port
# and is written byte by byte from another thread, so it can land in the middle of one of our
# lines. Dropping every tagged line that isn't ours stitches those lines back together.
COSMOS_NOISE = re.compile(r"\[(?!zenith\])[A-Za-z][A-Za-z0-9 ]*\][^\n]*\n")

KEYS = {" ": "spc", "\n": "ret", "/": "slash", ".": "dot", "-": "minus", "=": "equal", ",": "comma",
        ";": "semicolon", "'": "apostrophe", "\\": "backslash", "`": "grave_accent",
        "[": "bracket_left", "]": "bracket_right", "\t": "tab"}
SHIFTED = {"|": "backslash", ">": "dot", "<": "comma", "_": "minus", "+": "equal", ":": "semicolon",
           '"': "apostrophe", "~": "grave_accent", "!": "1", "@": "2", "#": "3", "$": "4", "%": "5",
           "^": "6", "&": "7", "*": "8", "(": "9", ")": "0", "?": "slash", "{": "bracket_left",
           "}": "bracket_right"}


class Machine:
    def __init__(self, iso: Path, workdir: Path):
        self.workdir = workdir
        self.serial = workdir / "serial.log"
        self.monitor = workdir / "monitor.sock"
        self.disk = workdir / "disk.img"
        with open(self.disk, "wb") as f:
            f.truncate(256 * 1024 * 1024)   # sparse raw image; no qemu-img needed
        self.process = subprocess.Popen([
            os.environ.get("QEMU", "qemu-system-x86_64"), "-M", "q35", "-m", "512M",
            "-accel", "kvm", "-accel", "tcg", "-cpu", "max",
            "-display", "none", "-no-reboot",
            "-serial", f"file:{self.serial}",
            "-monitor", f"unix:{self.monitor},server,nowait",
            "-vga", "none", "-device", "virtio-gpu-pci",
            "-device", "virtio-keyboard-pci", "-device", "virtio-mouse-pci",
            "-cdrom", str(iso), "-boot", "order=d",
            "-drive", f"file={self.disk},format=raw,if=none,id=disk0", "-device", "ide-hd,drive=disk0",
        ], stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)

    def log(self) -> str:
        raw = self.serial.read_text(errors="replace") if self.serial.exists() else ""
        return COSMOS_NOISE.sub("", raw)

    def wait_for(self, text: str, timeout: float) -> bool:
        deadline = time.time() + timeout
        while time.time() < deadline:
            if text in self.log():
                return True
            if self.process.poll() is not None:
                return False
            time.sleep(0.25)
        return False

    def command(self, line: str):
        sock = socket.socket(socket.AF_UNIX)
        sock.connect(str(self.monitor))
        sock.recv(4096)
        sock.sendall((line + "\n").encode())
        time.sleep(0.02)
        sock.close()

    def type(self, text: str):
        # ^C ^S ^Q: Ctrl+key chords; %%DELAY%%: give a window time to open.
        text = text.replace("^C", "\x03").replace("^S", "\x13").replace("^Q", "\x11").replace("%%DELAY%%", "\x00")
        chords = {"\x03": "ctrl-c", "\x13": "ctrl-s", "\x11": "ctrl-q"}
        for ch in text:
            if ch == "\x00":
                time.sleep(1.5)
                continue
            if ch in chords:
                time.sleep(0.5)
                self.command("sendkey " + chords[ch])
                time.sleep(0.5)
                continue
            if ch in KEYS:
                key = KEYS[ch]
            elif ch in SHIFTED:
                key = "shift-" + SHIFTED[ch]
            elif ch.isupper():
                key = "shift-" + ch.lower()
            else:
                key = ch
            self.command("sendkey " + key)
            time.sleep(0.03)

    def wait_exit(self, timeout: float) -> bool:
        try:
            self.process.wait(timeout=timeout)
            return True
        except subprocess.TimeoutExpired:
            return False

    def screenshot(self, path: Path):
        self.command(f"screendump {path}")
        time.sleep(1)

    def stop(self):
        if self.process.poll() is None:
            try:
                self.command("quit")
                self.process.wait(timeout=10)
            except Exception:
                self.process.kill()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--iso", type=Path, default=ROOT / "output-x64" / "Zenith.iso")
    parser.add_argument("--timeout", type=float, default=180, help="seconds to wait for boot")
    parser.add_argument("--step-timeout", type=float, default=60, help="seconds to wait for each command")
    parser.add_argument("--keep", type=Path, help="copy the serial log and screenshots here")
    args = parser.parse_args()

    if not args.iso.exists():
        print(f"missing {args.iso}; build first", file=sys.stderr)
        return 2

    workdir = Path(tempfile.mkdtemp(prefix="zenith-smoke-"))
    machine = Machine(args.iso, workdir)
    failures = 0
    try:
        start = time.time()
        if not machine.wait_for("kernel: desktop ready", args.timeout):
            print("FAIL boot: desktop never came up")
            failures += 1
        else:
            print(f"ok   boot ({time.time() - start:.1f}s)")
            time.sleep(2)   # let the first frames settle before typing
            for line, expected in STEPS:
                machine.type(line + "\n")
                if machine.wait_for(expected, args.step_timeout):
                    print(f"ok   {line}")
                else:
                    print(f"FAIL {line}\n     expected in log: {expected}")
                    failures += 1
            machine.screenshot(workdir / "final.ppm")
    finally:
        machine.stop()
        if args.keep:
            args.keep.mkdir(parents=True, exist_ok=True)
            for name in ("serial.log", "final.ppm"):
                if (workdir / name).exists():
                    shutil.copy(workdir / name, args.keep / name)
        if failures:
            print("\n--- last 40 kernel log lines ---")
            lines = [l for l in machine.log().splitlines() if l.startswith("[zenith]")]
            print("\n".join(lines[-40:]))
        shutil.rmtree(workdir, ignore_errors=True)

    # Second, short boot: a clean power-off unmounts everything and the VM actually turns off.
    workdir2 = Path(tempfile.mkdtemp(prefix="zenith-smoke-"))
    machine2 = Machine(args.iso, workdir2)
    try:
        if machine2.wait_for("kernel: desktop ready", args.timeout):
            time.sleep(2)
            machine2.type("poweroff\n")
            if machine2.wait_for("mounts: all filesystems unmounted", args.step_timeout) and machine2.wait_exit(args.step_timeout):
                print("ok   poweroff (filesystems unmounted, VM turned off)")
            else:
                print("FAIL poweroff: no clean unmount, or the VM did not turn off")
                failures += 1
        else:
            print("FAIL poweroff: second boot never came up")
            failures += 1
    finally:
        machine2.stop()
        shutil.rmtree(workdir2, ignore_errors=True)

    total = len(STEPS) + 2
    print(f"\n{total - failures}/{total} checks passed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
