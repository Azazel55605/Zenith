#!/usr/bin/env python3
"""M2 authentication/access regressions on a disposable installed ext2 guest."""
import importlib.util
from pathlib import Path
import shutil
import tempfile
import time
import sys

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("smoke", ROOT / "tools/smoke-test.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)
keep = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("/tmp/zenith-security-guest")
keep.mkdir(parents=True, exist_ok=True)
disk = keep / "installed.img"
failures = 0

def check(machine, command, expected, timeout=180):
    global failures
    offset = len(machine.raw_log())
    machine.type(command + ("" if command.endswith("\n") else "\n"))
    passed = machine.wait_for(expected, timeout, offset=offset)
    print(("ok   " if passed else "FAIL ") + expected, flush=True)
    failures += not passed
    return passed

def boot(label, initial=None):
    machine, _ = smoke.boot(ROOT / "output-x64/Zenith.iso", 600, label, initial)
    if machine is None:
        raise RuntimeError("guest did not boot: " + label)
    time.sleep(2)
    return machine

machine = boot("security live")
try:
    check(machine,"securityprobe | logger","security primitives ok")
    check(machine,"install sata0 --yes && logger installed","user: installed")
    check(machine,"poweroff","all filesystems unmounted")
    if not machine.wait_exit(15): raise RuntimeError("guest did not shut down")
    shutil.copyfile(machine.disk,disk)
finally:
    machine.stop(); shutil.copyfile(machine.serial,keep / "live.log"); shutil.rmtree(machine.workdir)

machine = boot("security setup", disk)
try:
    check(machine,"whoami | logger","user: root")
    check(machine,"accessprobe /home/user | logger","access probe passed")
    check(machine,"dd if=/dev/random of=/tmp/random bs=32 count=2 && stat /tmp/random | grep Size | logger","Size: 64 bytes")
    check(machine,"passwd root\n{delay}Root-Test-42\n{delay}Root-Test-42\n{delay}logger root-pass-set","user: root-pass-set")
    check(machine,"passwd user\n{delay}User-Test-42\n{delay}User-Test-42\n{delay}logger user-pass-set","user: user-pass-set")
    check(machine,"useradd alice && logger account-added","user: account-added")
    check(machine,"su user; id | logger","uid=1000(user) gid=1000")
    check(machine,"echo mine > /home/user/owned && stat /home/user/owned | grep Uid | logger","Uid: 1000  Gid: 1000")
    check(machine,"cat /etc/shadow; echo shadow-denied=$? | logger","user: shadow-denied=1")
    check(machine,"echo bad > /root/no; echo root-denied=$? | logger","user: root-denied=1")
    check(machine,"cd /root; echo search-denied=$? | logger","user: search-denied=1")
    check(machine,"chown 0 /home/user/owned; echo chown-denied=$? | logger","user: chown-denied=1")
    check(machine,"echo tmp > /tmp/user-owned && stat /tmp/user-owned | grep Uid | logger","Uid: 1000  Gid: 1000")
    check(machine,"logger audit-mirror-ok; cat /var/log/boot.log | grep audit-mirror-ok | wc -l | logger","user: 1")
    check(machine,"export USER=root; whoami | logger","user: user")
    check(machine,"edit /home/user/gui.txt\n{delay}user-editor-data{ctrl-s}{ctrl-q}cat /home/user/gui.txt | logger","user: user-editor-data")
    check(machine,"open files /home/user && logger files-user","user: files-user")
    check(machine,"{delay}{ctrl-n}gui-created.txt\n{ctrl-q}stat /home/user/gui-created.txt | grep Uid | logger","Uid: 1000  Gid: 1000")
    check(machine,"install sata0 --yes; echo install-denied=$? | logger","user: install-denied=1")
    check(machine,"dd if=/dev/sata0 of=/tmp/leak count=1; echo raw-denied=$? | logger","user: raw-denied=1")
    check(machine,"su root\n{delay}Wrong-Test-42\n{delay}whoami | logger","user: user")
    check(machine,"su root\n{delay}Root-Test-42\n{delay}whoami | logger","user: root")
    check(machine,"chmod 0600 /home/user/owned && echo root-data > /tmp/root-owned && chmod 0666 /tmp/root-owned && su user; rm /tmp/root-owned; echo sticky-denied=$? | logger","user: sticky-denied=1")
    check(machine,"su root\n{delay}Root-Test-42\n{delay}poweroff","all filesystems unmounted")
    if not machine.wait_exit(15): raise RuntimeError("guest did not shut down")
    shutil.copyfile(machine.disk,disk)
finally:
    machine.stop(); shutil.copyfile(machine.serial,keep / "setup.log"); shutil.rmtree(machine.workdir)

machine = boot("security persisted", disk)
try:
    check(machine,"whoami | logger","user: nobody")
    check(machine,"cat /etc/shadow; echo shadow-denied=$? | logger","user: shadow-denied=1")
    check(machine,"login user\n{delay}User-Test-42\n{delay}{delay}id | logger","uid=1000(user) gid=1000")
    check(machine,"cat /home/user/owned | logger","user: mine")
    check(machine,"su root\n{delay}Root-Test-42\n{delay}poweroff","all filesystems unmounted")
    machine.wait_exit(15)
finally:
    machine.stop(); shutil.copyfile(machine.serial,keep / "persisted.log"); shutil.copyfile(machine.disk,disk); shutil.rmtree(machine.workdir)
for log in keep.glob("*.log"):
    content = log.read_text(errors="replace")
    for password in ("Root-Test-42","User-Test-42","Wrong-Test-42"):
        if password in content:
            print("FAIL password disclosed in " + log.name); failures += 1
print(f"security guest failures: {failures}",flush=True)
sys.exit(bool(failures))
