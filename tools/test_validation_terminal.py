import importlib.util
import io
import json
import tempfile
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch
from PIL import Image, ImageDraw

spec=importlib.util.spec_from_file_location('validation_terminal',Path(__file__).resolve().parents[1]/'deploy/distributor/validation-terminal.py')
terminal=importlib.util.module_from_spec(spec);spec.loader.exec_module(terminal)

def frame(rect=None, public=True):
    image=Image.new('RGB',(300,200),(40,40,40))
    if public:
        draw=ImageDraw.Draw(image)
        draw.rectangle((5,74,42,78),fill=(90,180,130))
        draw.rectangle((44,76,46,78),fill=(100,130,220))
    if rect:ImageDraw.Draw(image).rectangle(rect,fill=(90,180,130))
    stream=io.BytesIO();image.save(stream,format='PPM');return stream.getvalue()

class TerminalTests(unittest.TestCase):
    def test_public_prompt_on_blue_tinted_recovery_background(self):
        for public in (True,False):
            image=Image.open(io.BytesIO(frame((50,70,51,87),public=public))).convert('RGB')
            pixels=image.load()
            for y in range(image.height):
                for x in range(image.width):
                    if pixels[x,y]==(40,40,40):pixels[x,y]=(28,32,48)
            out=io.BytesIO();image.save(out,format='PPM')
            self.assertEqual(terminal.public_prompt(out.getvalue(),(50,70,52,88)),public)

    def test_unavailable_registry_records_only_vm_identity_without_typing(self):
        for count in (0,2):
            with tempfile.TemporaryDirectory() as directory:
                path=Path(directory)/'monitor.sock'
                machines=[SimpleNamespace(directory=path.parent/'desktop',process=SimpleNamespace(pid=123,poll=lambda:None)) for _ in range(count)]
                login=SimpleNamespace(monitor_type=Mock());original=login.monitor_type
                policy=SimpleNamespace(RenderedMouse=Mock(),monitor_text=Mock())
                terminal.install(login,machines,policy)
                with self.assertRaisesRegex(RuntimeError,'registered='+str(count)):
                    login.monitor_type(path,'sh /root/u\n')
                report=json.loads(path.with_name('terminal-handoff.json').read_text())
                self.assertEqual(count,report['matches']);self.assertEqual(count,len(report['registered']))
                original.assert_not_called();policy.RenderedMouse.assert_not_called()

    def test_requires_a_real_two_pixel_caret_blink(self):
        plain=frame();caret=frame((50,70,51,87))
        self.assertTrue(terminal.caret_changed(plain,caret))
        self.assertTrue(terminal.caret_changed(caret,plain))
        for other in (plain,frame((50,70,54,87)),frame((50,70,51,75)),frame((50,70,51,110))):
            self.assertFalse(terminal.caret_changed(plain,other))
        self.assertFalse(terminal.caret_changed(caret,caret))
    def setup_hook(self, log='graphics: native surface opened 600x480\nuser: S_UI=0', retired=True, protocol='settings', replay_endpoint=None):
        path=Path('/tmp/fixture/monitor.sock')
        machine=SimpleNamespace(directory=path.parent/'desktop',log=Mock(return_value=log),wait=Mock(return_value=retired))
        login=SimpleNamespace(monitor_type=Mock());original=login.monitor_type
        observer=Mock(snapshot=path.parent/'observation.ppm')
        observer.capture.side_effect=[frame(),frame(),frame((50,70,51,87))]
        policy=SimpleNamespace(RenderedMouse=Mock(return_value=observer),monitor_text=Mock())
        terminal.install(login,[machine],policy,protocol=protocol,replay_endpoint=replay_endpoint)
        return path,machine,login,original,observer,policy

    def test_gallery_shutdown_observes_retirement_and_delayed_public_prompt_once(self):
        path,machine,login,original,observer,policy=self.setup_hook(log='graphics: native surface opened 600x440\nuser: UI_DONE=0',protocol='gallery')
        observer.capture.side_effect=[frame(public=False),frame(public=False),frame(),frame((50,70,51,87))]
        with patch.object(terminal.time,'sleep'):
            login.monitor_type(path,'sh /root/ui010-p\n')
        machine.wait.assert_called_once_with('graphics: native surface retired',0,timeout=15)
        self.assertEqual(4,observer.capture.call_count)
        original.assert_called_once_with(path,'sh /root/ui010-p\n')
        policy.monitor_text.assert_not_called()

    def test_gallery_first_launch_requires_public_prompt_without_retirement(self):
        path,machine,login,original,observer,policy=self.setup_hook(log='desktop ready',protocol='gallery')
        with patch.object(terminal.time,'sleep'):login.monitor_type(path,'sh /root/ui010-u\n')
        machine.wait.assert_not_called();self.assertEqual(3,observer.capture.call_count)
        original.assert_called_once_with(path,'sh /root/ui010-u\n')

    def test_gallery_unretired_window_or_frozen_prompt_never_sends_poweroff(self):
        for retired in (False,True):
            path,machine,login,original,observer,policy=self.setup_hook(retired=retired,protocol='gallery')
            observer.capture.side_effect=None;observer.capture.return_value=frame()
            with patch.object(terminal.time,'sleep'),patch.object(terminal.time,'monotonic',side_effect=[0,16]):
                with self.assertRaises(RuntimeError):login.monitor_type(path,'sh /root/ui010-p\n')
            original.assert_not_called()

    def test_gallery_protocol_does_not_intercept_other_scripts_or_credentials(self):
        path,machine,login,original,observer,policy=self.setup_hook(protocol='gallery')
        for text in ('sh /root/u\n','sh /root/ui010-p-extra\n','sh /root/ui010-p; poweroff\n','private-value\n','passwd root\n'):
            login.monitor_type(path,text)
        self.assertEqual(5,original.call_count);observer.capture.assert_not_called()
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
        for text in ('login root\n','a-secret\n','settings show\n','root\t','sh /root/later\n'):
            login.monitor_type(path,text)
        self.assertEqual(original.call_count,5);machine.wait.assert_not_called();observer.capture.assert_not_called()
    def test_unretired_native_surface_never_dispatches(self):
        path,machine,login,original,observer,policy=self.setup_hook(retired=False)
        with self.assertRaises(RuntimeError):login.monitor_type(path,'sh /root/o\n')
        original.assert_not_called();observer.capture.assert_not_called()
    def test_first_launch_waits_for_public_prompt_without_nonexistent_retirement(self):
        path,machine,login,original,observer,policy=self.setup_hook(log='desktop ready')
        with patch.object(terminal.time,'sleep'):
            login.monitor_type(path,'sh /root/u\n')
        machine.wait.assert_not_called();self.assertEqual(3,observer.capture.call_count)
        original.assert_called_once_with(path,'sh /root/u\n')
    def test_every_new_page_exit_is_observed_even_for_repeated_same_script(self):
        path,machine,login,original,observer,policy=self.setup_hook()
        with patch.object(terminal.time,'sleep'):
            for index,status in enumerate(('S_UI','S_KEYBOARD','S_TIMEZONE','S_SESSION_PAGE','S_MERIDIAN')):
                machine.log.return_value+=('\ngraphics: native surface opened 600x480\nuser: '+status+'=0')
                observer.capture.side_effect=[frame(),frame((50,70,51,87))]
                command='sh /root/l\n' if index==0 else 'sh /root/o\n'
                login.monitor_type(path,command)
                self.assertEqual(index+1,machine.wait.call_count)
                # Same native lifecycle is already acknowledged; a following
                # shell-only command must not require another retirement.
                observer.capture.side_effect=[frame(),frame((50,70,51,87))]
                login.monitor_type(path,'sh /root/v\n')
                self.assertEqual(index+1,machine.wait.call_count)
        self.assertEqual(10,original.call_count)
    def test_new_native_window_during_observation_fails_without_typing(self):
        path,machine,login,original,observer,policy=self.setup_hook()
        def capture(_):
            if observer.capture.call_count==2:
                machine.log.return_value+='\ngraphics: native surface opened 600x480'
                return frame((50,70,51,87))
            return frame()
        observer.capture.side_effect=capture
        with patch.object(terminal.time,'sleep'):
            with self.assertRaisesRegex(RuntimeError,'changed during'):login.monitor_type(path,'sh /root/o\n')
        original.assert_not_called()
    def test_frozen_caret_fails_without_replay(self):
        path,machine,login,original,observer,policy=self.setup_hook()
        observer.capture.side_effect=None;observer.capture.return_value=frame((50,70,51,87))
        with patch.object(terminal.time,'sleep'),patch.object(terminal.time,'monotonic',side_effect=[0,16]):
            with self.assertRaisesRegex(RuntimeError,'prompt'):login.monitor_type(path,'sh /root/l\n')
        original.assert_not_called()

    def test_password_caret_without_public_prompt_never_dispatches_cancel_probe(self):
        path,machine,login,original,observer,policy=self.setup_hook()
        # A real caret blink while the hidden prompt remains active must fail.
        observer.capture.side_effect=[frame(public=False),frame((50,70,51,87),public=False)]
        with patch.object(terminal.time,'sleep'),patch.object(terminal.time,'monotonic',side_effect=[0,16]):
            with self.assertRaisesRegex(RuntimeError,'Public Terminal prompt'):
                login.monitor_type(path,'echo S_SECRET_CANCELLED | logger\n')
        original.assert_not_called()
    def test_cancellation_waits_through_hidden_caret_until_public_prompt_returns(self):
        path,machine,login,original,observer,policy=self.setup_hook()
        observer.capture.side_effect=[frame(public=False),frame((50,70,51,87),public=False),frame(),frame((50,70,51,87))]
        with patch.object(terminal.time,'sleep'):
            login.monitor_type(path,'echo S_SECRET_CANCELLED | logger\n')
        self.assertEqual(4,observer.capture.call_count)
        original.assert_called_once_with(path,'echo S_SECRET_CANCELLED | logger\n')
    def test_known_passwd_command_waits_but_credentials_are_never_observed(self):
        path,machine,login,original,observer,policy=self.setup_hook()
        with patch.object(terminal.time,'sleep'):login.monitor_type(path,'passwd root\n')
        self.assertEqual(3,observer.capture.call_count)
        observer.capture.reset_mock();login.monitor_type(path,'private-value\n')
        observer.capture.assert_not_called()
        self.assertEqual(2,original.call_count)
    def test_public_prompt_requires_same_row_ordered_green_host_and_blue_cwd(self):
        box=(50,70,52,88)
        self.assertTrue(terminal.public_prompt(frame(),box))
        self.assertFalse(terminal.public_prompt(frame(public=False),box))
        self.assertFalse(terminal.public_prompt(frame(),(50,100,52,118)))
        # Neutral password text, green text alone, or blue before green cannot
        # be mistaken for the public fixture prompt.
        for host,cwd in (((150,150,150),(180,180,180)),((90,180,130),(150,150,150)),((100,130,220),(90,180,130))):
            image=Image.new('RGB',(300,200),(40,40,40));draw=ImageDraw.Draw(image)
            draw.rectangle((5,74,42,78),fill=host);draw.rectangle((44,76,46,78),fill=cwd)
            stream=io.BytesIO();image.save(stream,format='PPM')
            self.assertFalse(terminal.public_prompt(stream.getvalue(),box))

    def test_fresh_install_ignores_stopped_vms_that_shared_the_monitor_parent(self):
        path,machine,login,original,observer,policy=self.setup_hook()
        machine.process=SimpleNamespace(poll=lambda:None)
        stopped=[SimpleNamespace(directory=path.parent/name,process=SimpleNamespace(poll=lambda:0)) for name in ('live','setup')]
        other=SimpleNamespace(monitor_type=original)
        terminal.install(other,[*stopped,machine],policy)
        with patch.object(terminal.time,'sleep'):other.monitor_type(path,'sh /root/u\n')
        original.assert_called_once_with(path,'sh /root/u\n')

    def test_prepared_contention_refresh_then_new_prompt_then_single_original_launch(self):
        path,machine,login,original,observer,policy=self.setup_hook(replay_endpoint=lambda:'http://10.0.2.2:54321')
        observer.capture.side_effect=[frame(),frame((50,70,51,87)),frame(),frame((50,70,51,87))]
        with patch.object(terminal.time,'sleep'):login.monitor_type(path,'sh /root/u\n')
        self.assertEqual([('fetch --replace http://10.0.2.2:54321/io /root/io; echo S_REPLAY_IO_REFRESH=$? | logger\n',),('sh /root/u\n',)], [call.args[1:] for call in original.call_args_list])
        self.assertEqual(4,observer.capture.call_count)
        self.assertTrue(any(call.args[0]=='user: S_REPLAY_IO_REFRESH=0' for call in machine.wait.call_args_list))

    def test_refresh_failure_does_not_start_ui_or_repeat_download(self):
        path,machine,login,original,observer,policy=self.setup_hook(replay_endpoint=lambda:'http://10.0.2.2:54321')
        machine.wait.side_effect=[True,False]
        with patch.object(terminal.time,'sleep'):
            with self.assertRaisesRegex(RuntimeError,'refresh failed; not repeated'):login.monitor_type(path,'sh /root/u\n')
        original.assert_called_once();self.assertIn('fetch --replace http://10.0.2.2:54321/io',original.call_args.args[1])

    def test_replay_does_not_refresh_other_scripts(self):
        endpoint=Mock(side_effect=AssertionError('not the initial launch'))
        path,machine,login,original,observer,policy=self.setup_hook(replay_endpoint=endpoint)
        with patch.object(terminal.time,'sleep'):login.monitor_type(path,'sh /root/k\n')
        endpoint.assert_not_called();original.assert_called_once_with(path,'sh /root/k\n')

    def test_invalid_endpoint_never_types_a_download_or_launch(self):
        for endpoint in ('http://evil.example:123','http://10.0.2.2:65536','http://10.0.2.2:0','http://10.0.2.2:123; bad'):
            path,machine,login,original,observer,policy=self.setup_hook(replay_endpoint=lambda:endpoint)
            with patch.object(terminal.time,'sleep'):
                with self.assertRaisesRegex(RuntimeError,'Invalid prepared'):login.monitor_type(path,'sh /root/u\n')
            original.assert_not_called()

