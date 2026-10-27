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
    def test_movement_waits_for_observed_destination(self):
        calls=[];mouse=profile.RenderedMouse('/tmp/m.sock',lambda path,text:calls.append(text))
        with patch.object(mouse,'position',return_value=((696,443),(1280,800))), patch.object(mouse,'wait_position') as wait:
            mouse.command('mouse_move -383 -263')
        self.assertEqual(['mouse_move -383 -263'],calls);wait.assert_called_once_with((313,180))

if __name__=='__main__':unittest.main()
