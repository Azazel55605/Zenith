#!/usr/bin/env python3
"""Boot-tests Zenith in QEMU.

Boots output-x64/Zenith.iso headless with a blank disk attached, types shell commands into the
Terminal through the QEMU monitor, and checks the kernel log on the serial port for the
results (commands report back with `logger`, which writes to the kernel log). Ends with
`crash` to exercise the panic screen.

  tools/smoke-test.py [--iso PATH] [--timeout SECONDS] [--step-timeout SECONDS] [--keep DIR]

Exit status 0 when every check passes. Uses KVM when available, TCG otherwise (slower, so the
timeouts are generous). Needs only qemu-system-x86_64 and Python 3.
"""
import argparse
import os
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
    ("echo smoke-ok > /tmp/t.txt; cat /tmp/t.txt | logger", "user: smoke-ok"),
    ("ls / | grep etc | logger", "user: etc"),
    ("nope; echo status=$? | logger", "user: status=127"),
    ("install sata0 --yes && logger INSTALL-OK", "user: INSTALL-OK"),
    ("lsblk | grep sata0p1 | logger", "sata0p1       190M  part"),   # serial is ASCII-only: no box glyphs
    ("crash", "panic: InvalidOperationException: panic requested: crash command"),
]

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
        subprocess.run(["qemu-img", "create", "-f", "raw", str(self.disk), "256M"], check=True, stdout=subprocess.DEVNULL)
        self.process = subprocess.Popen([
            "qemu-system-x86_64", "-M", "q35", "-m", "512M",
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
        return self.serial.read_text(errors="replace") if self.serial.exists() else ""

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
        for ch in text:
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

    print(f"\n{len(STEPS) + 1 - failures}/{len(STEPS) + 1} checks passed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
