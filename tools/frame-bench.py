#!/usr/bin/env python3
"""Desktop frame rate on the VMware SVGA II path in a disposable VM.

Runs the compositor's window-animation test (`fps motion`) at each display mode, with one
window and then with two, and prints frames, elapsed time, achieved frames per second and
the worst frame. The rate includes everything a person sees: compose, present and the
loop's waits between frames.
"""
from pathlib import Path
import argparse
import importlib.util
import os
import re
import time
ROOT = Path(__file__).resolve().parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('keep', type=Path)
    parser.add_argument('--mode', action='append', default=None, help='WIDTHxHEIGHTxSCALE, repeatable')
    args = parser.parse_args()
    args.keep.mkdir(parents=True, exist_ok=False)
    os.environ['ZENITH_SMOKE_VGA'] = 'vmware'
    smoke = load('frame_smoke', ROOT / 'tools/smoke-test.py')
    load('frame_accel', ROOT / 'deploy/distributor/validation-accelerator.py').install(smoke)
    machine, _ = smoke.boot(ROOT / 'output-x64/Zenith.iso', 180, 'frame bench')
    if not machine:
        raise RuntimeError('desktop boot')

    def motion(label):
        rates = []
        for _ in range(3):
            offset = len(machine.raw_log())
            machine.type('fps motion\n')
            assert machine.wait_for('motion: test frames=', 30, offset), 'motion result'
            line = [l for l in machine.raw_log()[offset:].splitlines() if 'motion: test frames=' in l][-1]
            values = dict(re.findall(r'(\w+)=(-?\d+)', line))
            frames, elapsed, worst = int(values['frames']), int(values['elapsed']), int(values['worst'])
            rates.append((frames * 1e6 / max(1, elapsed), worst / 1000))
            time.sleep(1.5)
        best = max(rates)
        print(f'{label}: {best[0]:.0f} fps (runs: ' + ', '.join(f'{r:.0f}' for r, _ in rates) + f'), worst frame {best[1]:.1f} ms', flush=True)
        offset = len(machine.raw_log())
        machine.type('fps bench\n')
        assert machine.wait_for('bench windows=', 60, offset), 'bench result'
        bench = [l for l in machine.raw_log()[offset:].splitlines() if 'bench windows=' in l][-1]
        print('   compose ' + bench.split('compose: ')[-1], flush=True)
        offset = len(machine.raw_log())
        machine.type('fps | logger\n')
        machine.wait_for('last compose', 20, offset); time.sleep(1)
        for l in machine.raw_log()[offset:].splitlines():
            if 'user: last compose' in l or 'user: frames presented' in l: print('   ' + l.split('user: ')[-1], flush=True)
        time.sleep(1)

    try:
        for mode in args.mode or ['1280x800x1']:
            width, height, scale = mode.split('x')
            if mode != '1280x800x1':
                offset = len(machine.raw_log())
                machine.type(f'display test {width} {height} {scale}\n')
                time.sleep(3)
                machine.type('display keep\n')
                time.sleep(3)
            motion(f'{mode} one window')
            machine.type('{ctrl-alt-t}')
            time.sleep(3)
            motion(f'{mode} two windows')
            machine.screenshot(args.keep / (mode + '.ppm'))
    finally:
        machine.stop()


if __name__ == '__main__':
    main()
