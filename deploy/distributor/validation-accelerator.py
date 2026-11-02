"""Explicit QEMU accelerator policy for unchanged tagged validation harnesses."""
import os
import pathlib
import re
import shutil
import socket
import tempfile
import threading
import time
import types


def arguments(args, policy):
    if policy not in ('auto', 'kvm', 'tcg'):
        raise ValueError('ZENITH_VALIDATION_ACCELERATOR must be auto, kvm or tcg')
    if policy == 'auto': return args
    result = []
    i = 0
    while i < len(args):
        if args[i] == '-accel':
            if i + 1 >= len(args): raise ValueError('missing QEMU accelerator argument')
            i += 2
        else:
            result.append(args[i]); i += 1
    return [result[0], '-accel', policy, *result[1:]]


def steady_vcpu(path, running, interval=0.1):
    """Polls vCPU state over a private monitor until the VM stops.

    Without this, typed keys are lost under QEMU/KVM at about one long line in ten; with it, none were in
    1,470 lines. See the keyboard entry under W1 in docs/ROADMAP.md, whose cause is still open.
    tools/keyboard-loss-replay.py does not use this, so it keeps measuring that bug.
    Best effort: a monitor problem must never fail or stop a validation run.
    """
    connection = None
    try:
        while running():
            try:
                if connection is None:
                    connection = socket.socket(socket.AF_UNIX); connection.settimeout(2)
                    connection.connect(path); connection.recv(4096)
                connection.sendall(b'info registers\n'); received = b''
                while b'EFER' not in received:
                    data = connection.recv(65536)
                    if not data: raise OSError('monitor closed')
                    received += data
            except OSError:
                if connection is not None: connection.close()
                connection = None
            time.sleep(interval)
    finally:
        if connection is not None: connection.close()


def crash_report(directory):
    """The guest's own crash text from any serial log under a run's evidence, or None.

    A kernel that halts looks like a timeout to a harness waiting for output, so a failed run says so.
    """
    for path in sorted(pathlib.Path(directory).rglob('*serial*.log')):
        try: text = path.read_bytes().decode('utf-8', 'replace')
        except OSError: continue
        at = text.rfind('CPU EXCEPTION')
        if at >= 0 and 'System halted' in text[at:]:
            return str(path) + ':\n' + text[max(0, text.rfind('\n', 0, at)):at + 1400]
    return None


def is_vm(args):
    return bool(args) and re.search(r'qemu-system', str(args[0])) is not None


def install(module, policy=None):
    policy = policy or os.environ.get('ZENITH_VALIDATION_ACCELERATOR', 'auto')
    arguments(['qemu'], policy)  # Reject invalid policy before launching a VM.
    if not hasattr(module, 'subprocess'): return  # Minimal synthetic controller helpers.
    original = module.subprocess
    # Patch only the harness module's binding; build tools/firmware commands and
    # other modules retain the real subprocess module and their original argv.
    proxy = types.SimpleNamespace(**vars(original))
    def launch(args, *positional, **keywords):
        argv = arguments(args, policy)
        if not is_vm(argv): return original.Popen(argv, *positional, **keywords)
        directory = tempfile.mkdtemp(prefix='zvm-')
        path = os.path.join(directory, 'm')
        process = original.Popen([*argv, '-monitor', 'unix:' + path + ',server,nowait'], *positional, **keywords)
        def steady():
            steady_vcpu(path, lambda: process.poll() is None)
            shutil.rmtree(directory, ignore_errors=True)
        threading.Thread(target=steady, daemon=True).start()
        return process
    proxy.Popen = launch
    module.subprocess = proxy
    print('Validation accelerator policy: '+policy, flush=True)
