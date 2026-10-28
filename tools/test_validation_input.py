import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch

spec=importlib.util.spec_from_file_location('validation_input',Path(__file__).resolve().parents[1]/'deploy/distributor/validation-input.py')
profile=importlib.util.module_from_spec(spec);spec.loader.exec_module(profile)

class InputTests(unittest.TestCase):
    def test_mouse_index_is_discovered_not_assumed(self):
        self.assertEqual((4,False),profile.mouse_index('* Mouse #2: QEMU PS/2 Mouse\n  Mouse #4: QEMU Virtio Mouse\n'))
        self.assertEqual((19,True),profile.mouse_index(' Mouse #0: Other\n* Mouse #19: QEMU Virtio Mouse\n'))
    def test_missing_or_ambiguous_virtio_fails(self):
        for text in ('* Mouse #2: QEMU PS/2 Mouse\n','Mouse #1: QEMU Virtio Mouse\nMouse #9: QEMU Virtio Mouse\n'):
            with self.assertRaises(RuntimeError):profile.mouse_index(text)
    def test_selection_is_explicit_and_verified(self):
        with patch.object(profile,'monitor_text',side_effect=[' Mouse #7: QEMU Virtio Mouse\n','OK','* Mouse #7: QEMU Virtio Mouse\n']) as monitor:
            self.assertEqual(7,profile.select_mouse(Path('/tmp/test.sock')))
            self.assertEqual(['info mice','mouse_set 7','info mice'],[call.args[1] for call in monitor.call_args_list])
    def test_failed_selection_does_not_silently_fall_back(self):
        with patch.object(profile,'monitor_text',side_effect=[' Mouse #7: QEMU Virtio Mouse\n','OK',' Mouse #7: QEMU Virtio Mouse\n']):
            with self.assertRaises(RuntimeError):profile.select_mouse(Path('/tmp/test.sock'))
    def test_only_graphical_fixture_with_monitor_is_changed(self):
        class Machine:
            def __init__(self,*args,**kwargs):self.network=kwargs.get('network');self.stopped=False
            def stop(self):self.stopped=True
        profile.install(Machine)
        network=['-device','virtio-gpu-pci','-device','virtio-keyboard-pci']
        with patch.object(profile,'select_mouse') as select:
            graphical=Machine('/tmp',network=network,monitor=Path('/tmp/m.sock'))
            self.assertEqual(network+['-device','virtio-mouse-pci'],graphical.network)
            self.assertNotIn('virtio-mouse-pci',network)
            select.assert_called_once_with(Path('/tmp/m.sock'))
            select.reset_mock()
            Machine('/tmp',network=network);Machine('/tmp',network=['-device','e1000e'],monitor='/tmp/m.sock')
            select.assert_not_called()
    def test_existing_virtio_is_not_duplicated(self):
        class Machine:
            def __init__(self,*args,**kwargs):self.network=kwargs['network']
        profile.install(Machine)
        network=['-device','virtio-gpu-pci','-device','virtio-mouse-pci']
        with patch.object(profile,'select_mouse'):
            self.assertEqual(network,Machine('/tmp',network=network,monitor='/tmp/m').network)
    def test_install_is_idempotent_for_direct_and_controller_paths(self):
        class Machine:
            def __init__(self,*args,**kwargs):pass
        profile.install(Machine);profile.install(Machine)
        with patch.object(profile,'select_mouse') as select:
            Machine('/tmp',network=['virtio-gpu-pci'],monitor='/tmp/m')
        select.assert_called_once()
    def test_failed_profile_stops_its_disposable_vm(self):
        stopped=[]
        class Machine:
            def __init__(self,*args,**kwargs):pass
            def stop(self):stopped.append(True)
        profile.install(Machine)
        with patch.object(profile,'select_mouse',side_effect=RuntimeError('not selected')):
            with self.assertRaises(RuntimeError):Machine('/tmp',network=['virtio-gpu-pci'],monitor='/tmp/m')
        self.assertEqual([True],stopped)

