"""One Escape, host retirement and a rendered public prompt for legacy graphics tags."""
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import tempfile
import os
import unittest
from unittest.mock import Mock, patch
from tools.test_validation_terminal import frame

ROOT=Path(__file__).resolve().parents[1]
def load(name, path):
    spec=importlib.util.spec_from_file_location(name,path);module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module
handoff=load('graphics_handoff',ROOT/'deploy/distributor/validation-graphics.py')
terminal=load('graphics_terminal',ROOT/'deploy/distributor/validation-terminal.py')

class GraphicsHandoffTests(unittest.TestCase):
    def setup_hook(self):
        temp=tempfile.TemporaryDirectory();self.addCleanup(temp.cleanup)
        path=Path(temp.name)/'monitor.sock'
        raw=handoff.OPENED+'\n'+handoff.PACKAGE+'\n'+handoff.READY+'\n'+handoff.OPENED+'\n'+handoff.LAUNCHED+'\n'
        machine=SimpleNamespace(directory=path.parent/'desktop',serial=path.parent/'serial.log',process=SimpleNamespace(poll=lambda:None),log=Mock(return_value=raw),wait=Mock())
        original=Mock(return_value='monitor-response');original._zenith_rendered_mouse=True;original._zenith_observatory_handoff=False
        console=SimpleNamespace(monitor_command=original)
        observer=Mock(snapshot=path.with_name('observation.ppm'))
        observer.capture.side_effect=[frame(),frame(),frame((50,70,51,87))]
        policy=SimpleNamespace(RenderedMouse=Mock(return_value=observer),monitor_text=Mock())
        def wait(marker,off,timeout):
            if marker==handoff.OPENED:
                self.assertEqual(original.call_count,0);self.assertEqual(off,raw.rfind(handoff.PACKAGE));return True
            self.assertEqual(marker,handoff.RETIRED);self.assertEqual(off,raw.rfind(handoff.READY))
            self.assertEqual(original.call_count,1)
            machine.log.return_value+='\n'+handoff.RETIRED+'\n';return True
        machine.wait.side_effect=wait
        handoff.install_monitor(console,[machine],policy,terminal)
        return path,machine,console,original,observer,policy

    def test_legacy_client_without_focus_print_waits_for_retirement_and_public_blink(self):
        path,m,c,original,observer,policy=self.setup_hook()
        def capture(_):
            self.assertIn(handoff.RETIRED,m.log())
            return frame((50,70,51,87)) if observer.capture.call_count==3 else frame()
        observer.capture.side_effect=capture
        with patch.object(handoff.time,'sleep'):
            self.assertEqual(c.monitor_command(path,'sendkey esc 30'),'monitor-response')
        original.assert_called_once_with(path,'sendkey esc 30');self.assertEqual(m.wait.call_count,2)
        self.assertEqual(observer.capture.call_count,3);policy.monitor_text.assert_not_called()
        self.assertTrue(c.monitor_command._zenith_rendered_mouse)

    def test_unadmitted_window_never_receives_escape(self):
        path,m,c,original,observer,_=self.setup_hook();m.wait.side_effect=None;m.wait.return_value=False
        with self.assertRaisesRegex(RuntimeError,'admission'):c.monitor_command(path,'sendkey esc 30')
        original.assert_not_called();observer.capture.assert_not_called()

    def test_unretired_window_never_replays_escape_or_observes_prompt(self):
        path,m,c,original,observer,_=self.setup_hook();m.wait.side_effect=[True,False]
        with self.assertRaisesRegex(RuntimeError,'did not retire'):c.monitor_command(path,'sendkey esc 30')
        original.assert_called_once();observer.capture.assert_not_called()

    def test_frozen_or_hidden_prompt_is_not_readiness(self):
        for public in (True,False):
            path,m,c,original,observer,_=self.setup_hook()
            observer.capture.side_effect=None;observer.capture.return_value=frame(public=public)
            with patch.object(handoff.time,'sleep'),patch.object(handoff.time,'monotonic',side_effect=[0,16]):
                with self.assertRaisesRegex(RuntimeError,'public prompt'):c.monitor_command(path,'sendkey esc 30')
            original.assert_called_once()

    def test_private_blink_is_rejected(self):
        path,m,c,original,observer,_=self.setup_hook()
        observer.capture.side_effect=[frame(public=False),frame((50,70,51,87),public=False),frame(public=False)]
        with patch.object(handoff.time,'sleep'),patch.object(handoff.time,'monotonic',side_effect=[0,16]):
            with self.assertRaisesRegex(RuntimeError,'public prompt'):c.monitor_command(path,'sendkey esc 30')
        original.assert_called_once()

    def test_already_retired_client_never_receives_another_escape(self):
        path,m,c,original,observer,_=self.setup_hook();m.log.return_value+='\n'+handoff.RETIRED
        with self.assertRaisesRegex(RuntimeError,'before Escape'):c.monitor_command(path,'sendkey esc 30')
        original.assert_not_called();observer.capture.assert_not_called()

    def test_next_polaris_escape_and_nonfixture_commands_are_unchanged(self):
        path,m,c,original,observer,_=self.setup_hook()
        with patch.object(handoff.time,'sleep'):c.monitor_command(path,'sendkey esc 30')
        count=observer.capture.call_count
        for text in ('sendkey esc 30','sendkey a 30','mouse_button 1'):
            c.monitor_command(path,text)
        self.assertEqual(original.call_count,4);self.assertEqual(m.wait.call_count,2)
        self.assertEqual(observer.capture.call_count,count)

    def test_processes_view_is_a_second_independent_handoff(self):
        path,m,c,original,observer,_=self.setup_hook()
        with patch.object(handoff.time,'sleep'):c.monitor_command(path,'sendkey esc 30')
        boundary=m.log().rfind(handoff.RETIRED)
        m.log.return_value+='\n'+handoff.READY+'\n'+handoff.OPENED+'\n'+handoff.PROCESSES+'\n'
        ready=m.log().rfind(handoff.READY)
        def wait(marker,offset,timeout):
            if marker==handoff.OPENED:self.assertEqual(offset,boundary);return True
            self.assertEqual(offset,ready);self.assertEqual(original.call_count,2)
            m.log.return_value+='\n'+handoff.RETIRED+'\n';return True
        m.wait.side_effect=wait;observer.capture.side_effect=[frame(),frame((50,70,51,87))]
        with patch.object(handoff.time,'sleep'):c.monitor_command(path,'sendkey esc 30')
        self.assertEqual(original.call_count,2);self.assertEqual(m.wait.call_count,4)

    def test_launch_status_and_ready_may_arrive_in_either_order(self):
        path,m,c,original,observer,_=self.setup_hook()
        m.log.return_value=handoff.PACKAGE+'\n'+handoff.LAUNCHED+'\n'+handoff.READY+'\n'+handoff.OPENED+'\n'
        def wait(marker,offset,timeout):
            if marker==handoff.RETIRED:m.log.return_value+='\n'+handoff.RETIRED+'\n'
            return True
        m.wait.side_effect=wait
        with patch.object(handoff.time,'sleep'):c.monitor_command(path,'sendkey esc 30')
        original.assert_called_once();self.assertEqual(m.wait.call_count,2)

    def test_before_observatory_fixture_escape_is_unchanged(self):
        path,m,c,original,observer,_=self.setup_hook();m.log.return_value='user: POLARIS_READY\n'
        c.monitor_command(path,'sendkey esc 30')
        original.assert_called_once();m.wait.assert_not_called();observer.capture.assert_not_called()

    def test_new_native_window_during_prompt_observation_is_rejected(self):
        path,m,c,original,observer,_=self.setup_hook()
        def capture(_):
            if observer.capture.call_count==2:
                m.log.return_value+='\n'+handoff.OPENED;return frame((50,70,51,87))
            return frame()
        observer.capture.side_effect=capture
        with patch.object(handoff.time,'sleep'):
            with self.assertRaisesRegex(RuntimeError,'window changed'):c.monitor_command(path,'sendkey esc 30')
        original.assert_called_once()

    def test_idempotent_install_and_ambiguous_registry_fail_closed(self):
        path,m,c,original,observer,policy=self.setup_hook();first=c.monitor_command
        handoff.install_monitor(c,[m],policy,terminal);self.assertIs(c.monitor_command,first)
        other=SimpleNamespace(monitor_command=original)
        handoff.install_monitor(other,[m,m],policy,terminal)
        with self.assertRaisesRegex(RuntimeError,'Ambiguous'):other.monitor_command(path,'sendkey esc 30')
        original.assert_not_called()

