import importlib.util
import io
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch
from PIL import Image, ImageDraw

spec=importlib.util.spec_from_file_location('validation_terminal',Path(__file__).resolve().parents[1]/'deploy/distributor/validation-terminal.py')
terminal=importlib.util.module_from_spec(spec);spec.loader.exec_module(terminal)

def frame(rect=None):
    image=Image.new('RGB',(300,200),(40,40,40))
    if rect:ImageDraw.Draw(image).rectangle(rect,fill=(90,180,130))
    stream=io.BytesIO();image.save(stream,format='PPM');return stream.getvalue()

class TerminalTests(unittest.TestCase):
    def test_requires_a_real_two_pixel_caret_blink(self):
        plain=frame();caret=frame((50,70,51,87))
        self.assertTrue(terminal.caret_changed(plain,caret))
        self.assertTrue(terminal.caret_changed(caret,plain))
        for other in (plain,frame((50,70,54,87)),frame((50,70,51,75)),frame((50,70,51,110))):
            self.assertFalse(terminal.caret_changed(plain,other))
        self.assertFalse(terminal.caret_changed(caret,caret))
    def setup_hook(self, log='user: S_UI=0', retired=True):
        path=Path('/tmp/fixture/monitor.sock')
        machine=SimpleNamespace(directory=path.parent/'desktop',log=Mock(return_value=log),wait=Mock(return_value=retired))
        login=SimpleNamespace(monitor_type=Mock());original=login.monitor_type
        observer=Mock(snapshot=path.parent/'observation.ppm')
        observer.capture.side_effect=[frame(),frame(),frame((50,70,51,87))]
        policy=SimpleNamespace(RenderedMouse=Mock(return_value=observer),monitor_text=Mock())
        terminal.install(login,[machine],policy)
        return path,machine,login,original,observer,policy
    def test_close_status_and_retirement_then_rendered_blink_before_one_dispatch(self):
        path,machine,login,original,observer,policy=self.setup_hook()
        def capture(_):
            self.assertEqual(original.call_count,0)
            return frame((50,70,51,87)) if observer.capture.call_count==3 else frame()
        observer.capture.side_effect=capture
        with patch.object(terminal.time,'sleep'):
            login.monitor_type(path,'sh /root/l\n')
        machine.wait.assert_called_once_with('graphics: native surface retired',0,timeout=15)
        original.assert_called_once_with(path,'sh /root/l\n')
        policy.monitor_text.assert_not_called()
    def test_other_public_commands_and_all_credentials_are_untouched(self):
        path,machine,login,original,observer,policy=self.setup_hook()
        for text in ('passwd root\n','a-secret\n','sh /root/u\n','root\t','sh /root/later\n'):
            login.monitor_type(path,text)
        self.assertEqual(original.call_count,5);machine.wait.assert_not_called();observer.capture.assert_not_called()
    def test_missing_close_or_retirement_never_dispatches(self):
        for log,retired in (('',True),('user: S_UI=0',False)):
            path,machine,login,original,observer,policy=self.setup_hook(log,retired)
            with self.assertRaises(RuntimeError):login.monitor_type(path,'sh /root/l\n')
            original.assert_not_called();observer.capture.assert_not_called()
    def test_frozen_caret_fails_without_replay(self):
        path,machine,login,original,observer,policy=self.setup_hook()
        observer.capture.side_effect=None;observer.capture.return_value=frame((50,70,51,87))
        with patch.object(terminal.time,'sleep'),patch.object(terminal.time,'monotonic',side_effect=[0,16]):
            with self.assertRaisesRegex(RuntimeError,'caret'):login.monitor_type(path,'sh /root/l\n')
        original.assert_not_called()

if __name__=='__main__':unittest.main()