class RenderedMouseTests(unittest.TestCase):
    def image(self, positions=((10,5),)):
        width,height=64,40;pixels=bytearray(bytes.fromhex('334455')*(width*height))
        for x,y in positions:
            for dy,row in enumerate(profile.CURSOR):
                for dx,c in enumerate(row):
                    if c==' ':continue
                    at=((y+dy)*width+x+dx)*3
                    pixels[at:at+3]=bytes.fromhex('101320' if c=='#' else 'ffffff')
        return b'P6\n64 40\n255\n'+pixels
    def test_monitor_hook_is_scoped_and_idempotent(self):
        from types import SimpleNamespace
        calls=[]
        def raw(path,text):calls.append((path,text))
        module=SimpleNamespace(monitor_command=raw)
        with patch.object(profile,'RenderedMouse') as transport:
            profile.install_monitor(module);profile.install_monitor(module)
            module.monitor_command('/tmp/m','sendkey a 30')
            self.assertEqual([('/tmp/m','sendkey a 30')],calls)
            module.monitor_command('/tmp/m','mouse_move 1 0')
            module.monitor_command('/tmp/m','mouse_button 1')
            transport.assert_called_once_with('/tmp/m',raw)
            self.assertEqual(2,transport.return_value.command.call_count)
    def test_probe_requires_real_aligned_pixels(self):
        header=b'P6\n100 100\n255\n'
        self.assertFalse(profile.probe_visible(header+b'\0'*30000))
        self.assertTrue(profile.probe_visible(header+bytes.fromhex('6b8cff')*10000))
        # The same byte sequence straddling RGB boundaries is not a probe pixel.
        self.assertFalse(profile.probe_visible(header+bytes.fromhex('ff6b8c')*10000))
    def test_fixture_capture_waits_for_presented_pixels(self):
        import tempfile
        with tempfile.TemporaryDirectory() as directory:
            frame=Path(directory)/'close.ppm';calls=[]
            def raw(path,text):
                calls.append(text)
                pixels=b'\0'*30000 if len(calls)==1 else bytes.fromhex('6b8cff')*10000
                frame.write_bytes(b'P6\n100 100\n255\n'+pixels)
            mouse=profile.RenderedMouse(Path(directory)/'m',raw)
            with patch('time.sleep'):
                mouse.command('screendump '+str(frame))
            self.assertEqual(2,len(calls))
            self.assertTrue(all(c=='screendump '+str(frame) for c in calls))
    def test_capture_waits_for_complete_bytes_without_reissuing_command(self):
        import tempfile
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'close.ppm';calls=[]
            def raw(monitor,text):
                calls.append(text);path.write_bytes(b'P6\n100 100\n255\n')
            def complete(_):path.write_bytes(b'P6\n100 100\n255\n'+bytes.fromhex('6b8cff')*10000)
            mouse=profile.RenderedMouse(Path(directory)/'m',raw)
            with patch('time.sleep',side_effect=complete):mouse.command('screendump '+str(path))
            self.assertEqual(['screendump '+str(path)],calls)
    def test_rendered_cursor_is_unique_and_exact(self):
        self.assertEqual(((10,5),(64,40)),profile.cursor_position(self.image()))
        for positions in ((),((10,5),(30,5))):
            with self.assertRaises(RuntimeError):profile.cursor_position(self.image(positions))
    def test_malformed_snapshot_fails(self):
        for data in (b'',b'P6\n64 40\n255\nshort',b'P6\n999999 40\n255\n'):
            with self.assertRaises((RuntimeError,ValueError)):profile.cursor_position(data)
    def test_press_is_sampled_before_return_without_button_replay(self):
        calls=[];mouse=profile.RenderedMouse('/tmp/m.sock',lambda path,text:calls.append(text))
        with patch.object(mouse,'position',return_value=((100,100),(1280,800))), patch.object(mouse,'wait_position') as wait:
            mouse.command('mouse_button 1')
        self.assertEqual(['mouse_button 1','mouse_move 1 0','mouse_move -1 0'],calls)
        self.assertEqual([(101,100),(100,100)],[c.args[0] for c in wait.call_args_list])
    def test_release_is_sampled_and_position_restored(self):
        calls=[];mouse=profile.RenderedMouse('/tmp/m.sock',lambda path,text:calls.append(text))
        with patch.object(mouse,'position',return_value=((100,100),(1280,800))), patch.object(mouse,'wait_position'):
            mouse.command('mouse_button 0')
        self.assertEqual(['mouse_button 0','mouse_move 1 0','mouse_move -1 0'],calls)
    def test_failed_ack_does_not_retry_a_click_or_release_early(self):
        calls=[];mouse=profile.RenderedMouse('/tmp/m.sock',lambda path,text:calls.append(text))
        with patch.object(mouse,'position',return_value=((100,100),(1280,800))), patch.object(mouse,'wait_position',side_effect=RuntimeError('no frame')):
            with self.assertRaises(RuntimeError):mouse.command('mouse_button 1')
        self.assertEqual(['mouse_button 1','mouse_move 1 0'],calls)
    def test_stale_frame_never_accumulates_relative_corrections(self):
        import time
        calls=[];mouse=profile.RenderedMouse('/tmp/m.sock',lambda path,text:calls.append(text))
        with patch.object(mouse,'position',side_effect=[((672,644),(1280,800)),((671,644),(1280,800)),((668,644),(1280,800))]), patch.object(time,'monotonic',side_effect=[0,2.1,4.2,10.1]), patch.object(time,'sleep'), patch.object(mouse,'failure') as failure:
            with self.assertRaises(RuntimeError):mouse.wait_position((670,644),correct_motion=True)
        self.assertEqual([],calls);failure.assert_called_once_with((670,644),(668,644))
    def test_delayed_frame_can_reach_exact_target_without_input_replay(self):
        import time
        calls=[];mouse=profile.RenderedMouse('/tmp/m.sock',lambda path,text:calls.append(text))
        with patch.object(mouse,'position',side_effect=[((100,100),(1280,800)),((102,100),(1280,800)),((101,100),(1280,800))]), patch.object(time,'monotonic',side_effect=[0,2.1,4.2]), patch.object(time,'sleep'):
            mouse.wait_position((101,100),correct_motion=True)
        self.assertEqual([],calls)
    def test_frozen_pointer_fails_with_original_deadline_and_read_only_diagnostic(self):
        import time
        calls=[];mouse=profile.RenderedMouse('/tmp/m.sock',lambda path,text:calls.append(text))
        with patch.object(mouse,'position',return_value=((100,100),(1280,800))), patch.object(time,'monotonic',side_effect=[0,2.1,4.2,10.1]), patch.object(time,'sleep'), patch.object(mouse,'failure') as failure:
            with self.assertRaises(RuntimeError):mouse.wait_position((101,100))
        self.assertEqual([],calls);failure.assert_called_once_with((101,100),(100,100))
    def test_failure_metadata_contains_only_coordinates_and_snapshot_path(self):
        import tempfile,json
        with tempfile.TemporaryDirectory() as directory:
            mouse=profile.RenderedMouse(Path(directory)/'monitor.sock',lambda *args:None)
            with patch.object(profile,'DIAGNOSTIC_ELF',None):mouse.failure((1,2),(3,4))
            row=json.loads((Path(directory)/'input-failure.json').read_text())
            self.assertEqual([1,2],row['target']);self.assertTrue(row['observation_only'])
            self.assertEqual('disabled',row['correction_policy'])
    def test_movement_waits_for_observed_destination(self):
        calls=[];mouse=profile.RenderedMouse('/tmp/m.sock',lambda path,text:calls.append(text))
        with patch.object(mouse,'position',return_value=((696,443),(1280,800))), patch.object(mouse,'wait_position') as wait:
            mouse.command('mouse_move -383 -263')
        self.assertEqual(['mouse_move -383 -263'],calls);wait.assert_called_once_with((313,180))

if __name__=='__main__':unittest.main()
