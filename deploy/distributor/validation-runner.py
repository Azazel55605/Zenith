#!/usr/bin/env python3
"""Run an unchanged tagged acceptance harness with efficient independent image copies."""
from pathlib import Path
import runpy
import importlib.machinery
import importlib.util
import hashlib
import os
import shutil
import subprocess
import sys
import time

original_copyfile = shutil.copyfile


def copyfile(source, destination, *, follow_symlinks=True):
    src, dst = Path(source), Path(destination)
    if follow_symlinks and src.suffix == ".img" and src.is_file() and not src.is_symlink() and not dst.is_symlink():
        if dst.exists() and (not dst.is_file() or src.samefile(dst)):
            return original_copyfile(source, destination)
        cp = shutil.which("cp")
        if cp:
            # Match copyfile's creation mode/umask, including read-only sources.
            with src.open("rb"), dst.open("wb"): pass
            try:
                result = subprocess.run([cp, "--reflink=auto", "--sparse=always", "--", str(src), str(dst)],
                                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                if result.returncode == 0: return destination
            except OSError: pass
    return original_copyfile(source, destination, follow_symlinks=follow_symlinks)


def run_harness(script):
    spec = importlib.util.spec_from_file_location('validation_accelerator', Path(__file__).with_name('validation-accelerator.py'))
    accelerator = importlib.util.module_from_spec(spec); spec.loader.exec_module(accelerator)
    spec = importlib.util.spec_from_file_location('validation_network', Path(__file__).with_name('validation-network.py'))
    network = importlib.util.module_from_spec(spec); spec.loader.exec_module(network)
    spec = importlib.util.spec_from_file_location('validation_status', Path(__file__).with_name('validation-status.py'))
    status = importlib.util.module_from_spec(spec); spec.loader.exec_module(status)
    spec = importlib.util.spec_from_file_location('validation_profile', Path(__file__).with_name('validation-profile.py'))
    profile = importlib.util.module_from_spec(spec); spec.loader.exec_module(profile)
    spec = importlib.util.spec_from_file_location('guest_auth_controller', Path(__file__).resolve().parents[2] / 'tools/guest_auth.py')
    authentication = importlib.util.module_from_spec(spec); spec.loader.exec_module(authentication)
    spec = importlib.util.spec_from_file_location('validation_input', Path(__file__).with_name('validation-input.py'))
    input_profile = importlib.util.module_from_spec(spec); spec.loader.exec_module(input_profile)
    input_profile.DIAGNOSTIC_ELF = script.parent.parent / 'output-x64/Zenith.elf'
    original_run = subprocess.run
    prebuilt = os.environ.get('ZENITH_NATIVE_PREBUILT_SHA256')
    root = os.environ.get('ZENITH_NATIVE_PREBUILT_ROOT')
    def run(command, *args, **kwargs):
        if prebuilt and root and isinstance(command,(list,tuple)) and len(command) in (5,6) and list(map(str,command[:2]))==['dotnet','build'] and list(map(str,command[3:5]))==['-c','Release'] and (len(command)==5 or str(command[5])=='--nologo'):
            target=Path(command[2]).resolve();expected=Path(root)/'tools/Zenith.PackageTool'
            if target==expected or target==expected/'Zenith.PackageTool.csproj':
                binary=expected/'bin/Release/net10.0/Zenith.PackageTool.dll'
                if hashlib.sha256(binary.read_bytes()).hexdigest()!=prebuilt:raise ValueError('prebuilt PackageTool changed before native gate')
                print('Reusing hash-verified prebuilt PackageTool',flush=True)
                empty='' if kwargs.get('text') or kwargs.get('encoding') else b''
                return subprocess.CompletedProcess(command,0,empty if kwargs.get('capture_output') else None,empty if kwargs.get('capture_output') else None)
        return original_run(command,*args,**kwargs)
    subprocess.run = run
    original_exec = importlib.machinery.SourceFileLoader.exec_module
    instances = []
    headless = script.parent / 'headless-smoke.py'
    def execute(loader, module):
        original_exec(loader, module)
        if script.name in ('native-settings-smoke.py','native-materials-smoke.py','ui-gallery-smoke.py') and Path(loader.path).name == 'login-session-smoke.py':
            spec = importlib.util.spec_from_file_location('validation_terminal', Path(__file__).with_name('validation-terminal.py'))
            terminal = importlib.util.module_from_spec(spec); spec.loader.exec_module(terminal)
            terminal.install(module, instances, input_profile,
                             protocol='settings' if script.name == 'native-settings-smoke.py' else 'gallery')
        if Path(loader.path).name == 'validation-input.py':
            # Immutable tags keep their assertions and binaries. Input delivery is
            # controller policy, just like authentication and network diagnostics.
            # Some tagged harnesses import their own helper explicitly.
            module.RenderedMouse = input_profile.RenderedMouse
            module.install_monitor = input_profile.install_monitor
            module.install = input_profile.install
            print('Validation input policy: controller observed-motion-v3 (read-only failure capture)', flush=True)
        if script.name == 'native-graphics-smoke.py' and Path(loader.path).name == 'recovery_console.py':
            input_profile.install_monitor(module)
        if Path(loader.path).resolve() == headless.resolve():
            accelerator.install(module)
            network.install(module.Machine, instances)
            status.install(module.Machine)
            authentication.install(module.Machine)
            if script.name == 'native-graphics-smoke.py': input_profile.install(module.Machine)
            profile.install(module.Machine, elf=module.ROOT / 'output-x64/Zenith.elf' if hasattr(module, 'ROOT') else None, harness=script.name)
    importlib.machinery.SourceFileLoader.exec_module = execute
    if script.name == 'native-graphics-smoke.py' and 'recovery_console' in sys.modules:
        input_profile.install_monitor(sys.modules['recovery_console'])
    started = time.monotonic()
    try: runpy.run_path(str(script), run_name='__main__')
    finally:
        importlib.machinery.SourceFileLoader.exec_module = original_exec
        subprocess.run = original_run
        # A failed boot can raise before its harness assigns the machine to the
        # outer cleanup variable. Reap that VM before retaining/removing evidence.
        for machine in instances:
            try: machine.stop()
            except (OSError, RuntimeError): pass
        print('[validation] harness wall time: '+str(round(time.monotonic()-started, 3))+'s', flush=True)


def main():
    if len(sys.argv) < 3: raise SystemExit("usage: validation-runner.py HARNESS KEEP [HARNESS_ARGS...]")
    script = Path(sys.argv[1]).resolve(); keep = sys.argv[2]
    sys.path.insert(0, str(script.parent))
    sys.argv = [str(script), keep, *sys.argv[3:]]
    shutil.copyfile = copyfile
    try: run_harness(script)
    finally: shutil.copyfile = original_copyfile


if __name__ == "__main__": main()
