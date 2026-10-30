#!/usr/bin/env python3
"""Host execution of the real client UI renderer and event state machine."""
from pathlib import Path
import re
import sys
import subprocess
import tempfile
import os
import shutil
import unittest
import importlib.util
ROOT = Path(__file__).resolve().parents[1]

class UiLibraryTests(unittest.TestCase):
    def test_ui_programs_fit_the_package_file_limit(self):
        # PackageManifest.MaximumFileBytes is enforced by installed kernels too.
        limit = int(re.search(r'MaximumFileBytes = (\d+)', (ROOT/'src/Core/Packages/PackageManifest.cs').read_text()).group(1))
        with tempfile.TemporaryDirectory(prefix='zenith-ui-size-') as directory:
            for program in ('ui-gallery', 'settings', 'desktop-tools'):
                dest = Path(directory)/program
                subprocess.run([sys.executable, '-B', str(ROOT/'tools/build-userland.py'), str(dest), '--program', program],
                               check=True, stdout=subprocess.DEVNULL)
                size = (dest/'usr/share/zenith/native'/(program + '.elf')).stat().st_size
                self.assertLessEqual(size, limit - 32768, f'{program}.elf leaves under 32 KiB package headroom: {size}')

    def test_settings_units_with_sanitizers(self):
        with tempfile.TemporaryDirectory(prefix='zenith-settings-units-') as directory:
            dest = Path(directory)
            def generated(tool, name):
                spec = importlib.util.spec_from_file_location(name, ROOT/'tools'/tool)
                module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
                path = dest/(name + '.c'); module.build(path); return str(path)
            exe = dest/'settings-units'
            subprocess.run(['gcc','-std=c11','-Wall','-Wextra','-Werror','-O1','-g',
                            '-fsanitize=address,undefined','-fno-omit-frame-pointer','-I'+str(ROOT/'userland/ui'),
                            '-I'+str(ROOT/'userland/settings'),str(ROOT/'userland/settings/test_navigation.c'),
                            str(ROOT/'userland/settings/navigation.c'),generated('build-meridian-pages.py','pages'),
                            str(ROOT/'userland/ui/ui.c'),str(ROOT/'userland/ui/widgets.c'),
                            generated('build-ui-fonts.py','fonts'),generated('build-ui-icons.py','icons'),'-o',str(exe)],check=True)
            subprocess.run([str(exe)],check=True,timeout=30)

    def test_widgets_with_sanitizers(self):
        with tempfile.TemporaryDirectory(prefix='zenith-widgets-') as directory:
            dest = Path(directory)
            def generated(tool, name):
                spec = importlib.util.spec_from_file_location(name, ROOT/'tools'/tool)
                module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
                path = dest/(name + '.c'); module.build(path); return str(path)
            exe = dest/'widgets-test'
            subprocess.run(['gcc','-std=c11','-Wall','-Wextra','-Werror','-O1','-g',
                            '-fsanitize=address,undefined','-fno-omit-frame-pointer','-I'+str(ROOT/'userland/ui'),
                            str(ROOT/'userland/ui/test_widgets.c'),str(ROOT/'userland/ui/widgets.c'),
                            str(ROOT/'userland/ui/ui.c'),generated('build-ui-fonts.py','fonts'),
                            generated('build-ui-icons.py','icons'),'-o',str(exe)],check=True)
            subprocess.run([str(exe)],check=True,timeout=30)

    def test_renderer_and_input_with_sanitizers(self):
        with tempfile.TemporaryDirectory(prefix='zenith-ui-') as directory:
            dest = Path(directory)
            spec = importlib.util.spec_from_file_location('ui_fonts', ROOT/'tools/build-ui-fonts.py')
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            atlas = dest/'fonts.c'
            module.build(atlas)
            icon_spec = importlib.util.spec_from_file_location('ui_icons', ROOT/'tools/build-ui-icons.py')
            icon_module = importlib.util.module_from_spec(icon_spec); icon_spec.loader.exec_module(icon_module)
            icons = dest/'icons.c'; icon_module.build(icons)
            exe = dest/'ui-test'
            subprocess.run(['gcc', '-std=c11', '-Wall', '-Wextra', '-Werror', '-O1', '-g',
                            '-fsanitize=address,undefined', '-fno-omit-frame-pointer',
                            '-I'+str(ROOT/'userland/ui'), str(ROOT/'userland/ui/test_ui.c'),
                            str(ROOT/'userland/ui/ui.c'), str(ROOT/'userland/ui/widgets.c'), str(ROOT/'userland/ui-gallery/gallery.c'),
                            str(atlas),str(icons), '-o', str(exe)], check=True)
            subprocess.run([str(exe), directory], check=True, timeout=30)
            def luminance(value):
                values=[((value >> shift) & 255)/255 for shift in (16,8,0)]
                values=[x/12.92 if x<=.04045 else ((x+.055)/1.055)**2.4 for x in values]
                return sum(x*k for x,k in zip(values,(.2126,.7152,.0722)))
            def contrast(a,b):
                high,low=sorted((luminance(a),luminance(b)),reverse=True)
                return (high+.05)/(low+.05)
            palettes=(dest/'palette.tsv').read_text().splitlines()
            self.assertEqual(len(palettes),12)
            for row in palettes:
                values=row.split()
                surface,raised,subtle,text,secondary,accent,on_accent=map(lambda x:int(x,16),values[2:])
                for foreground in (text,secondary):
                    for background in (surface,raised,subtle):
                        self.assertGreaterEqual(contrast(foreground,background),4.5,row)
                self.assertGreaterEqual(contrast(accent,on_accent),4.5,row)
                for background in (surface,raised,subtle):
                    self.assertGreaterEqual(contrast(accent,background),3,row)
            settings_exe=dest/'settings-test'
            subprocess.run(['gcc','-std=c11','-Wall','-Wextra','-Werror','-O1','-g',
                            '-fsanitize=address,undefined','-fno-omit-frame-pointer',
                            '-I'+str(ROOT/'userland/ui'),str(ROOT/'userland/settings/test_settings.c'),
                            str(ROOT/'userland/settings/app.c'),str(ROOT/'userland/ui/ui.c'),
                            str(atlas),str(icons),'-o',str(settings_exe)],check=True)
            subprocess.run([str(settings_exe), directory],check=True,timeout=30)
            pages_spec=importlib.util.spec_from_file_location('meridian_pages', ROOT/'tools/build-meridian-pages.py')
            pages_module=importlib.util.module_from_spec(pages_spec)
            pages_spec.loader.exec_module(pages_module)
            pages=dest/'pages.c';pages_module.build(pages)
            meridian_exe=dest/'meridian-test'
            subprocess.run(['gcc','-std=c11','-Wall','-Wextra','-Werror','-O1','-g',
                            '-fsanitize=address,undefined','-fno-omit-frame-pointer',
                            '-I'+str(ROOT/'userland/ui'),'-I'+str(ROOT/'userland/settings'),str(ROOT/'userland/settings/test_meridian.c'),
                            str(ROOT/'userland/settings/meridian.c'),str(ROOT/'userland/settings/app.c'),
                            str(ROOT/'userland/ui/text.c'),str(ROOT/'userland/ui/ui.c'),
                            str(pages),str(atlas),str(icons),'-o',str(meridian_exe)],check=True)
            subprocess.run([str(meridian_exe),directory],check=True,timeout=30)
            evidence=os.environ.get('ZENITH_UI_EVIDENCE_DIR')
            if evidence:
                target=Path(evidence);target.mkdir(parents=True,exist_ok=True)
                for source in dest.glob('*.ppm'):shutil.copyfile(source,target/source.name)
            for name in ('dark', 'light'):
                self.assertTrue((dest/(name+'.ppm')).read_bytes().startswith(b'P6\n600 440\n255\n'))

if __name__ == '__main__':
    unittest.main()
