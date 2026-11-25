#!/usr/bin/env python3
"""Build a freestanding x86_64 ELF program without the kernel runtime."""
from pathlib import Path
import subprocess
import argparse,importlib.util
import os
import shutil
import re
import sys
import tempfile
ROOT=Path(__file__).resolve().parents[1]
def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('destination',type=Path);parser.add_argument('--version',default='0.1.0');parser.add_argument('--program',choices=('sunbeam','base-tools','fs-tools','net-tools','gui-probe','ui-gallery','settings','desktop-tools','polaris','observatory','equinox','horizon','aurora','audio-probe','beacon','orbit','quill','doom'),default='sunbeam');args=parser.parse_args()
    if not re.fullmatch(r'(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*))?',args.version):raise ValueError('invalid program version')
    destination=args.destination
    output=destination/('usr/share/zenith/native/'+args.program+'.elf');output.parent.mkdir(parents=True,exist_ok=True)
    source=ROOT/'userland/sunbeam'
    compiler=os.environ.get('ZENITH_USER_CC') or ('gcc' if shutil.which('gcc') else 'clang')
    target=['--target=x86_64-unknown-none','-fuse-ld=lld','-Wno-unused-command-line-argument'] if 'clang' in Path(compiler).name else []
    with tempfile.TemporaryDirectory(prefix='zenith-userland-') as temporary:
        extra=[]
        if args.program in ('ui-gallery','settings','desktop-tools','polaris','observatory','equinox','horizon','aurora','beacon','orbit','doom'):
            atlas=Path(temporary)/'ui-fonts.c'
            subprocess.run([sys.executable,str(ROOT/'tools/build-ui-fonts.py'),str(atlas),*(['--mono'] if args.program in ('polaris','equinox','aurora') else [])],check=True)
            extra=['-I'+str(ROOT/'userland/ui'),str(ROOT/'userland/ui/ui.c'),str(ROOT/'userland/ui/widgets.c'),
                   *([str(ROOT/'userland/polaris/term.c')] if args.program=='polaris' else [] if args.program=='aurora' else [str(ROOT/'userland/ui-gallery/gallery.c')] if args.program=='ui-gallery' else [str(ROOT/'userland/settings/app.c'),str(ROOT/'userland/settings/navigation.c'),str(ROOT/'userland/settings/bindings.c'),str(ROOT/'userland/settings/styles.c')] if args.program=='settings' else []),str(ROOT/'userland/ui/runtime.c'),str(ROOT/'userland/ui/text.c'),str(atlas)]
            if args.program=='settings':
                pages=Path(temporary)/'meridian-pages.c'
                subprocess.run([sys.executable,str(ROOT/'tools/build-meridian-pages.py'),str(pages)],check=True)
                extra+=['-I'+str(ROOT/'userland/settings'),str(ROOT/'userland/settings/meridian.c'),str(ROOT/'userland/settings/desktop_settings.c'),str(pages)]
        if args.program=='quill':
            extra.append(str(ROOT/'userland/aurora/buffer.c'))
        common=['-fcf-protection=none','-fno-stack-protector','-fno-builtin','-fno-unwind-tables','-fno-asynchronous-unwind-tables','-mno-red-zone',
            '-mno-sse','-mno-sse2','-mno-mmx','-Wall','-Wextra','-Werror']
        if args.program=='doom':
            engine=Path(temporary)/'doom-engine.o'
            # The upstream engine uses pre-C23 declarations and its own true/false enum.
            subprocess.run([compiler,*target,'-c','-fno-pie',*common[:-3],'-std=gnu11','-w','-O2',
                '-ffile-prefix-map='+str(ROOT)+'=.',
                str(ROOT/'userland/doom/engine.c'),'-o',str(engine)],check=True)
            extra.extend(('-std=gnu11',str(engine),str(ROOT/'userland/doom/launcher.c')))
        if args.program=='aurora':
            # Aurora's own code is built for size (the toolkit stays -O2 for drawing speed) and unused toolkit code is
            # dropped, so the editor keeps room under the package file limit. No libc: GCC must not turn loops into
            # strlen/memset calls.
            size=['-ffunction-sections','-fdata-sections','-fno-tree-loop-distribute-patterns']
            for name in ('editor','buffer','regex'):
                obj=Path(temporary)/(name+'.o')
                subprocess.run([compiler,*target,'-c','-fno-pie',*common,'-Os',*size,'-I'+str(ROOT/'userland/ui'),str(ROOT/'userland/aurora'/(name+'.c')),'-o',str(obj)],check=True)
                extra.append(str(obj))
            extra+=[*size,'-Wl,--gc-sections']
        main_source=source/'main.c' if args.program in ('sunbeam','base-tools','fs-tools') else ROOT/'userland'/args.program/'main.c'
        subprocess.run([compiler,*target,'-nostdlib','-static','-fno-pie','-no-pie',*common,'-O2',
            '-DSUNBEAM_VERSION="'+args.version+'"','-Wl,-T,'+str(source/'link.ld'),'-Wl,--build-id=none',str(source/'start.s'),str(main_source),*extra,'-o',str(output)],check=True)
    if args.program=='doom':
        apps=destination/'usr/share/zenith/apps'
        apps.mkdir(parents=True,exist_ok=True)
        shutil.copyfile(ROOT/'userland/doom/doom.app',apps/'doom.app')
        source_dir=destination/'usr/share/zenith/doom/source'
        for name in ('userland/doom/main.c','userland/doom/engine.c','userland/doom/engine.h','userland/doom/UPSTREAM.md','userland/doom/launcher.c',
                     'userland/doom/launcher.h','userland/doom/doom.app',
                     'userland/doom/upstream/PureDOOM.h','userland/doom/upstream/LICENSE',
                     'userland/include/zenith_graphics.h','userland/sunbeam/zenith.h',
                     'userland/sunbeam/start.s','userland/sunbeam/link.ld','tools/build-userland.py',
                     'tools/build-ui-fonts.py','Resources/Fonts/Inter-Regular.ttf',
                     'Resources/Fonts/Inter-SemiBold.ttf','Resources/Fonts/LICENSE-Inter.txt',
                     'userland/include/zenith_files.h','userland/include/zenith_settings.h',
                     'userland/include/zenith_style.h','userland/include/zenith_system_settings.h',
                     *(f'userland/ui/{file}' for file in ('runtime.c','ui.c','ui.h','widgets.c','widgets.h','text.c','text.h'))):
            target_path=source_dir/name
            target_path.parent.mkdir(parents=True,exist_ok=True)
            shutil.copyfile(ROOT/name,target_path)
    if args.program=='beacon':
        # Beacon's own icons beside the shared ones (zenith-icons is at the package file limit).
        spec=importlib.util.spec_from_file_location('ui_icons',ROOT/'tools/build-ui-icons.py');icons=importlib.util.module_from_spec(spec);spec.loader.exec_module(icons)
        icon_dir=destination/icons.ICON_DIR;icon_dir.mkdir(parents=True,exist_ok=True)
        for svg in sorted((ROOT/'userland/beacon/icons').glob('*.svg')):(icon_dir/(svg.stem+'.zic')).write_bytes(icons.icon_file(svg.read_text()))
        (destination/'usr/share/zenith/beacon').mkdir(parents=True,exist_ok=True)
        shutil.copyfile(ROOT/'userland/beacon/icons/LICENSE',destination/'usr/share/zenith/beacon/LICENSE-Lucide.txt')
    if args.program in ('ui-gallery','settings','desktop-tools','polaris','observatory','equinox','horizon','aurora','beacon','orbit'):
        license_dir=destination/('usr/share/zenith/'+args.program);license_dir.mkdir(parents=True,exist_ok=True)
        shutil.copyfile(ROOT/'Resources/Fonts/LICENSE-Inter.txt',license_dir/'LICENSE-Inter.txt')
        if args.program in ('polaris','equinox','aurora'): shutil.copyfile(ROOT/'Resources/Fonts/LICENSE-Hack.txt',license_dir/'LICENSE-Hack.txt')
    print('Built standalone native program: '+str(output))
if __name__=='__main__':main()