class ProcessPublicInputTests(unittest.TestCase):
    def fixture(self, moving=True):
        temporary=tempfile.TemporaryDirectory();self.addCleanup(temporary.cleanup)
        path=Path(temporary.name)/'gui-monitor.sock'
        machine=SimpleNamespace(directory=path.parent/'desktop',process=SimpleNamespace(poll=lambda:None))
        position=[50]
        def typed(path,text,*args,**kwargs):
            if moving and text!='\n':position[0]+=2
        original=Mock(side_effect=typed);login=SimpleNamespace(monitor_type=original)
        observer=Mock(snapshot=path.parent/'observed.ppm')
        observer.capture.side_effect=lambda *args: frame() if observer.capture.call_count%2 else frame((position[0],70,position[0]+1,87))
        observer.position.return_value=((0,0),(300,200))
        policy=SimpleNamespace(RenderedMouse=Mock(return_value=observer),monitor_text=Mock())
        terminal.install_process(login,[machine],policy)
        return path,machine,login,original,observer,policy

    def test_pointer_is_parked_once_without_click_or_corrective_motion(self):
        path,machine,login,original,observer,policy=self.fixture()
        observer.position.side_effect=[((150,100),(300,200)),((150,0),(300,200))]
        with patch.object(terminal.time,'sleep'):
            login.monitor_type(path,'sunbeam --tty-raw | logger; echo N_GUI_RAW=$? | logger\n')
        policy.monitor_text.assert_called_once_with(path,'mouse_move 0 -100')
        observer.command.assert_not_called()

    def test_all_public_stages_are_selected_exactly(self):
        templates={
            'logger {0}_READY; sunbeam cat | logger; echo {0}_EOF=$? | logger\n':'canonical',
            'logger {0}_CANCEL_READY; sunbeam cat | sunbeam cat\n':'cancel',
            'echo {0}_CANCEL=$? | logger\n':'cancel-status',
            'sunbeam --tty-raw | logger; echo {0}_RAW=$? | logger\n':'raw',
            'sunbeam --tty-fault; echo {0}_RAW_FAULT=$? | logger\n':'fault',
            'sunbeam --tty-bounds; echo {0}_TTY_RESTORED=$? | logger\n':'bounds',
        }
        for label in ('N_GUI','N_GUI_EARLY'):
            for text,kind in templates.items():
                self.assertEqual((label,kind),terminal.process_command(text.format(label)))
        for text in ('N_GUI_EARLY_INPUT\n','root\tpassword\n','echo arbitrary\n','sunbeam --tty-raw | logger; echo N_LOCAL_RAW=$? | logger\n'):
            self.assertIsNone(terminal.process_command(text))

    def test_visible_caret_acknowledges_without_a_blink_per_character(self):
        path,machine,login,original,observer,policy=self.fixture();position=[50]
        def typed(path,text):
            if text!='\n':position[0]+=2
        original.side_effect=typed
        observer.capture.side_effect=lambda *args: frame() if observer.capture.call_count==1 else frame((position[0],70,position[0]+1,87))
        text='sunbeam --tty-raw | logger; echo N_GUI_EARLY_RAW=$? | logger\n'
        with patch.object(terminal.time,'sleep'),patch.object(terminal,'caret_box',wraps=terminal.caret_box) as blink:
            login.monitor_type(path,text)
        self.assertEqual(1,blink.call_count)
        self.assertEqual(list(text),[call.args[1] for call in original.call_args_list])

    def test_static_caret_rejects_old_ambiguous_or_short_shapes(self):
        prior=(50,70,52,88);ink=(90,180,130)
        self.assertEqual((58,70,60,88),terminal.advanced_caret(frame((58,70,59,87)),prior,ink))
        self.assertIsNone(terminal.advanced_caret(frame((50,70,51,87)),prior,ink))
        self.assertIsNone(terminal.advanced_caret(frame((58,70,59,75)),prior,ink))
        img=Image.open(io.BytesIO(frame((58,70,59,87))));ImageDraw.Draw(img).rectangle((62,70,63,87),fill=ink)
        data=io.BytesIO();img.save(data,format='PPM')
        self.assertIsNone(terminal.advanced_caret(data.getvalue(),prior,ink))
        self.assertEqual((20,88,22,106),terminal.advanced_caret(frame((20,88,21,105)),prior,ink))
        self.assertEqual((20,70,22,88),terminal.advanced_caret(frame((20,70,21,87)),(200,70,202,88),ink))

    def test_caret_ink_calibration_handles_light_and_dark(self):
        for background in ((40,40,40),(245,245,245)):
            first=Image.new('RGB',(100,100),background);second=first.copy()
            ImageDraw.Draw(second).rectangle((50,50,51,67),fill=(70,110,210))
            a=io.BytesIO();b=io.BytesIO();first.save(a,format='PPM');second.save(b,format='PPM')
            self.assertEqual((70,110,210),terminal.caret_ink(a.getvalue(),b.getvalue(),(50,50,52,68)))
            self.assertIsNone(terminal.caret_ink(a.getvalue(),a.getvalue(),(50,50,52,68)))

    def test_every_character_is_rendered_and_enter_is_submitted_once(self):
        path,machine,login,original,observer,policy=self.fixture()
        text='sunbeam --tty-fault; echo N_GUI_EARLY_RAW_FAULT=$? | logger\n'
        with patch.object(terminal.time,'sleep'):login.monitor_type(path,text)
        self.assertEqual(list(text),[call.args[1] for call in original.call_args_list])
        self.assertEqual(1,sum(call.args[1]=='\n' for call in original.call_args_list))
        self.assertIn(path.with_name('process-command-N_GUI_EARLY-fault.ppm'),[call.args[0] for call in observer.capture.call_args_list])
        policy.monitor_text.assert_not_called()

    def test_unacknowledged_character_stops_before_enter_without_replay(self):
        import itertools
        path,machine,login,original,observer,policy=self.fixture(moving=False)
        with patch.object(terminal.time,'sleep'),patch.object(terminal.time,'monotonic',side_effect=itertools.count(0,16)):
            with self.assertRaisesRegex(RuntimeError,'input not repeated'):
                login.monitor_type(path,'sunbeam --tty-fault; echo N_GUI_RAW_FAULT=$? | logger\n')
        original.assert_called_once_with(path,'s')
        report=json.loads(path.with_name('process-input.json').read_text())
        self.assertEqual(0,report['acknowledged_characters']);self.assertFalse(report['input_repeated'])

    def test_nonpublic_prompt_never_receives_a_key(self):
        import itertools
        path,machine,login,original,observer,policy=self.fixture()
        observer.capture.side_effect=lambda *args: frame(public=False)
        with patch.object(terminal.time,'sleep'),patch.object(terminal.time,'monotonic',side_effect=itertools.count(0,16)):
            with self.assertRaisesRegex(RuntimeError,'public-prompt'):
                login.monitor_type(path,'sunbeam --tty-fault; echo N_GUI_RAW_FAULT=$? | logger\n')
        original.assert_not_called()

    def test_credentials_local_console_and_stream_input_are_unchanged(self):
        path,machine,login,original,observer,policy=self.fixture()
        for text in ('root\tsynthetic-secret\n','N_GUI_EARLY_INPUT\n','sunbeam --tty-fault; echo N_LOCAL_RAW_FAULT=$? | logger\n'):
            login.monitor_type(path,text)
        self.assertEqual(3,original.call_count);observer.capture.assert_not_called()

