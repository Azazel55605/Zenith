#!/usr/bin/env python3
"""Independent sparse native images, unchanged harness execution and phase metrics."""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]

def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec); spec.loader.exec_module(result)
    return result

runner = module('validation_runner', ROOT / 'deploy/distributor/validation-runner.py')
release = module('release', ROOT / 'deploy/distributor/release.py')
output = module('build_output', ROOT / 'deploy/distributor/build_output.py')
network = module('validation_network', ROOT / 'deploy/distributor/validation-network.py')

class ValidationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / 'source.img'
        with self.source.open('wb') as output:
            output.write(b'GPT fixture'); output.seek(32 * 1024 * 1024); output.write(b'ext2 fixture')
        self.destination = self.root / 'copy.img'

    def test_sparse_copy_has_identical_content_and_independent_writes(self):
        runner.copyfile(self.source, self.destination)
        self.assertEqual(self.source.read_bytes(), self.destination.read_bytes())
        self.assertNotEqual(self.source.stat().st_ino, self.destination.stat().st_ino)
        self.assertLess(self.destination.stat().st_blocks * 512, self.destination.stat().st_size // 4)
        with self.destination.open('r+b') as output: output.write(b'changed')
        self.assertTrue(self.source.read_bytes().startswith(b'GPT fixture'))
        with self.source.open('r+b') as output: output.seek(32 * 1024 * 1024); output.write(b'original')
        self.assertTrue(self.destination.read_bytes().endswith(b'ext2 fixture'))

    def test_existing_image_is_truncated_and_same_file_is_refused(self):
        self.destination.write_bytes(b'x' * (33 * 1024 * 1024))
        runner.copyfile(self.source, self.destination)
        self.assertEqual(self.source.stat().st_size, self.destination.stat().st_size)
        with self.assertRaises(shutil.SameFileError): runner.copyfile(self.source, self.source)
        self.destination.unlink(); os.link(self.source, self.destination)
        with self.assertRaises(shutil.SameFileError): runner.copyfile(self.source, self.destination)

    def test_directory_and_symlink_semantics_preserved(self):
        with self.assertRaises(IsADirectoryError): runner.copyfile(self.source, self.root)
        link = self.root / 'link.img'; link.symlink_to(self.source)
        runner.copyfile(link, self.destination, follow_symlinks=False)
        self.assertTrue(self.destination.is_symlink())
        self.destination.unlink()
        with patch.object(runner.subprocess, 'run') as cp:
            runner.copyfile(link, self.destination)
            cp.assert_not_called()
        self.assertEqual(self.source.read_bytes(), self.destination.read_bytes())

    def test_readonly_source_still_produces_writable_clone(self):
        self.source.chmod(0o444)
        runner.copyfile(self.source, self.destination)
        self.assertTrue(self.destination.stat().st_mode & 0o200)

    def test_missing_or_failed_cp_falls_back_without_changing_content(self):
        with patch.object(runner.shutil, 'which', return_value=None):
            runner.copyfile(self.source, self.destination)
        self.assertEqual(self.source.read_bytes(), self.destination.read_bytes())
        with patch.object(runner.subprocess, 'run', return_value=subprocess.CompletedProcess([], 1)):
            runner.copyfile(self.source, self.destination)
        self.assertEqual(self.source.read_bytes(), self.destination.read_bytes())
        with patch.object(runner.subprocess, 'run', side_effect=FileNotFoundError):
            runner.copyfile(self.source, self.destination)
        self.assertEqual(self.source.read_bytes(), self.destination.read_bytes())

    def test_unchanged_harness_receives_arguments_imports_and_sparse_copies(self):
        (self.root / 'fixture_helper.py').write_text('VALUE = 7\n')
        harness = self.root / 'harness.py'
        harness.write_text("import fixture_helper, shutil, sys\nfrom pathlib import Path\n"
                           "assert fixture_helper.VALUE == 7\n"
                           "assert sys.argv[2:] == ['--fixture-argument']\n"
                           "shutil.copyfile(Path(__file__).parent / 'source.img', Path(sys.argv[1]))\n")
        subprocess.run([sys.executable, '-B', str(ROOT / 'deploy/distributor/validation-runner.py'),
                        str(harness), str(self.destination), '--fixture-argument'], check=True)
        self.assertEqual(self.source.read_bytes(), self.destination.read_bytes())
        self.assertLess(self.destination.stat().st_blocks * 512, self.destination.stat().st_size // 4)

    def test_dhcp_probe_requires_actual_online_state_and_expected_address(self):
        from unittest.mock import Mock
        machine=Mock(); machine.process.poll.return_value=None; machine.log.return_value='boot log'
        calls=[]
        def original(instance,marker,offset,timeout):
            calls.append((marker,offset,timeout));return marker=='user: DHCP_PROBE_fixture_OK'
        with patch.object(network.secrets,'token_hex',return_value='fixture'):
            self.assertTrue(network.wait(machine,'network: online address=10.0.2.15',0,90,original))
        command=machine.send.call_args.args[0]
        self.assertIn("grep 'mode=dhcp state=online'",command)
        self.assertIn("grep '  address=10.0.2.15 '",command)
        self.assertEqual(('user: DHCP_PROBE_fixture_OK',len('boot log'),30),calls[-1])
        self.assertNotIn('netcfg',command)
        self.assertNotIn('fetch',command)

    def test_failed_dhcp_probe_dead_guest_other_markers_and_offsets_still_fail(self):
        from unittest.mock import Mock
        machine=Mock();machine.process.poll.return_value=None;machine.log.return_value='boot log'
        self.assertFalse(network.wait(machine,'network: online address=10.0.2.15',0,90,lambda *args:False))
        machine.send.assert_called_once();machine.send.reset_mock()
        for marker,offset in [('headless ready',0),('network: online address=10.0.2.15',10),
                              ('network: online address=999.0.2.15',0)]:
            self.assertFalse(network.wait(machine,marker,offset,90,lambda *args:False))
        machine.process.poll.return_value=1
        self.assertFalse(network.wait(machine,'network: online address=10.0.2.15',0,90,lambda *args:False))
        machine.send.assert_not_called()

    def test_absent_online_transition_retains_full_startup_deadline(self):
        from unittest.mock import Mock
        machine=Mock();machine.process.poll.return_value=None;machine.log.return_value='no online transition'
        calls=[]
        def original(instance,marker,offset,timeout):calls.append(timeout);return False
        with patch.object(network.time,'monotonic',side_effect=[10,13]):
            self.assertFalse(network.wait(machine,'network: online address=10.0.2.15',0,90,original))
        self.assertEqual([3,87,30],calls)

    def test_intact_dhcp_marker_needs_no_probe(self):
        from unittest.mock import Mock
        machine=Mock()
        self.assertTrue(network.wait(machine,'network: online address=10.0.2.15',0,90,lambda *args:True))
        machine.send.assert_not_called()

    def test_failed_boot_reaps_vm_without_rewriting_tagged_sources(self):
        helper=self.root/'headless-smoke.py'
        helper.write_text("from pathlib import Path\nclass Machine:\n"
            " def __init__(self): pass\n"
            " def wait(self,marker,offset=0,timeout=180): return False\n"
            " def send(self,text): pass\n"
            " def log(self): return ''\n"
            " def stop(self): Path(__file__).with_name('reaped').write_text('yes')\n")
        harness=self.root/'harness.py'
        harness.write_text("import importlib.util\nfrom pathlib import Path\n"
            "spec=importlib.util.spec_from_file_location('headless',Path(__file__).with_name('headless-smoke.py'))\n"
            "h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)\n"
            "machine=h.Machine();assert machine.wait('headless ready')\n")
        before=(harness.read_bytes(),helper.read_bytes())
        result=subprocess.run([sys.executable,'-B',str(ROOT/'deploy/distributor/validation-runner.py'),
            str(harness),str(self.destination)],capture_output=True)
        self.assertNotEqual(0,result.returncode)
        self.assertEqual('yes',(self.root/'reaped').read_text())
        self.assertEqual(before,(harness.read_bytes(),helper.read_bytes()))

    def test_native_repository_failure_precedes_long_kernel_matrix_and_retains_evidence(self):
        repo=self.root/'repo';(repo/'tools').mkdir(parents=True)
        for name in ('native-process-smoke.py','login-session-smoke.py'):
            (repo/'tools'/name).touch()
        scratch=self.root/'native-scratch';scratch.mkdir()
        work=self.root/'release';work.mkdir()
        calls=[]
        def phase(name,args,log):
            calls.append(name)
            if name == 'Native repository-smoke.py':
                (scratch/'failure-evidence.txt').write_text('retained')
                raise ValueError('repository failure')
        with patch.object(release,'REPO',repo), patch.object(release.tempfile,'mkdtemp',return_value=str(scratch)), \
             patch.object(release.maintenance,'register_native'), patch.object(release,'phase',side_effect=phase):
            with self.assertRaisesRegex(ValueError,'repository failure'):
                release.native_checks(work)
        self.assertEqual(['Native login-session-smoke.py','Native repository-smoke.py'],calls)
        self.assertEqual('retained',(work/'native/failure-evidence.txt').read_text())

    def test_successful_native_release_keeps_every_gate_with_kernel_matrix_last(self):
        repo=self.root/'repo';(repo/'tools').mkdir(parents=True)
        for name in ('native-process-smoke.py','login-session-smoke.py'):
            (repo/'tools'/name).touch()
        scratch=self.root/'native-scratch';scratch.mkdir()
        work=self.root/'release';work.mkdir()
        with patch.object(release,'REPO',repo), patch.object(release.tempfile,'mkdtemp',return_value=str(scratch)), \
             patch.object(release.maintenance,'register_native'), patch.object(release,'phase') as phase:
            release.native_checks(work)
        self.assertEqual(['Native login-session-smoke.py','Native repository-smoke.py',
                          'Native native-process-smoke.py','Native kernel-update-smoke.py'],
                         [call.args[0] for call in phase.call_args_list])

    def test_auth_policy_applies_to_unchanged_tag_without_helper_dependency(self):
        helper=self.root/'headless-smoke.py'
        helper.write_text("class Machine:\n"
            " def __init__(self): self.text=''; self.calls=0; self.process=self\n"
            " def wait(self,marker,offset=0,timeout=180):\n"
            "  self.calls+=1\n"
            "  if self.calls>=8:self.text='password updated for root\\n'\n"
            "  return marker in self.text\n"
            " def log(self):return self.text\n"
            " def send(self,text):raise AssertionError('credentials must not be repeated')\n"
            " def poll(self):return None\n"
            " def stop(self):pass\n")
        harness=self.root/'harness.py'
        harness.write_text("import importlib.util\nfrom pathlib import Path\n"
            "p=Path(__file__).with_name('headless-smoke.py')\n"
            "spec=importlib.util.spec_from_file_location('old_headless',p)\n"
            "h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)\n"
            "m=h.Machine()\nassert m.wait('password updated for root',timeout=30)\n"
            "assert m.calls==8\nassert m._validation_auth['effective_seconds']==180\n"
            "assert m._validation_auth['phase']=='password-update'\n")
        before=(helper.read_bytes(),harness.read_bytes())
        subprocess.run([sys.executable,'-B',str(ROOT/'deploy/distributor/validation-runner.py'),
                        str(harness),str(self.destination)],check=True)
        self.assertEqual(before,(helper.read_bytes(),harness.read_bytes()))

    def test_phase_metrics_record_success_and_failure_without_hiding_error(self):
        with patch.object(release, 'REPO', self.root):
            release.phase('success', [sys.executable, '-c', "print('ok')"], self.root / 'ok.log')
            with self.assertRaisesRegex(ValueError, 'failure failed'):
                release.phase('failure', [sys.executable, '-c', 'raise SystemExit(3)'], self.root / 'fail.log')
        rows = json.loads((self.root / 'phase-times.json').read_text())
        self.assertEqual(['passed', 'failed'], [row['status'] for row in rows])
        self.assertTrue(all(row['seconds'] >= 0 for row in rows))
        (self.root / 'phase-times.json').write_text('{}')
        release.phase_timing(self.root / 'fail.log', 'failure', time.monotonic(), 'failed')
        self.assertEqual('{}', (self.root / 'phase-times.json').read_text())

    def test_dashboard_snapshot_is_bounded_and_excludes_guest_serial(self):
        path = self.root / 'build.details.json'; log = self.root / 'phase.log'
        log.write_text('x' * 50000 + '\nok rollback verified\n')
        keep = self.root / 'native/k'; guest = keep / 'previous'; guest.mkdir(parents=True)
        (guest / 'qemu.log').write_text('KVM permission denied; falling back to tcg\n')
        (guest / 'serial.log').write_text('private password input')
        reporter = output.Reporter(path); reporter.start('Native kernel-update-smoke.py')
        reporter.update(log, ['runner', str(keep)], 1600, force=True)
        row = json.loads(path.read_text())['phases'][0]
        self.assertLessEqual(len(row['text']), output.LIMIT)
        self.assertIn('rollback verified', row['text']); self.assertIn('falling back', row['diagnostic'])
        self.assertNotIn('private password', path.read_text())
        self.assertEqual(0o640, path.stat().st_mode & 0o777)
        reporter.update(log, ['runner', str(keep)], 1601, 'passed', force=True)
        self.assertEqual('passed', json.loads(path.read_text())['phases'][0]['status'])

    def test_snapshot_failure_does_not_fail_build(self):
        reporter = output.Reporter(self.root / 'missing/build.details.json'); reporter.start('Build')
        log = self.root / 'phase.log'; log.write_text('output')
        reporter.update(log, [], 1, force=True)

    def test_release_phase_exports_actual_child_output_and_failure(self):
        path = self.root / 'build.details.json'
        with patch.dict(os.environ, ZENITH_BUILD_OUTPUT=str(path)), \
             patch.object(release.build_output, 'reporter', None), patch.object(release, 'REPO', self.root):
            release.phase('Tests', [sys.executable, '-c', "print('ok real child output')"], self.root / 'tests.log')
            with self.assertRaisesRegex(ValueError, 'Compile failed'):
                release.phase('Compile', [sys.executable, '-c', "print('actual compiler failure'); raise SystemExit(2)"], self.root / 'build.log')
        rows = json.loads(path.read_text())['phases']
        self.assertEqual(['passed', 'failed'], [row['status'] for row in rows])
        self.assertIn('real child output', rows[0]['text']); self.assertIn('compiler failure', rows[1]['text'])

if __name__ == '__main__': unittest.main()
