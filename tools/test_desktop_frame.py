"""Reject stale Terminal/menu frames before one irreversible test key dispatch."""
import importlib.util
import io
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import Mock,patch
from PIL import Image,ImageDraw

ROOT=Path(__file__).resolve().parents[1]
def load(name,path):
    spec=importlib.util.spec_from_file_location(name,path)
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module
policy=load('desktop_frame_policy',ROOT/'deploy/distributor/validation-input.py')
replay=load('desktop_launch_replay',ROOT/'tools/desktop-launch-replay.py')

def ppm(image):
    stream=io.BytesIO();image.save(stream,format='PPM');return stream.getvalue()

class DesktopFrameTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp=tempfile.TemporaryDirectory();folder=Path(cls.temp.name)
        atlas=folder/'fonts.c';load('desktop_frame_fonts',ROOT/'tools/build-ui-fonts.py').build(atlas)
        icons=folder/'icons.c';load('desktop_frame_icons',ROOT/'tools/build-ui-icons.py').build(icons)
        exe=folder/'frame'
        subprocess.run(['gcc','-std=c11','-Wall','-Wextra','-Werror','-O1','-I'+str(ROOT/'userland/ui'),
                        str(ROOT/'tools/fixtures/desktop-frame.c'),str(ROOT/'userland/ui/ui.c'),str(atlas),str(icons),'-o',str(exe)],check=True)
        dest=folder/'native.ppm';subprocess.run([str(exe),str(dest)],check=True)
        cls.native=Image.open(dest).convert('RGB')
        dest=folder/'disabled.ppm';subprocess.run([str(exe),str(dest),'disabled'],check=True)
        cls.disabled=Image.open(dest).convert('RGB')
    @classmethod
    def tearDownClass(cls):cls.temp.cleanup()
    def screen(self,x=200,y=120,native=None):
        image=Image.new('RGB',(1280,800),'#2a2c31');image.paste(self.native if native is None else native,(x,y))
        draw=ImageDraw.Draw(image);draw.rectangle((x,y-44,x+599,y+479),outline='#7ecac7')
        for i in range(-4,5):
            draw.point((x+578+i,y-22+i),fill='#b4b7c0');draw.point((x+578+i,y-22-i),fill='#b4b7c0')
        return image
    def test_real_ui_renderer_supplies_enabled_close_and_focused_host_geometry(self):
        for x,y in ((200,120),(340,150),(424,234)):
            self.assertEqual(dict(content=[x,y,600,480],close=[x+476,y+420,96,34]),policy.desktop_frame(ppm(self.screen(x,y))))
    def test_terminal_background_passes_old_predicate_but_is_not_native_readiness(self):
        image=Image.new('RGB',(1280,800),'#2a2c31');raw=ppm(image)
        self.assertIn(bytes.fromhex('2a2c31')*94,image.tobytes())
        self.assertIsNone(policy.desktop_frame(raw))
    def test_disabled_close_from_real_ui_renderer_is_rejected(self):
        self.assertIsNone(policy.desktop_frame(ppm(self.screen(native=self.disabled))))
    def test_launcher_panel_and_incomplete_client_are_rejected(self):
        image=Image.new('RGB',(1280,800),'#202125');ImageDraw.Draw(image).rectangle((10,320,260,730),fill='#2a2c31')
        self.assertIsNone(policy.desktop_frame(ppm(image)))
        image=self.screen();ImageDraw.Draw(image).rectangle((676,540,771,573),fill='#2a2c31')
        self.assertIsNone(policy.desktop_frame(ppm(image)))
    def test_unfocused_chrome_disabled_or_missing_control_text_never_acknowledges(self):
        for point in ((500,76),(200,320),(799,320),(778,98),(724,540)):
            image=self.screen();ImageDraw.Draw(image).point(point,fill='#202125')
            self.assertIsNone(policy.desktop_frame(ppm(image)),str(point))
        image=self.screen();ImageDraw.Draw(image).rectangle((696,547,751,566),fill='#2a2c31')
        self.assertIsNone(policy.desktop_frame(ppm(image)))
    def test_panel_shadow_over_bottom_border_does_not_hide_focused_chrome(self):
        image=self.screen();ImageDraw.Draw(image).line((210,599,789,599),fill='#71b6b3')
        self.assertIsNotNone(policy.desktop_frame(ppm(image)))
    def test_stale_capture_is_observed_until_real_client_without_replaying_input(self):
        with tempfile.TemporaryDirectory() as temp:
            raw=Mock();mouse=policy.RenderedMouse(Path(temp)/'monitor.sock',raw)
            stale=ppm(Image.new('RGB',(1280,800),'#2a2c31'));ready=ppm(self.screen())
            frames=iter((stale,ready))
            def observe(_):raw.assert_not_called();return next(frames)
            with patch.object(mouse,'_capture_once',side_effect=observe) as capture,patch('time.sleep'):
                self.assertEqual(ready,mouse.capture(Path(temp)/'launcher-native-0.ppm'))
            self.assertEqual(2,capture.call_count);raw.assert_not_called()
            mouse.command('sendkey esc 30')
            raw.assert_called_once_with(mouse.monitor,'sendkey esc 30')
    def test_frozen_old_frame_fails_without_sending_escape_or_replaying_launch(self):
        with tempfile.TemporaryDirectory() as temp:
            raw=Mock();mouse=policy.RenderedMouse(Path(temp)/'monitor.sock',raw)
            with patch.object(mouse,'_capture_once',return_value=ppm(Image.new('RGB',(1280,800),'#2a2c31'))),patch('time.monotonic',side_effect=[0,21]):
                with self.assertRaisesRegex(RuntimeError,'not presented'):mouse.capture(Path(temp)/'launcher-native-5.ppm')
            raw.assert_not_called();self.assertTrue((Path(temp)/'native-frame-failure.json').exists())
    def test_other_capture_paths_keep_original_behavior(self):
        mouse=policy.RenderedMouse('/tmp/monitor.sock',Mock())
        with patch.object(mouse,'_capture_once',return_value=b'unchanged') as capture:
            self.assertEqual(b'unchanged',mouse.capture('/tmp/other.ppm'))
        capture.assert_called_once_with(Path('/tmp/other.ppm'))

class LauncherFixtureTests(unittest.TestCase):
    def test_latest_desktop_fixture_is_selected_without_changing_original(self):
        with tempfile.TemporaryDirectory() as temp:
            build=Path(temp);(build/'output-x64').mkdir();elf=build/'output-x64/Zenith.elf';elf.write_bytes(b'ELF')
            for name,stamp in (('release-old',1),('release-new',2)):
                p=build/'obj/distributor'/name/'native/a/desktop/disk.img';p.parent.mkdir(parents=True);p.write_bytes(b'original');os.utime(p,(stamp,stamp))
            disk,found=replay.fixture(build)
            self.assertIn('release-new',str(disk));self.assertEqual(elf,found)
            self.assertEqual(b'original',disk.read_bytes())
    def test_missing_fixture_or_elf_fails_without_starting_anything(self):
        with tempfile.TemporaryDirectory() as temp:
            build=Path(temp)
            with self.assertRaisesRegex(ValueError,'No retained'):replay.fixture(build)
            p=build/'obj/distributor/release-one/native/a/desktop/disk.img';p.parent.mkdir(parents=True);p.write_bytes(b'original')
            with self.assertRaisesRegex(ValueError,'ELF missing'):replay.fixture(build)

if __name__=='__main__':unittest.main()
