#!/usr/bin/env python3
"""Build the allocation-free HDA transport; reject accidental runtime imports."""
from pathlib import Path
import subprocess, sys, tempfile
ROOT=Path(__file__).resolve().parents[1]
def build(out):
    out.mkdir(parents=True,exist_ok=True)
    source=ROOT/'src/Native/Audio/hda.c';target=out/'audio-hda.o'
    if not target.exists() or target.stat().st_mtime<max(source.stat().st_mtime,Path(__file__).stat().st_mtime):
        with tempfile.NamedTemporaryFile(dir=out,prefix='.audio-',delete=False) as f: temporary=Path(f.name)
        try:
            subprocess.run(['clang','--target=x86_64-none-elf','-O2','-std=c11','-ffreestanding','-fno-builtin','-fno-stack-protector','-mno-red-zone','-mgeneral-regs-only','-mcmodel=kernel','-fno-pic','-Wall','-Wextra','-Werror','-c',str(source),'-o',str(temporary)],check=True)
            if subprocess.check_output(['nm','-u',str(temporary)],text=True).strip():raise RuntimeError('HDA transport has unexpected imports')
            temporary.replace(target)
        finally:temporary.unlink(missing_ok=True)
    if subprocess.check_output(['nm','-u',str(target)],text=True).strip():raise RuntimeError('HDA transport has unexpected imports')
if __name__=='__main__':build(Path(sys.argv[1]))
