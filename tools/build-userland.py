#!/usr/bin/env python3
"""Build a freestanding x86_64 ELF program without the kernel runtime."""
from pathlib import Path
import subprocess
import argparse
import os
import shutil
import re
import sys
import tempfile
ROOT=Path(__file__).resolve().parents[1]
def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('destination',type=Path);parser.add_argument('--version',default='0.1.0');parser.add_argument('--program',choices=('sunbeam','base-tools','fs-tools','net-tools','gui-probe','ui-gallery','settings'),default='sunbeam');args=parser.parse_args()
    if not re.fullmatch(r'(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*))?',args.version):raise ValueError('invalid program version')
    destination=args.destination
    output=destination/('usr/share/zenith/native/'+args.program+'.elf');output.parent.mkdir(parents=True,exist_ok=True)
    source=ROOT/'userland/sunbeam'
    compiler=os.environ.get('ZENITH_USER_CC') or ('gcc' if shutil.which('gcc') else 'clang')
    target=['--target=x86_64-unknown-none','-fuse-ld=lld','-Wno-unused-command-line-argument'] if 'clang' in Path(compiler).name else []
    with tempfile.TemporaryDirectory(prefix='zenith-userland-') as temporary:
        extra=[]
        if args.program in ('ui-gallery','settings'):
            atlas=Path(temporary)/'ui-fonts.c'
            subprocess.run([sys.executable,str(ROOT/'tools/build-ui-fonts.py'),str(atlas)],check=True)
            extra=['-I'+str(ROOT/'userland/ui'),str(ROOT/'userland/ui/ui.c'),
                   str(ROOT/('userland/ui-gallery/gallery.c' if args.program=='ui-gallery' else 'userland/settings/app.c')),str(ROOT/'userland/ui/runtime.c'),str(atlas)]
        subprocess.run([compiler,*target,'-nostdlib','-static','-fno-pie','-no-pie',
            '-fcf-protection=none','-fno-stack-protector','-fno-builtin','-fno-unwind-tables','-fno-asynchronous-unwind-tables','-mno-red-zone',
            '-mno-sse','-mno-sse2','-mno-mmx','-O2','-Wall','-Wextra','-Werror',
            '-DSUNBEAM_VERSION="'+args.version+'"','-Wl,-T,'+str(source/'link.ld'),'-Wl,--build-id=none',str(source/'start.s'),str(ROOT/('userland/'+args.program+'/main.c') if args.program in ('net-tools','gui-probe','ui-gallery','settings') else source/'main.c'),*extra,'-o',str(output)],check=True)
    if args.program in ('ui-gallery','settings'):
        license_dir=destination/('usr/share/zenith/'+args.program);license_dir.mkdir(parents=True,exist_ok=True)
        shutil.copyfile(ROOT/'Resources/Fonts/LICENSE-Inter.txt',license_dir/'LICENSE-Inter.txt')
    print('Built standalone native program: '+str(output))
if __name__=='__main__':main()