class ReplayFixtureTests(unittest.TestCase):
    def test_selects_latest_clean_setup_and_preserves_the_source(self):
        replay=load('observatory_replay_fixture',ROOT/'tools/observatory-handoff-replay.py')
        with tempfile.TemporaryDirectory() as folder:
            build=Path(folder)
            for name,stamp in (('old',1),('new',2)):
                disk=build/('obj/distributor/release-'+name+'/native/g/setup/disk.img')
                disk.parent.mkdir(parents=True);disk.write_bytes(b'original fixture');os.utime(disk,(stamp,stamp))
            disk,elf,harness=replay.fixture(build)
            self.assertIn('release-new',str(disk));self.assertEqual(disk.read_bytes(),b'original fixture')
            self.assertEqual(elf,build/'output-x64/Zenith.elf')
            self.assertEqual(harness,build/'tools/native-graphics-smoke.py')
    def test_missing_setup_never_substitutes_a_mutated_desktop_disk(self):
        replay=load('observatory_replay_missing',ROOT/'tools/observatory-handoff-replay.py')
        with tempfile.TemporaryDirectory() as folder:
            disk=Path(folder)/'obj/distributor/release-new/native/g/desktop/disk.img'
            disk.parent.mkdir(parents=True);disk.write_bytes(b'failed desktop')
            with self.assertRaisesRegex(ValueError,'No retained graphics setup'):replay.fixture(Path(folder))

if __name__=='__main__':unittest.main()
