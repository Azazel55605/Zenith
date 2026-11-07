"""Login startup must finish its input drain before the fixture opens Terminal."""
import importlib.util,json,tempfile,unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock,patch
from tools.test_validation_terminal import frame,terminal
ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('login_input',ROOT/'deploy/distributor/validation-login.py')
login=importlib.util.module_from_spec(spec);spec.loader.exec_module(login)

class LoginInputTests(unittest.TestCase):
 def fixture(self):
  folder=tempfile.TemporaryDirectory();self.addCleanup(folder.cleanup);path=Path(folder.name)/'monitor.sock'
  machine=SimpleNamespace(directory=path.parent/'gui',serial=path.parent/'gui/serial.log',process=SimpleNamespace(poll=lambda:None),log=Mock(return_value='desktop ready (installed)\n'),wait=Mock(return_value=True))
  raw=Mock(return_value='one-key');module=SimpleNamespace(monitor_command=raw)
  observer=Mock(snapshot=path.parent/'observed.ppm');observer.capture.side_effect=[frame(),frame(),frame((50,70,51,87))]
  policy=SimpleNamespace(RenderedMouse=Mock(return_value=observer),monitor_text=Mock())
  login.install_monitor(module,[machine],policy,terminal)
  return path,machine,raw,module,observer,policy
 def test_fade_then_single_shortcut_then_focused_prompt_before_return(self):
  path,machine,raw,module,observer,policy=self.fixture();order=[]
  def fade(marker,offset,timeout):
   raw.assert_not_called();self.assertEqual((login.FADE,0,30),(marker,offset,timeout));order.append('fade');return True
  machine.wait.side_effect=fade
  def shortcut(*args):order.append('shortcut');self.assertEqual(['fade','shortcut'],order);return 'one-key'
  raw.side_effect=shortcut
  original_capture=observer.capture.side_effect
  def capture(_):self.assertEqual(['fade','shortcut'],order);return next(original_capture)
  observer.capture.side_effect=capture
  with patch.object(login.time,'sleep'):self.assertEqual('one-key',module.monitor_command(path,'sendkey ctrl-alt-t 30'))
  raw.assert_called_once_with(path,'sendkey ctrl-alt-t 30');self.assertEqual(3,observer.capture.call_count);policy.monitor_text.assert_not_called()
 def test_unfinished_fade_never_sends_shortcut(self):
  path,machine,raw,module,observer,policy=self.fixture();machine.wait.return_value=False;observer.capture.side_effect=None
  with self.assertRaisesRegex(RuntimeError,'fade did not finish'):module.monitor_command(path,'sendkey ctrl-alt-t 30')
  raw.assert_not_called();data=json.loads(path.with_name('login-terminal-handoff.json').read_text());self.assertFalse(data['shortcut_sent']);self.assertEqual('session-fade',data['phase'])
 def test_greeter_or_changed_session_never_receives_shortcut(self):
  for changed in (False,True):
   path,machine,raw,module,observer,policy=self.fixture();observer.capture.side_effect=None
   if changed:machine.wait.side_effect=lambda *args,**kwargs: (setattr(machine.log,'return_value','desktop ready (installed)\nsession: login ready\n') or True)
   else:machine.log.return_value='session: login ready\n'
   with self.assertRaises(RuntimeError):module.monitor_command(path,'sendkey ctrl-alt-t 30')
   raw.assert_not_called()
 def test_missing_public_prompt_does_not_repeat_the_shortcut_or_type_commands(self):
  path,machine,raw,module,observer,policy=self.fixture();observer.capture.side_effect=None;observer.capture.return_value=frame(public=False)
  with patch.object(login.time,'sleep'),patch.object(login.time,'monotonic',side_effect=[0,16]):
   with self.assertRaisesRegex(RuntimeError,'public Terminal prompt'):module.monitor_command(path,'sendkey ctrl-alt-t 30')
  raw.assert_called_once();policy.monitor_text.assert_not_called();data=json.loads(path.with_name('login-terminal-handoff.json').read_text());self.assertTrue(data['shortcut_sent']);self.assertEqual('public-terminal-prompt',data['phase'])
 def test_session_change_after_opening_never_acknowledges_a_stale_prompt(self):
  path,machine,raw,module,observer,policy=self.fixture()
  def capture(_):
   if observer.capture.call_count==2:
    machine.log.return_value+='session: login ready\n'
    return frame((50,70,51,87))
   return frame()
  observer.capture.side_effect=capture
  with patch.object(login.time,'sleep'):
   with self.assertRaisesRegex(RuntimeError,'Session changed'):module.monitor_command(path,'sendkey ctrl-alt-t 30')
  raw.assert_called_once();policy.monitor_text.assert_not_called()
 def test_credentials_and_other_actions_are_not_intercepted(self):
  path,machine,raw,module,observer,policy=self.fixture()
  for text in ('sendkey ret 30','sendkey ctrl-alt-l 30','sendkey a 30','sendkey ctrl-alt-t 31'):
   self.assertEqual('one-key',module.monitor_command(path,text))
  self.assertEqual(4,raw.call_count);machine.log.assert_not_called();machine.wait.assert_not_called();observer.capture.assert_not_called()
 def test_reinstall_does_not_duplicate_dispatch(self):
  path,machine,raw,module,observer,policy=self.fixture();installed=module.monitor_command
  login.install_monitor(module,[machine],policy,terminal);self.assertIs(installed,module.monitor_command)
  with patch.object(login.time,'sleep'):module.monitor_command(path,'sendkey ctrl-alt-t 30')
  raw.assert_called_once()
 def test_dead_and_ambiguous_registry_never_dispatch(self):
  for kind in ('dead','ambiguous'):
   path,machine,raw,module,observer,policy=self.fixture();observer.capture.side_effect=None
   candidates=[machine,machine] if kind=='ambiguous' else [SimpleNamespace(directory=machine.directory,process=SimpleNamespace(poll=lambda:0))]
   module=SimpleNamespace(monitor_command=raw);login.install_monitor(module,candidates,policy,terminal)
   with self.assertRaisesRegex(RuntimeError,'VM unavailable'):module.monitor_command(path,'sendkey ctrl-alt-t 30')
   raw.assert_not_called()

if __name__=='__main__':unittest.main()
