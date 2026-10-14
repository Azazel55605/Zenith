"""Explicit QEMU accelerator policy for unchanged tagged validation harnesses."""
import os
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


def install(module, policy=None):
    policy = policy or os.environ.get('ZENITH_VALIDATION_ACCELERATOR', 'auto')
    arguments(['qemu'], policy)  # Reject invalid policy before launching a VM.
    if not hasattr(module, 'subprocess'): return  # Minimal synthetic controller helpers.
    original = module.subprocess
    # Patch only the harness module's binding; build tools/firmware commands and
    # other modules retain the real subprocess module and their original argv.
    proxy = types.SimpleNamespace(**vars(original))
    def launch(args, *positional, **keywords):
        return original.Popen(arguments(args, policy), *positional, **keywords)
    proxy.Popen = launch
    module.subprocess = proxy
    print('Validation accelerator policy: '+policy, flush=True)
