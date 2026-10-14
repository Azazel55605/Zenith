#!/usr/bin/env python3
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('profile',ROOT/'deploy/distributor/validation-profile.py')
profile=importlib.util.module_from_spec(spec);spec.loader.exec_module(profile)

class ProfileTests(unittest.TestCase):
    def machine(self,outcome):
        temporary=tempfile.TemporaryDirectory();self.addCleanup(temporary.cleanup)
        directory=Path(temporary.name)
        class Machine:
            def __init__(self):
                self.directory=directory;self.serial=directory/'serial.log';self.serial.write_text('private guest data')
            def wait(self,marker,offset=0,timeout=180):
                if isinstance(outcome,Exception):raise outcome
                return outcome
        profile.install(Machine);return Machine()

    def test_result_and_original_arguments_remain_unchanged_without_private_marker_output(self):
        m=self.machine(True)
        with patch.object(profile,'acceleration',return_value='kvm'):
            self.assertTrue(m.wait('a private marker value',7,21))
        raw=(m.directory/'wait-times.jsonl').read_text();row=json.loads(raw)
        self.assertEqual(('guest-check','passed','kvm',21),(row['label'],row['outcome'],row['accelerator'],row['timeout']))
        self.assertNotIn('private',raw)

    def test_failed_wait_still_fails(self):
        m=self.machine(False)
        with patch.object(profile,'acceleration',return_value='tcg'):
            self.assertFalse(m.wait('user: K_INSTALL=',0,600))
        row=json.loads((m.directory/'wait-times.jsonl').read_text())
        self.assertEqual(('K_INSTALL','failed'),(row['label'],row['outcome']))

    def test_plain_repository_status_has_a_safe_phase_label(self):
        self.assertEqual('P4_KERNEL_STAGE',profile.label('P4_KERNEL_STAGE=0'))
        self.assertEqual('guest-check',profile.label('pkg update --yes'))

    def test_resource_deltas_are_observations_only(self):
        m=self.machine(False)
        with patch.object(profile,'resources',side_effect=[dict(cpu_seconds=2,read_bytes=100,write_bytes=30),
                                                          dict(cpu_seconds=3.25,read_bytes=612,write_bytes=1054)]):
            self.assertFalse(m.wait('P4_KERNEL_STAGE=0'))
        row=json.loads((m.directory/'wait-times.jsonl').read_text())
        self.assertEqual((1.25,512,1024),(row['qemu_cpu_seconds'],row['host_read_bytes'],row['host_write_bytes']))

    def test_original_exception_is_not_masked_by_observation(self):
        m=self.machine(RuntimeError('original failure'))
        with patch.object(profile,'acceleration',return_value='unknown'):
            with self.assertRaisesRegex(RuntimeError,'original failure'):m.wait('headless ready')
        self.assertEqual('error',json.loads((m.directory/'wait-times.jsonl').read_text())['outcome'])

    def test_observation_write_failure_does_not_change_acceptance(self):
        m=self.machine(True);(m.directory/'wait-times.jsonl').mkdir()
        with patch.object(profile,'acceleration',return_value='unknown'):
            self.assertTrue(m.wait('headless ready'))

    def test_unknown_accelerator_is_not_claimed_as_kvm(self):
        m=self.machine(True)
        class Process:pid=2147483648
        m.process=Process()
        self.assertEqual('unknown',profile.acceleration(m))
        (m.directory/'qemu.log').write_text('qemu-system-x86_64: falling back to tcg\n')
        # No /proc entry is available: still cannot assert an actual KVM VM.
        self.assertNotEqual('kvm',profile.acceleration(m))

if __name__=='__main__':unittest.main()
