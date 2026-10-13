#!/usr/bin/env python3
"""German keyboard/terminal checks with real PS/2 and virtio key injection."""
import importlib.util
from pathlib import Path
import shutil
import sys
import tempfile
import time

spec = importlib.util.spec_from_file_location("desktop", Path(__file__).with_name("smoke-test.py"))
desktop = importlib.util.module_from_spec(spec)
spec.loader.exec_module(desktop)
root = Path(__file__).resolve().parent.parent
destination = Path(sys.argv[1]) if len(sys.argv) > 1 else root / "obj/keyboard-check"
destination.mkdir(parents=True, exist_ok=True)
checks = [
    ("loadkeys de", None),
    ("logger {shift-7}tmp", "user: /tmp"),
    ("{num_lock}logger {shift-7}root", "user: /root"),
    ("logger arrox{left}{delete}w", "user: arrow"),
    ("logger historz", "user: history"),
    ("draft{up}{down}{home}logger {end}", "user: draft"),
    ("{up}{end}recalled", "user: draftrecalled"),
    ("logger {alt_r-q}{alt_r-7}{alt_r-8}{alt_r-9}{alt_r-0}", "user: @{[]}"),
    ("logger {shift-backslash}{backslash}{less}{shift-less}{alt_r-less}{shift-backslash}", "user: #<>|"),
    ("logger {caps_lock}{shift-7}caps{caps_lock}", "user: /CAPS"),
    ("loadkezs us", None),
]
for backend in ("ps2", "virtio"):
    work = Path(tempfile.mkdtemp(prefix="zenith-keyboard-"))
    machine = desktop.Machine(root / "output-x64/Zenith.iso", work, keyboard=backend)
    try:
        if not machine.wait_for("kernel: desktop ready", 180):
            raise RuntimeError(backend + " failed to boot")
        time.sleep(2)
        for text, expected in checks:
            offset = len(machine.raw_log())
            machine.type(text + "\n")
            if expected and not machine.wait_for(expected, 30, offset):
                raise RuntimeError(backend + " failed: " + text + " expected " + expected)
            if expected: print("ok " + backend + " " + expected, flush=True)
            else: time.sleep(1)
        machine.screenshot(work / "keyboard.ppm")
    finally:
        machine.stop()
        shutil.copyfile(machine.serial, destination / (backend + ".log"))
        if (work / "keyboard.ppm").exists():
            shutil.copyfile(work / "keyboard.ppm", destination / (backend + ".ppm"))
        shutil.rmtree(work)
print("German keyboard checks passed on PS/2 and virtio", flush=True)
