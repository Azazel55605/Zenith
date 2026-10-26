#!/usr/bin/env python3
"""Fail-fast acceptance orchestration and parity with the distributor gates."""
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    return module


validate = load('acceptance_test', ROOT / 'deploy/distributor/validate.py')
release = load('acceptance_release_test', ROOT / 'deploy/distributor/release.py')


class AcceptanceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / 'output-x64').mkdir()
        for name in ('Zenith.iso', 'Zenith.elf'): (self.root / 'output-x64' / name).write_bytes(b'fixture')
        self.keep = self.root / 'evidence'

    def test_controller_failure_prevents_host_build_and_native_work(self):
        engine = Mock()
        with patch.object(validate, 'ROOT', self.root), patch.object(validate.subprocess, 'run',
                side_effect=subprocess.CalledProcessError(1, 'controller')):
            with self.assertRaises(subprocess.CalledProcessError): validate.acceptance(engine, self.keep)
        engine.phase.assert_not_called(); engine.native_checks.assert_not_called()
        self.assertTrue((self.keep / 'controller-tests.log').exists())

    def test_native_environment_failure_prevents_expensive_build(self):
        engine = Mock(); engine.native_prerequisites.side_effect = ValueError('missing firmware')
        with patch.object(validate, 'ROOT', self.root), patch.object(validate.subprocess, 'run'):
            with self.assertRaisesRegex(ValueError, 'firmware'): validate.acceptance(engine, self.keep)
        engine.phase.assert_not_called(); engine.native_checks.assert_not_called()

    def test_default_builds_then_calls_actual_release_gate_plan_without_publishing(self):
        engine = Mock()
        with patch.object(validate, 'ROOT', self.root), patch.object(validate.subprocess, 'run'):
            validate.acceptance(engine, self.keep)
        self.assertEqual(['Host tests', 'Kernel build', 'Package tool'],
                         [call.args[0] for call in engine.phase.call_args_list])
        engine.native_checks.assert_called_once_with(self.keep)
        engine.publish.assert_not_called(); engine.indexes.assert_not_called()

    def test_existing_artifacts_skip_only_kernel_compilation(self):
        engine = Mock()
        with patch.object(validate, 'ROOT', self.root), patch.object(validate.subprocess, 'run'):
            validate.acceptance(engine, self.keep, existing=True)
        self.assertEqual(['Host tests', 'Package tool'], [call.args[0] for call in engine.phase.call_args_list])
        engine.native_checks.assert_called_once_with(self.keep)

    def test_missing_artifact_or_failed_host_test_blocks_native_acceptance(self):
        (self.root / 'output-x64/Zenith.iso').unlink()
        engine = Mock()
        with patch.object(validate, 'ROOT', self.root), patch.object(validate.subprocess, 'run'):
            with self.assertRaisesRegex(ValueError, 'missing built artifact'):
                validate.acceptance(engine, self.keep, existing=True)
        engine.native_checks.assert_not_called()
        self.assertTrue((self.keep / 'controller-tests.log').exists())

    def test_failed_host_tests_stop_before_building_or_booting(self):
        engine = Mock(); engine.phase.side_effect = ValueError('host tests failed')
        with patch.object(validate, 'ROOT', self.root), patch.object(validate.subprocess, 'run'):
            with self.assertRaisesRegex(ValueError, 'host tests failed'):
                validate.acceptance(engine, self.keep)
        self.assertEqual(['Host tests'], [call.args[0] for call in engine.phase.call_args_list])
        engine.native_checks.assert_not_called()

    def test_changed_artifacts_are_refused_after_native_checks(self):
        engine = Mock()
        engine.native_checks.side_effect = lambda keep: (self.root / 'output-x64/Zenith.elf').write_bytes(b'changed')
        with patch.object(validate, 'ROOT', self.root), patch.object(validate.subprocess, 'run'):
            with self.assertRaisesRegex(ValueError, 'artifact changed during acceptance'):
                validate.acceptance(engine, self.keep, existing=True)
        self.assertTrue((self.keep / 'artifacts.json').exists())

    def test_existing_evidence_is_never_overwritten(self):
        self.keep.mkdir(); (self.keep / 'failure.log').write_text('original')
        with self.assertRaises(FileExistsError): validate.acceptance(Mock(), self.keep)
        self.assertEqual('original', (self.keep / 'failure.log').read_text())

    def test_dirty_candidate_and_source_movement_cannot_pass_pre_tag_validation(self):
        engine = Mock(); engine.clean.side_effect = ValueError('dirty checkout')
        with self.assertRaisesRegex(ValueError, 'dirty checkout'):
            validate.acceptance(engine, self.keep)
        engine.phase.assert_not_called(); engine.native_checks.assert_not_called()
        engine = Mock(); engine.source_commit.side_effect = ['a' * 40, 'b' * 40]
        with patch.object(validate, 'ROOT', self.root), patch.object(validate.subprocess, 'run'):
            with self.assertRaisesRegex(ValueError, 'source commit changed'):
                validate.acceptance(engine, self.root / 'moved')

    def test_controller_tests_use_running_checkout_and_required_authentication_regressions(self):
        with patch.object(validate.subprocess, 'run') as run:
            validate.controller_tests()
        args, kwargs = run.call_args
        self.assertIn('tools/test_guest_auth.py', args[0])
        self.assertIn('tools/test_release_acceptance.py', args[0])
        self.assertIn('tools/test_distributor_poll.py', args[0])
        self.assertIn('tools/test_distributor_release.py', args[0])
        self.assertEqual(ROOT, kwargs['cwd'])
        self.assertTrue(kwargs['env']['PYTHONPATH'].startswith(str(ROOT / 'tools')))

    def test_old_tag_cannot_replace_current_controller_test_command(self):
        with patch.object(release, 'REPO', self.root), patch.object(release, 'phase') as phase:
            release.controller_checks(self.keep)
        self.assertEqual(str(ROOT / 'deploy/distributor/validate.py'), phase.call_args.args[1][2])
        self.assertIn('--controller-only', phase.call_args.args[1])

    def test_missing_native_tools_and_invalid_acceleration_fail_before_importing_harness(self):
        with patch.object(release.shutil, 'which', return_value=None):
            with self.assertRaisesRegex(ValueError, 'missing native validation tools'): release.native_prerequisites()
        with patch.object(release.shutil, 'which', return_value='/fixture/tool'), \
             patch.dict(os.environ, ZENITH_VALIDATION_ACCELERATOR='invalid'):
            with self.assertRaisesRegex(ValueError, 'must be auto'): release.native_prerequisites()

    def test_forced_kvm_permission_error_is_reported_before_build(self):
        with patch.object(release.shutil, 'which', return_value='/fixture/tool'), \
             patch.dict(os.environ, ZENITH_VALIDATION_ACCELERATOR='kvm'), \
             patch.object(release.os, 'open', side_effect=PermissionError('permission denied')):
            with self.assertRaisesRegex(ValueError, 'forced KVM is unavailable'): release.native_prerequisites()

    def test_missing_firmware_is_reported_and_import_path_is_restored(self):
        import sys
        paths = list(sys.path); firmware = Mock()
        firmware.firmware.side_effect = RuntimeError('missing OVMF firmware')
        with patch.object(release.shutil, 'which', return_value='/fixture/tool'), \
             patch.dict(os.environ, ZENITH_VALIDATION_ACCELERATOR='tcg'), \
             patch.object(release.importlib.util, 'spec_from_file_location', return_value=Mock()), \
             patch.object(release.importlib.util, 'module_from_spec', return_value=firmware):
            with self.assertRaisesRegex(ValueError, 'firmware unavailable.*OVMF'): release.native_prerequisites()
        self.assertEqual(paths, sys.path)


if __name__ == '__main__': unittest.main()
