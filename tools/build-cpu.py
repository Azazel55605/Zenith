#!/usr/bin/env python3
"""Build the isolated x64 C1 runtime without host libraries or FP/SIMD code."""
import argparse
from pathlib import Path
import subprocess
import tempfile
ROOT=Path(__file__).resolve().parents[1]
ALLOWED={'zenith_cpu_ap_entry','zenith_cpu_mp_response','zenith_cpu_wake_entry',
         'zenith_cpu_spurious_entry','zenith_cpu_fault_entries','zenith_execution_test','zenith_cpu_reschedule_entry','zenith_cpu_timer_entry','zenith_gc_stop_entry', 'zenith_cpu_logical_id','zenith_gc_poll','zenith_cpu_gc_notify','zenith_gc_managed_enter','zenith_gc_managed_leave','zenith_gc_fail_stop','irq246_stub','zenith_cpu_completion_entry','zenith_cpu_ap_fail_stop'}

def check_imports(target):
    imports=subprocess.check_output(['nm','-u',str(target)],text=True)
    unknown={line.split()[-1] for line in imports.splitlines()}-ALLOWED
    if unknown:raise RuntimeError('Unexpected C1 runtime imports: '+repr(unknown))

def build_one(out,name):
    out.mkdir(parents=True,exist_ok=True)
    source=ROOT/('src/Native/Cpu/'+name+'.c')
    target=out/('cpu-'+name+'.o')
    if not target.exists() or target.stat().st_mtime<max(source.stat().st_mtime, (source.parent/"blur.h").stat().st_mtime, Path(__file__).stat().st_mtime):
        # A killed/failed compiler must not replace a known complete object with
        # a partial file that appears newer than its source on the next build.
        with tempfile.NamedTemporaryFile(prefix='.cpu-',suffix='.tmp',dir=out,delete=False) as f:
            temporary=Path(f.name)
        try:
            subprocess.run(['clang','--target=x86_64-none-elf','-O2','-std=c11','-ffreestanding','-fno-builtin',
                            '-fno-stack-protector','-mno-red-zone','-mgeneral-regs-only','-mcmodel=kernel',
                            '-fno-pic','-Wall','-Wextra','-Werror','-c',str(source),'-o',str(temporary)],check=True)
            check_imports(temporary)
            temporary.replace(target)
        finally:temporary.unlink(missing_ok=True)
    # Validate cached objects too; a rejected import can never become a cache hit.
    check_imports(target)

def build(out):
    build_one(out,'workers')
    build_one(out,'gc')

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('output',type=Path)
    build(p.parse_args().output)
