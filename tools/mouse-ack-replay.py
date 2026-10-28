#!/usr/bin/env python3
"""No-build mouse acknowledgement stress/fault test on a copied disposable fixture."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import time
import boot_image

ROOT = Path(__file__).resolve().parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    return module


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--disk', type=Path, required=True, help='disposable installed fixture; original preserved')
    parser.add_argument('--elf', type=Path, required=True, help='exact ELF to verify against the fixture')
    parser.add_argument('--keep', type=Path, required=True, help='new short evidence directory')
    parser.add_argument('--cycles', type=int, default=100)
    parser.add_argument('--omit-return-motion', action='store_true', help='deliberately drop one synthetic return movement')
    args = parser.parse_args()
    if not 1 <= args.cycles <= 1000: parser.error('--cycles must be 1..1000')
    keep = args.keep.resolve()
    if len(str(keep/'desktop/serial.sock').encode()) > 100: parser.error('--keep path too long; use a short directory')
    boot_image.verify(args.disk, args.elf)
    keep.mkdir(parents=True, exist_ok=False)
    h = load('ack_headless', ROOT/'tools/headless-smoke.py')
    policy = load('ack_policy', ROOT/'deploy/distributor/validation-input.py')
    load('ack_accelerator', ROOT/'deploy/distributor/validation-accelerator.py').install(h)
    policy.install(h.Machine)
    monitor = keep/'monitor.sock'; buttons = []; omitted = []

    def transport(path, text):
        if text.startswith('mouse_button '): buttons.append(text)
        if args.omit_return_motion and text == 'mouse_move -1 0' and not omitted:
            omitted.append(text); return ''
        return policy.monitor_text(path, text)

    mouse = policy.RenderedMouse(monitor, transport); machine = None
    try:
        machine = h.Machine(keep/'desktop', args.disk, 'session=de',
                            network=['-device', 'virtio-gpu-pci', '-device', 'virtio-keyboard-pci'], monitor=monitor)
        if not machine.wait('desktop ready (installed)', timeout=180): raise RuntimeError('desktop not ready')
        # The readiness log precedes the first framebuffer. This is observation,
        # never input or button replay.
        deadline = time.monotonic()+10
        while True:
            try: (x,y),(width,height) = mouse.position(); break
            except RuntimeError:
                if time.monotonic() >= deadline: raise
                time.sleep(.05)
        target = (width-40, 40)
        mouse.command('mouse_move '+str(target[0]-x)+' '+str(target[1]-y))
        for i in range(args.cycles):
            mouse.command('mouse_button 1'); mouse.command('mouse_button 0')
            if mouse.position()[0] != target: raise RuntimeError('cursor not restored exactly')
            if (i+1)%25 == 0: print('PASS exact button cycles: '+str(i+1), flush=True)
        if len(buttons) != args.cycles*2: raise RuntimeError('button edge replay')
        if args.omit_return_motion and len(omitted) != 1: raise RuntimeError('fault was not injected')
        rows = (keep/'input-motion.jsonl').read_text().splitlines() if (keep/'input-motion.jsonl').exists() else []
        report = dict(cycles=args.cycles, button_commands=len(buttons), omitted_movements=len(omitted),
                      corrections=len(rows), final_position=target, elf_sha256=hashlib.sha256(args.elf.read_bytes()).hexdigest())
        (keep/'result.json').write_text(json.dumps(report, indent=2)+'\n')
        print('PASS mouse acknowledgement replay: '+json.dumps(report), flush=True)
    finally:
        if machine is not None: machine.stop()


if __name__ == '__main__': main()
