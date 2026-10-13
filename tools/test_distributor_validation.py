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
                           "shutil.copyfile(Path(__file__).parent / 'source.img', Path(sys.argv[1]))\n")
        subprocess.run([sys.executable, '-B', str(ROOT / 'deploy/distributor/validation-runner.py'),
                        str(harness), str(self.destination)], check=True)
        self.assertEqual(self.source.read_bytes(), self.destination.read_bytes())
        self.assertLess(self.destination.stat().st_blocks * 512, self.destination.stat().st_size // 4)

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

if __name__ == '__main__': unittest.main()