class SettingsOpeningTests(unittest.TestCase):
    def fixture(self):
        temporary=tempfile.TemporaryDirectory();self.addCleanup(temporary.cleanup)
        path=Path(temporary.name)/'monitor.sock'
        machine=SimpleNamespace(directory=path.parent/'desktop',serial=path.parent/'serial.log',
            log=Mock(return_value='user: MERIDIAN_MOTION percent=100\ngraphics: native surface opened 640x480\nuser: MERIDIAN_PAGE input.keyboard\nuser: MERIDIAN_FOCUSED\n'),wait=Mock(return_value=True))
        observer=Mock(snapshot=path.parent/'observed.ppm');observer.capture.return_value=frame()
        policy=SimpleNamespace(RenderedMouse=Mock(return_value=observer),monitor_text=Mock())
        original=Mock();module=SimpleNamespace(monitor_command=original)
        terminal.install_settings_monitor(module,[machine],policy)
        return path,machine,module,original,observer

    def test_opening_and_rendering_precede_single_navigation(self):
        path,machine,module,original,observer=self.fixture();order=[]
        machine.wait.side_effect=lambda *args,**kwargs: (order.append('opening') or True)
        observer.capture.side_effect=lambda *args: (order.append('frame') or frame())
        original.side_effect=lambda *args: order.append('key')
        with patch.object(terminal.time,'sleep'):module.monitor_command(path,'sendkey shift-tab 30')
        self.assertEqual(['opening','frame','frame','key'],order);original.assert_called_once()

    def test_missing_opening_does_not_send_key(self):
        path,machine,module,original,observer=self.fixture();machine.wait.return_value=False
        with self.assertRaisesRegex(RuntimeError,'opening did not complete'):module.monitor_command(path,'sendkey shift-tab 30')
        original.assert_not_called();self.assertFalse(json.loads(path.with_name('settings-opening.json').read_text())['key_attempted'])

    def test_changed_page_does_not_send_key(self):
        path,machine,module,original,observer=self.fixture()
        observer.capture.side_effect=lambda *args: (setattr(machine.log,'return_value',machine.log.return_value+'user: MERIDIAN_PAGE locale.time\n') or frame())
        with patch.object(terminal.time,'sleep'):
            with self.assertRaisesRegex(RuntimeError,'window changed'):module.monitor_command(path,'sendkey shift-tab 30')
        original.assert_not_called()

    def test_legacy_and_disabled_animations_keep_original_transport(self):
        for log in ('graphics: native surface opened 640x480\n','user: MERIDIAN_MOTION percent=0\n'):
            path,machine,module,original,observer=self.fixture();machine.log.return_value=log
            module.monitor_command(path,'sendkey shift-tab 30')
            original.assert_called_once();machine.wait.assert_not_called();observer.capture.assert_not_called()

    def test_other_pages_keep_original_keyboard_transport(self):
        path,machine,module,original,observer=self.fixture()
        machine.log.return_value=machine.log.return_value.replace('input.keyboard','style.colors')
        module.monitor_command(path,'sendkey shift-tab 30')
        original.assert_called_once();machine.wait.assert_not_called();observer.capture.assert_not_called()

    def test_other_keys_are_unchanged(self):
        path,machine,module,original,observer=self.fixture()
        for key in ('ret','tab','down','ctrl-q'):module.monitor_command(path,'sendkey '+key+' 30')
        self.assertEqual(4,original.call_count);machine.wait.assert_not_called();observer.capture.assert_not_called()

if __name__=='__main__':unittest.main()
