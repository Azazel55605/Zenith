#!/usr/bin/env python3
import importlib.util
from pathlib import Path
import subprocess
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
