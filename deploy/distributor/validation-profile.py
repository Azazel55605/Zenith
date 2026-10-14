"""Secret-free per-VM timing and actual accelerator diagnostics for tagged harnesses."""
import json
from pathlib import Path
import re
import time

STATUS = re.compile(r'(?:user: )?([A-Z][A-Za-z0-9_-]{0,63})=')


def label(marker):
    match = STATUS.match(marker)
    if match: return match[1]
    if marker == 'headless ready': return 'boot-ready'
    if marker.startswith('network: online address='): return 'dhcp-ready'
    if marker == 'all filesystems unmounted': return 'shutdown'
    if 'password' in marker.lower(): return 'password-prompt'
    if marker.startswith('authenticated as '): return 'authentication'
    return 'guest-check'


def acceleration(machine):
    try:
        descriptors = Path('/proc') / str(machine.process.pid) / 'fd'
        for item in descriptors.iterdir():
            try: target = str(item.readlink())
            except OSError: continue
            if 'kvm-vcpu' in target: return 'kvm'
    except (OSError, AttributeError): pass
    try:
        text = (machine.directory / 'qemu.log').read_text(errors='replace')
        if 'falling back to tcg' in text: return 'tcg'
    except (OSError, AttributeError): pass
    return 'unknown'


def size(machine):
    try: return machine.serial.stat().st_size
    except (OSError, AttributeError): return 0


def install(machine_type):
    original_wait = machine_type.wait
    def wait(machine, marker, offset=0, timeout=180):
        # Minimal synthetic helpers in controller tests have no VM directory.
        if not isinstance(getattr(machine, 'directory', None), Path):
            return original_wait(machine, marker, offset, timeout)
        started = time.monotonic(); before = size(machine); outcome = 'error'
        try:
            result = original_wait(machine, marker, offset, timeout)
            outcome = 'passed' if result else 'failed'
            return result
        finally:
            row = dict(kind='wait', label=label(marker), seconds=round(time.monotonic()-started, 3),
                       outcome=outcome, timeout=timeout, serial_bytes=max(0,size(machine)-before),
                       accelerator=acceleration(machine))
            try:
                with (machine.directory/'wait-times.jsonl').open('a') as stream:
                    stream.write(json.dumps(row)+'\n')
            except OSError: pass  # Diagnostics cannot change acceptance.
            print('[validation] '+machine.directory.name+' '+row['label']+': '+str(row['seconds'])+
                  's '+outcome+'; serial='+str(row['serial_bytes'])+'B; accelerator='+row['accelerator'],flush=True)
    machine_type.wait = wait
