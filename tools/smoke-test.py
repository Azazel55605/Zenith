#!/usr/bin/env python3
"""Boot-tests Zenith in QEMU.

Boots output-x64/Zenith.iso headless with a blank disk attached, types shell commands into the
Terminal through the QEMU monitor, and checks the kernel log on the serial port for the
results (commands report back with `logger`, which writes to the kernel log). Ends with
`crash` to exercise the panic screen.

  tools/smoke-test.py [--iso PATH] [--timeout SECONDS] [--step-timeout SECONDS] [--keep DIR]

Exit status 0 when every check passes. Uses KVM when available, TCG otherwise (slower, so the
timeouts are generous). Needs qemu-system-x86_64 (or $QEMU), Python 3 and e2fsprogs (mke2fs/e2fsck/debugfs).
"""
import argparse
import ext2_fixture
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
    ("ls /dev | grep sata0 | logger", "user: sata0"),
    ("dd if=/dev/sata0 of=/tmp/raw bs=17 count=3 && stat /tmp/raw | grep Size | logger", "user:   Size: 51 bytes"),
    ("echo denied > /dev/sata0; echo raw-write=$? | logger", "user: raw-write=1"),
    ("dd if=/tmp/zeros of=/dev/sata0 count=1; echo raw-dd=$? | logger", "user: raw-dd=1"),
    ("install sata0 --yes && logger INSTALL-OK", "user: INSTALL-OK"),
    ("mkdir /mnt/inst && mount sata0p1 /mnt/inst && cat /mnt/inst/etc/hostname | logger", "user: zenith"),
    ("ls /dev | grep sata0p1 | logger", "user: sata0p1"),
    ("dd if=/dev/sata0p1 of=/tmp/sector count=1 && stat /tmp/sector | grep Size | logger", "user:   Size: 512 bytes"),
    ("echo denied > /dev/sata0p1; echo partition-write=$? | logger", "user: partition-write=1"),
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
    ("mkdir /mnt/ext; mount -t ext2 sata0p1 /mnt/ext; echo ext2-reject-fat=$? | logger", "user: ext2-reject-fat=1"),
    ("mount -t ext2 sata1p0 /mnt/ext && cat /mnt/ext/host.txt | logger", "user: host-ext2"),
    ("echo persisted-ext2 > /mnt/ext/persist.txt && mkdir /mnt/ext/dir && logger ext2-write-ok", "user: ext2-write-ok"),
    ("echo lower > /mnt/ext/dir/a; echo upper > /mnt/ext/dir/A; logger ext2-case-$(cat /mnt/ext/dir/a)-$(cat /mnt/ext/dir/A)", "user: ext2-case-lower-upper"),
    ("mv /mnt/ext/dir/a /mnt/ext/dir/renamed && rm /mnt/ext/dir/A && logger ext2-rename-$(cat /mnt/ext/dir/renamed)", "user: ext2-rename-lower"),
    ("dd if=/dev/zero of=/mnt/ext/indirect bs=1024 count=20 && logger ext2-indirect-ok", "user: ext2-indirect-ok"),
    ("echo shrunk > /mnt/ext/indirect && logger ext2-truncate-ok", "user: ext2-truncate-ok"),
    ("rm /mnt/ext/delete.bin && logger ext2-delete-ok", "user: ext2-delete-ok"),
    ("echo sparse-shrunk > /mnt/ext/sparse.bin && logger ext2-sparse-shrink-ok", "user: ext2-sparse-shrink-ok"),
    ("dd if=/dev/zero of=/mnt/ext/recycle bs=1024 count=300 && rm /mnt/ext/recycle && logger ext2-double-delete-ok", "user: ext2-double-delete-ok"),
    ("umount /mnt/ext && mount -t ext2 sata1p0 /mnt/ext && logger ext2-remount-$(cat /mnt/ext/persist.txt)", "user: ext2-remount-persisted-ext2"),
    ("stat /mnt/ext/indirect | grep Size | logger", "user:   Size: 7 bytes"),
    ("mkdir /mnt/ext/empty && rmdir /mnt/ext/empty && logger ext2-rmdir-ok", "user: ext2-rmdir-ok"),
    ("cat /mnt/ext/sparse.bin | logger", "user: sparse-shrunk"),
    ("cat /mnt/ext/indirect | logger", "user: shrunk"),
    ("umount /mnt/ext && logger ext2-unmounted", "user: ext2-unmounted"),
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
        self.ext2_disk = ext2_fixture.create(workdir)
        self.process = subprocess.Popen([
            os.environ.get("QEMU", "qemu-system-x86_64"), "-M", "q35", "-m", "512M",
            "-accel", "kvm", "-accel", "tcg", "-cpu", "max",
            "-display", "none", "-no-reboot",
            "-serial", f"file:{self.serial}",
            "-monitor", f"unix:{self.monitor},server,nowait",
            "-vga", "none", "-device", "virtio-gpu-pci",
            "-device", "virtio-keyboard-pci", "-device", "virtio-mouse-pci",
            "-cdrom", str(iso), "-boot", "order=d",
            "-drive", f"file={self.disk},format=raw,if=none,id=disk0", "-device", "ide-hd,drive=disk0,bus=ide.0,unit=0",
            "-drive", f"file={self.ext2_disk},format=raw,if=none,id=disk1", "-device", "ide-hd,drive=disk1,bus=ide.1,unit=0",
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

        if machine.process.poll() is not None:
            print("QEMU exited: " + machine.process.stderr.read().decode(errors="replace").strip())
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
    parser.add_argument("--keep", type=Path, help="copy logs, screenshots and the ext2 scratch disk here")
    args = parser.parse_args()

    if not args.iso.exists():
        print(f"missing {args.iso}; build first", file=sys.stderr)
        return 2

    for tool in ("mke2fs", "e2fsck", "debugfs"):
        if shutil.which(tool) is None:
            print(f"missing {tool}; install e2fsprogs", file=sys.stderr)
            return 2

    failures = 0
    retried = 0
    start = time.time()
    machine, attempts = boot(args.iso, args.timeout, "main boot")
    retried += attempts - 1
    if machine is None:
        print("FAIL boot: desktop never came up")
        failures += len(STEPS) + 2
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
            report = machine.workdir / "ext2-check.log"
            if ext2_fixture.verify(machine.ext2_disk, report):
                print("ok   ext2 host e2fsck and persisted content")
            else:
                print("FAIL ext2 host check\n" + report.read_text())
                failures += 1
            if args.keep:
                args.keep.mkdir(parents=True, exist_ok=True)
                for name in ("serial.log", "final.ppm", "ext2-check.log", "ext2-disk.img"):
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

    total = len(STEPS) + 3
    print(f"\n{total - failures}/{total} checks passed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
