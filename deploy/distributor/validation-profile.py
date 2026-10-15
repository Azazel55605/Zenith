"""Secret-free per-VM timing and actual accelerator diagnostics for tagged harnesses."""
import json
import os
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
    # A forced, single TCG backend cannot have fallen back to another backend.
    # QEMU keeps quiet about successful TCG initialization, unlike KVM failures.
    try:
        args = machine.process.args
        if isinstance(args, (list, tuple)):
            backends = [args[i+1] for i, value in enumerate(args[:-1]) if value == '-accel']
            if backends == ['tcg'] and machine.process.poll() is None: return 'tcg'
    except AttributeError: pass
    return 'unknown'


def size(machine):
    try: return machine.serial.stat().st_size
    except (OSError, AttributeError): return 0


def resources(machine):
    try:
        process = Path('/proc') / str(machine.process.pid)
        fields = (process/'stat').read_text().rsplit(')', 1)[1].split()
        io = dict(line.split(':', 1) for line in (process/'io').read_text().splitlines())
        return dict(cpu_seconds=(int(fields[11])+int(fields[12]))/os.sysconf('SC_CLK_TCK'),
                    read_bytes=int(io['read_bytes']), write_bytes=int(io['write_bytes']))
    except (OSError, AttributeError, ValueError, KeyError, IndexError): return {}


def install(machine_type):
    original_wait = machine_type.wait
    def wait(machine, marker, offset=0, timeout=180):
        # Minimal synthetic helpers in controller tests have no VM directory.
        if not isinstance(getattr(machine, 'directory', None), Path):
            return original_wait(machine, marker, offset, timeout)
        started = time.monotonic(); before = size(machine); used = resources(machine); outcome = 'error'
        try:
            result = original_wait(machine, marker, offset, timeout)
            outcome = 'passed' if result else 'failed'
            return result
        finally:
            row = dict(kind='wait', label=label(marker), seconds=round(time.monotonic()-started, 3),
                       outcome=outcome, timeout=timeout, serial_bytes=max(0,size(machine)-before),
                       accelerator=acceleration(machine))
            budget = getattr(machine, '_validation_budget', None)
            if isinstance(budget, dict):
                row['budget'] = budget
            failure = getattr(machine, '_validation_failure', None)
            if outcome == 'failed' and isinstance(failure, dict):
                row['failure'] = failure
            after = resources(machine)
            if used and after:
                row['qemu_cpu_seconds'] = round(max(0, after['cpu_seconds']-used['cpu_seconds']), 3)
                row['host_read_bytes'] = max(0, after['read_bytes']-used['read_bytes'])
                row['host_write_bytes'] = max(0, after['write_bytes']-used['write_bytes'])
            try:
                with (machine.directory/'wait-times.jsonl').open('a') as stream:
                    stream.write(json.dumps(row)+'\n')
            except OSError: pass  # Diagnostics cannot change acceptance.
            print('[validation] '+machine.directory.name+' '+row['label']+': '+str(row['seconds'])+
                  's '+outcome+'; serial='+str(row['serial_bytes'])+'B; accelerator='+row['accelerator']+
                  ('; QEMU CPU='+str(row['qemu_cpu_seconds'])+'s' if 'qemu_cpu_seconds' in row else ''),flush=True)
    machine_type.wait = wait
