#!/usr/bin/env python3
"""Build a freestanding x86_64 ELF text tool without the kernel runtime."""
from pathlib import Path
import subprocess
import argparse
import os
import shutil
import re
import sys
ROOT=Path(__file__).resolve().parents[1]
def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('destination',type=Path);parser.add_argument('--version',default='0.1.0');parser.add_argument('--program',choices=('sunbeam','base-tools','fs-tools'),default='sunbeam');args=parser.parse_args()
    if not re.fullmatch(r'(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*))?',args.version):raise ValueError('invalid program version')
    destination=args.destination
    output=destination/('usr/share/zenith/native/'+args.program+'.elf');output.parent.mkdir(parents=True,exist_ok=True)
    source=ROOT/'userland/sunbeam'
    compiler=os.environ.get('ZENITH_USER_CC') or ('gcc' if shutil.which('gcc') else 'clang')
    target=['--target=x86_64-unknown-none','-fuse-ld=lld','-Wno-unused-command-line-argument'] if 'clang' in Path(compiler).name else []
    subprocess.run([compiler,*target,'-nostdlib','-static','-fno-pie','-no-pie',
        '-fcf-protection=none','-fno-stack-protector','-fno-builtin','-fno-unwind-tables','-fno-asynchronous-unwind-tables','-mno-red-zone',
        '-mno-sse','-mno-sse2','-mno-mmx','-O2','-Wall','-Wextra','-Werror',
        '-DSUNBEAM_VERSION="'+args.version+'"','-Wl,-T,'+str(source/'link.ld'),'-Wl,--build-id=none',str(source/'start.s'),str(source/'main.c'),'-o',str(output)],check=True)
    print('Built standalone native text tool: '+str(output))
if __name__=='__main__':main()
