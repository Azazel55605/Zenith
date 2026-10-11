#!/usr/bin/env python3
"""S0 settings/session and P0 read-only package regressions on disposable guests."""
import importlib.util
from pathlib import Path
import shutil
import sys
import time

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("smoke", ROOT / "tools/smoke-test.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)
keep = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("/tmp/zenith-foundation")
keep.mkdir(parents=True, exist_ok=True)
disk = keep / "installed.img"
failures = checks = 0

def check(machine, command, expected):
    global failures, checks
    offset = len(machine.raw_log()); checks += 1
    machine.type(command + "\n")
    passed = machine.wait_for(expected, 180, offset=offset)
    print(("ok   " if passed else "FAIL ") + expected, flush=True)
    failures += not passed

def boot(label, initial=None):
    machine, _ = smoke.boot(ROOT / "output-x64/Zenith.iso", 600, label, initial)
    if machine is None: raise RuntimeError("boot failed: " + label)
    time.sleep(2)
    return machine

def finish(machine, label):
    machine.stop()
    shutil.copyfile(machine.serial, keep / (label + ".log"))
    shutil.copyfile(machine.disk, disk)
    shutil.rmtree(machine.workdir)

manifest = ("ZENITH-PACKAGE\\t1\\nname\\tp0-example\\nversion\\t1.0.0\\n"
            "architecture\\tany\\nkind\\tdata\\nruntime\\tbootstrap-1\\n"
            "requires\\tzenith-base\\t0.1\\nfile\\t/usr/share/doc/p0-example.txt\\t0644\\t6\\t"
            "5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03\\n")
machine = boot("settings/package live")
try:
    check(machine, "settings show | logger", "scope: system; storage: live (volatile)")
    check(machine, "session status | logger", "active=de (saved default)")
    check(machine, "session set-default headless; echo unavailable=$? | logger", "user: unavailable=1")
    check(machine, "settings set keyboard uk; settings show | logger", "keyboard=gb (active gb)")
    # Restore with letters/punctuation shared by US/GB before injecting a US pipe key.
    check(machine, "settings set keyboard us; logger keymap-restored", "user: keymap-restored")
    check(machine, "settings set timezone Europe/Berlin; settings show | logger", "timezone=Europe/Berlin (active Europe/Berlin)")
    check(machine, "settings set timezone unknown; echo invalid=$? | logger", "user: invalid=1")
    check(machine, "printf 'SESSION=de\\nEXTRA=keep\\n' > /etc/zenith/session.conf; session set-default de; cat /etc/zenith/session.conf | grep VERSION | logger", "user: VERSION=1")
    check(machine, "cat /etc/zenith/session.conf | grep EXTRA | logger", "user: EXTRA=keep")
    check(machine, "pkg list | grep zenith-desktop-test | logger", "kernel-linked test DE; reboot")
    check(machine, "pkg inspect /dev/zero; echo device-manifest=$? | logger", "user: device-manifest=1")
    check(machine, "mkdir -p /tmp/p0/payload/usr/share/doc; printf 'hello\\n' > /tmp/p0/payload/usr/share/doc/p0-example.txt; printf '" + manifest + "' > /tmp/p0/manifest.tsv; pkg inspect /tmp/p0/manifest.tsv | logger", "experimental data package p0-example 1.0.0 arch=any")
    check(machine, "pkg plan /tmp/p0/manifest.tsv | logger", "create /usr/share/doc/p0-example.txt mode=0644 bytes=6")
    check(machine, "test ! -e /usr/share/doc/p0-example.txt && logger plan-no-write", "user: plan-no-write")
    check(machine, "echo local > /usr/share/doc/p0-example.txt; pkg plan /tmp/p0/manifest.tsv; echo conflict=$? | logger", "user: conflict=1")
    check(machine, "cat /usr/share/doc/p0-example.txt | logger", "user: local")
    check(machine, "printf 'HELLO\\n' > /tmp/p0/payload/usr/share/doc/p0-example.txt; pkg plan /tmp/p0/manifest.tsv; echo tamper=$? | logger", "user: tamper=1")
    check(machine, "install sata0 --yes && logger foundation-installed", "user: foundation-installed")
    check(machine, "poweroff", "all filesystems unmounted")
    if not machine.wait_exit(15): raise RuntimeError("guest did not shut down")
finally: finish(machine, "live")

machine = boot("settings persisted", disk)
try:
    check(machine, "settings show | logger", "scope: system; storage: installed")
    check(machine, "settings show | logger", "timezone=Europe/Berlin (active Europe/Berlin)")
    check(machine, "session status | logger", "default=de")
    check(machine, "sh -c 'su user; settings set timezone UTC; echo setting-denied=$?' | logger", "user: setting-denied=1")
    check(machine, "sh -c 'su user; session set-default de; echo session-denied=$?' | logger", "user: session-denied=1")
    check(machine, "settings show | logger", "timezone=Europe/Berlin (active Europe/Berlin)")
    check(machine, "printf 'VERSION=2\\nSESSION=wm\\n' > /etc/zenith/session.conf; poweroff", "all filesystems unmounted")
    machine.wait_exit(15)
finally: finish(machine, "persisted")

machine = boot("future settings schema", disk)
try:
    check(machine, "session status | logger", "active=de (saved default)")
    check(machine, "session set-default de; echo future=$? | logger", "user: future=1")
    check(machine, "cat /etc/zenith/session.conf | grep VERSION | logger", "user: VERSION=2")
    check(machine, "printf 'VERSION=1\\nSESSION=headless\\n' > /etc/zenith/session.conf; poweroff", "all filesystems unmounted")
    machine.wait_exit(15)
finally: finish(machine, "future-schema")

machine = boot("unavailable saved session", disk)
try:
    check(machine, "session status | logger", "active=de (capability fallback)")
    check(machine, "session status | logger", "default=headless")
    check(machine, "printf 'VERSION=1\\nSESSION=de\\n' > /etc/zenith/session.conf; poweroff", "all filesystems unmounted")
    machine.wait_exit(15)
finally: finish(machine, "unavailable")

print(f"{checks-failures}/{checks} foundation checks passed", flush=True)
sys.exit(bool(failures))
