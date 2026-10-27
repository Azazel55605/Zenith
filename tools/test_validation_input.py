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

if __name__=='__main__':unittest.main()
