#!/usr/bin/env python3
"""Boot a clone of the smoke-test installation from its EFI disk, without an ISO."""
import argparse
import importlib.util
import shutil
import subprocess
import tempfile
from pathlib import Path
import ext2_fixture

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("smoke", ROOT / "tools/smoke-test.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


def firmware():
    for code, vars in (
        ("/usr/share/OVMF/OVMF_CODE_4M.fd", "/usr/share/OVMF/OVMF_VARS_4M.fd"),
        ("/usr/share/OVMF/OVMF_CODE.fd", "/usr/share/OVMF/OVMF_VARS.fd"),
        ("/usr/share/OVMF/x64/OVMF_CODE.4m.fd", "/usr/share/OVMF/x64/OVMF_VARS.4m.fd"),
        ("/usr/share/edk2/x64/OVMF_CODE.4m.fd", "/usr/share/edk2/x64/OVMF_VARS.4m.fd"),
    ):
        if Path(code).is_file() and Path(vars).is_file():
            return code, vars
    raise RuntimeError("missing OVMF firmware; install ovmf/edk2-ovmf")


class InstalledMachine(smoke.Machine):
    def __init__(self, iso, workdir, initial_disk=None, memory=512):
        self.workdir = workdir
        self.serial = workdir / "serial.log"
        self._gc_offset = 0
        self._gc = smoke.GcPauseTracker()
        self.monitor = workdir / "monitor.sock"
        self.disk = workdir / "disk.img"
        shutil.copyfile(initial_disk, self.disk)
        code, vars = firmware()
        private_vars = workdir / "OVMF_VARS.fd"
        shutil.copyfile(vars, private_vars)
        self.process = subprocess.Popen([
            smoke.os.environ.get("QEMU", "qemu-system-x86_64"), "-M", "q35", "-m", str(memory) + "M",
            "-accel", "kvm", "-accel", "tcg", "-cpu", "max", "-display", "none", "-no-reboot",
            "-serial", f"file:{self.serial}", "-monitor", f"unix:{self.monitor},server,nowait",
            "-vga", "none", "-device", "virtio-gpu-pci",
            "-device", "virtio-keyboard-pci", "-device", "virtio-mouse-pci",
            "-drive", f"if=pflash,format=raw,readonly=on,file={code}",
            "-drive", f"if=pflash,format=raw,file={private_vars}",
            "-drive", f"file={self.disk},format=raw,if=none,id=disk0",
            "-device", "ide-hd,drive=disk0,bus=ide.0,unit=0",
        ], stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("disk", type=Path)
    parser.add_argument("--configuration", choices=("Debug", "Release"), default="Release")
    parser.add_argument("--timeout", type=float, default=600)
    parser.add_argument("--step-timeout", type=float, default=180)
    parser.add_argument("--keep", type=Path)
    parser.add_argument("--test-session-override", action="store_true", help="Boot with session=de in the cloned ESP configuration")
    args = parser.parse_args()
    if not args.disk.is_file():
        parser.error("disk must be a regular image file")
    firmware()
    with tempfile.TemporaryDirectory(prefix="zenith-efi-") as scratch:
        prepared = Path(scratch) / "disk.img"
        shutil.copyfile(args.disk, prepared)
        import boot_image
        boot_image.verify(prepared)
        if args.test_session_override:
            boot_image.override(prepared, "session=de", Path(scratch))
        smoke.Machine = InstalledMachine
        machine, attempts = smoke.boot(Path(), args.timeout, "EFI disk-only", prepared)
        if machine is None:
            print("FAIL disk-only EFI boot"); return 1
        failures = 0
        print("ok   disk-only EFI boot (" + str(attempts) + " attempt(s))")
        steps = [
            ("cat /proc/mounts | grep ' / ext2 ' | logger", " / ext2 "),
            ("cat /home/user/ext2-link | logger", "user: installed-ext2"),
            ("stat /home/user/ext2-persist | grep Access | logger", "Uid: 70000  Gid: 80000"),
            ("fsprobe /home/user | logger", "final-close deletion and reuse passed"),
        ]
        if args.test_session_override:
            steps.append(("session status | logger", "active=de (boot argument)"))
            # Full manuals now belong to zenith-man-pages, whose installation is
            # covered by native-process-smoke. Check actual persisted settings and
            # recovery help here rather than pinning an obsolete editorial title.
            steps.append(("settings show | logger", "scope: system; storage: installed"))
            steps.append(("man headless | head -1 | logger", "SERIAL HEADLESS SESSION"))
            steps.append(("man pkg | grep update | logger", "update [--yes]"))
        try:
            for line, expected in steps:
                offset = len(machine.raw_log())
                machine.type(line + "\n")
                if machine.wait_for(expected, args.step_timeout, offset):
                    print("ok   " + line)
                else:
                    print("FAIL " + line); failures += 1
            machine.type("poweroff\n")
            if machine.wait_for("mounts: all filesystems unmounted", args.step_timeout) and machine.wait_exit(args.step_timeout):
                print("ok   EFI ext2 clean poweroff")
            else:
                print("FAIL EFI ext2 poweroff"); failures += 1
        finally:
            machine.stop()
            report = machine.workdir / "installed-check.log"
            if ext2_fixture.verify_installed(machine.disk, report):
                print("ok   EFI root independent e2fsck and persistent attributes")
            else:
                print("FAIL EFI disk inspection\n" + report.read_text()); failures += 1
            if args.keep:
                args.keep.mkdir(parents=True, exist_ok=True)
                for source in (machine.serial, machine.disk, report):
                    shutil.copyfile(source, args.keep / source.name)
            shutil.rmtree(machine.workdir, ignore_errors=True)
        total = 3 + len(steps)
        print(f"{total - failures}/{total} checks passed")
        return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
