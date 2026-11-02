#!/usr/bin/env python3
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import types
import unittest
from unittest.mock import Mock

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('accelerator',ROOT/'deploy/distributor/validation-accelerator.py')
a=importlib.util.module_from_spec(spec);spec.loader.exec_module(a)

class Tests(unittest.TestCase):
    def test_auto_preserves_original_policy(self):
        args=['qemu','-accel','kvm','-accel','tcg','-cpu','max','-drive','file=scratch.img']
        self.assertIs(args,a.arguments(args,'auto'))

    def test_forced_mode_has_no_silent_fallback_and_keeps_devices(self):
        args=['qemu','-accel','kvm','-accel','tcg','-cpu','max','-drive','file=scratch.img']
        for mode in ('kvm','tcg'):
            self.assertEqual(['qemu','-accel',mode,'-cpu','max','-drive','file=scratch.img'],a.arguments(args,mode))
        self.assertEqual(2,args.count('-accel'))

    def test_vm_gets_a_private_monitor_other_programs_do_not(self):
        original=types.SimpleNamespace(**vars(subprocess));child=Mock();child.poll=Mock(return_value=0);original.Popen=Mock(return_value=child)
        module=types.SimpleNamespace(subprocess=original)
        a.install(module,'auto')
        module.subprocess.Popen(['qemu-img','create'])
        original.Popen.assert_called_with(['qemu-img','create'])
        self.assertIs(child,module.subprocess.Popen(['qemu-system-x86_64','-m','1G'],stdout=17))
        argv=original.Popen.call_args.args[0]
        self.assertEqual(['qemu-system-x86_64','-m','1G','-monitor'],argv[:4])
        self.assertTrue(argv[4].startswith('unix:') and argv[4].endswith(',server,nowait'))
        self.assertEqual(17,original.Popen.call_args.kwargs['stdout'])

    def test_a_crashed_guest_is_reported_from_its_serial_log(self):
        with tempfile.TemporaryDirectory() as directory:
            self.assertIsNone(a.crash_report(directory))
            log=Path(directory)/'installed';log.mkdir();log=log/'serial.log'
            log.write_bytes(b'[zenith] fine\n')
            self.assertIsNone(a.crash_report(directory))
            log.write_bytes(b'[zenith] fine\n=====\nCPU EXCEPTION: Page Fault (#PF)\n  RIP: 0x0\nSystem halted.\n')
            report=a.crash_report(directory)
            self.assertIn('Page Fault',report);self.assertIn('RIP: 0x0',report);self.assertNotIn('fine',report)

    def test_bad_policy_and_incomplete_arguments_refused(self):
        with self.assertRaises(ValueError): a.arguments(['qemu'],'whatever')
        with self.assertRaises(ValueError): a.arguments(['qemu','-accel'],'tcg')

    def test_module_binding_only_and_popen_arguments_preserved(self):
        original=types.SimpleNamespace(**vars(subprocess)); original.Popen=Mock(return_value='child')
        module=types.SimpleNamespace(subprocess=original)
        a.install(module,'tcg')
        self.assertIsNot(module.subprocess,original)
        self.assertEqual('child',module.subprocess.Popen(['qemu','-accel','kvm'],stdout=17))
        original.Popen.assert_called_once_with(['qemu','-accel','tcg'],stdout=17)
        self.assertIs(module.subprocess.run,subprocess.run)

if __name__=='__main__': unittest.main()
