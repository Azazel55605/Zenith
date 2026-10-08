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
    ("ls /proc | grep meminfo | logger", "user: meminfo"),
    ("cat /proc/meminfo | grep MemTotal | logger", "user: MemTotal:"),
    ("cat /proc/mounts | grep /proc | logger", "user: proc /proc proc ro 0 0"),
    ("test -n \"$(cat /proc/uptime)\" && logger proc-uptime-ok", "user: proc-uptime-ok"),
    ("echo denied > /proc/meminfo; echo proc-write=$? | logger", "user: proc-write=1"),
    ("cat /proc/cmdline | wc -c | logger", "user: 1"),
    ("cat /proc/mounts | grep /dev | logger", "user: dev /dev dev rw 0 0"),
    ("echo discard > /dev/null && echo discard >> /dev/zero && logger dev-write-ok", "user: dev-write-ok"),
    ("cat /dev/null | wc -c | logger", "user: 0"),
    ("dd if=/dev/zero of=/tmp/zeros bs=17 count=3 && cat /tmp/zeros | wc -c | logger", "user: 51"),
    ("dd if=/tmp/zeros of=/dev/null bs=7 count=20 && logger dev-copy-ok", "user: dev-copy-ok"),
    ("dd if=/tmp/zeros of=/tmp/ZEROS count=1; echo dd-alias=$? | logger", "user: dd-alias=1"),
    ("dd if=/dev/zero of=/dev/null count=999999999\n^Cecho dd-stop=$? | logger", "user: dd-stop=130"),
    ("touch /dev/nope; echo dev-create=$? | logger", "user: dev-create=1"),
    ("install sata0 --yes && logger INSTALL-OK", "user: INSTALL-OK"),
    ("mkdir /mnt/inst && mount sata0p1 /mnt/inst && cat /mnt/inst/etc/hostname | logger", "user: zenith"),
    ("umount /mnt/inst && sync && logger unmounted", "user: unmounted"),
    # Editor: open a new file, type, save (Ctrl+S), close (Ctrl+Q); focus returns to the terminal.
    ("edit /tmp/ed.txt\n%%DELAY%%written in editor^S^Qcat /tmp/ed.txt | logger", "user: written in editor"),
    ("man sh | grep COMPOUND | logger", "user: COMPOUND COMMANDS"),
    # Clipboard: copy in the editor (Ctrl+A, Ctrl+C), discard and close (Ctrl+Q twice), paste into the
    # terminal (Ctrl+Shift+V); the pasted line break submits the command.
    ("edit\n{delay}logger clip-ok\n{ctrl-a}{ctrl-c}{ctrl-q}{ctrl-q}{ctrl-shift-v}", "user: clip-ok"),
    # Regex (System.Text.RegularExpressions) on Cosmos: sed and grep -E; xargs re-enters the shell.
    ("echo hello world | sed 's/o/0/g' | logger", "user: hell0 w0rld"),
    ("seq 12 | grep -E '^1[0-9]$' | xargs | logger", "user: 10 11 12"),
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

    def raw_log(self) -> str:
        return self.serial.read_text(errors="replace") if self.serial.exists() else ""

    def log(self) -> str:
        return COSMOS_NOISE.sub("", self.raw_log())

    def wait_for(self, text: str, timeout: float) -> bool:
        deadline = time.time() + timeout
        while time.time() < deadline:
            raw = self.raw_log()
            # A Cosmos tag can precede an otherwise intact marker on the same
            # line. Noise stripping then discards that marker; check both forms.
            if text in raw or text in COSMOS_NOISE.sub("", raw):
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
        # {ctrl-a}, {ctrl-shift-v}...: key chords; ^C ^S ^Q: shorthands; %%DELAY%%: let a window open.
        text = text.replace("^C", "{ctrl-c}").replace("^S", "{ctrl-s}").replace("^Q", "{ctrl-q}").replace("%%DELAY%%", "{delay}")
        for part in re.split(r"(\{[a-z0-9-]+\})", text):
            if part == "{delay}":
                time.sleep(1.5)
            elif part.startswith("{") and part.endswith("}"):
                time.sleep(0.5)
                self.command("sendkey " + part[1:-1])
                time.sleep(0.5)
            else:
                self.type_plain(part)

    def type_plain(self, text: str):
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


BOOT_ATTEMPTS = 3


def boot(iso: Path, timeout: float, label: str):
    """
    Boots a fresh VM until the desktop is up, retrying when the kernel faults during early
    init. Cosmos 3.0.89 occasionally takes a general-protection fault while setting up the
    APIC under nested KVM (GitHub's runners); see docs/ROADMAP.md. Returns (machine, attempts)
    or (None, attempts).
    """
    for attempt in range(1, BOOT_ATTEMPTS + 1):
        workdir = Path(tempfile.mkdtemp(prefix="zenith-smoke-"))
        machine = Machine(iso, workdir)
        start = time.time()
        while time.time() - start < timeout:
            log = machine.log()
            if "kernel: desktop ready" in log:
                return machine, attempt
            if "FATAL: Exception" in log or machine.process.poll() is not None:
                break
            time.sleep(0.25)

        fatal = [l for l in machine.log().splitlines() if "[INT]" in l or "FATAL" in l]
        print(f"warn {label}: boot attempt {attempt} failed" + (": " + " | ".join(fatal[:3]) if fatal else " (timeout)"))
        machine.stop()
        shutil.rmtree(workdir, ignore_errors=True)

    return None, BOOT_ATTEMPTS


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

    failures = 0
    retried = 0
    start = time.time()
    machine, attempts = boot(args.iso, args.timeout, "main boot")
    retried += attempts - 1
    if machine is None:
        print("FAIL boot: desktop never came up")
        failures += 1
    else:
        try:
            print(f"ok   boot ({time.time() - start:.1f}s)")
            time.sleep(2)   # let the first frames settle before typing
            for line, expected in STEPS:
                machine.type(line + "\n")
                if machine.wait_for(expected, args.step_timeout):
                    print(f"ok   {line}")
                else:
                    print(f"FAIL {line}\n     expected in log: {expected}")
                    failures += 1
            machine.screenshot(machine.workdir / "final.ppm")
        finally:
            machine.stop()
            if args.keep:
                args.keep.mkdir(parents=True, exist_ok=True)
                for name in ("serial.log", "final.ppm"):
                    if (machine.workdir / name).exists():
                        shutil.copy(machine.workdir / name, args.keep / name)
            if failures:
                print("\n--- last 40 kernel log lines ---")
                lines = [l for l in machine.log().splitlines() if l.startswith("[zenith]")]
                print("\n".join(lines[-40:]))
            shutil.rmtree(machine.workdir, ignore_errors=True)

    # Second, short boot: a clean power-off unmounts everything and the VM actually turns off.
    machine2, attempts = boot(args.iso, args.timeout, "poweroff boot")
    retried += attempts - 1
    if machine2 is None:
        print("FAIL poweroff: second boot never came up")
        failures += 1
    else:
        try:
            time.sleep(2)
            machine2.type("poweroff\n")
            if machine2.wait_for("mounts: all filesystems unmounted", args.step_timeout) and machine2.wait_exit(args.step_timeout):
                print("ok   poweroff (filesystems unmounted, VM turned off)")
            else:
                print("FAIL poweroff: no clean unmount, or the VM did not turn off")
                failures += 1
        finally:
            machine2.stop()
            shutil.rmtree(machine2.workdir, ignore_errors=True)

    if retried:
        print(f"\nnote: {retried} boot(s) had to be retried after an early kernel fault")

    total = len(STEPS) + 2
    print(f"\n{total - failures}/{total} checks passed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
