#!/usr/bin/env python3
"""Pinned connect-timeout retirement: real dependency, drift refusal and idempotence."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]

class TcpConnectFixupTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        for source in Path(os.environ.get('ZENITH_FIXUP_REFERENCES', ROOT / 'output-x64')).glob('*.dll'):
            shutil.copyfile(source, self.directory / source.name)
        packages = Path(os.environ.get('NUGET_PACKAGES', Path.home()/'.nuget/packages'))
        self.source = packages/'cosmos.kernel.plugs/3.0.89/runtimes/linux-x64/lib/net10.0/Cosmos.Kernel.Plugs.dll'
        self.copy = self.directory / self.source.name
        shutil.copyfile(self.source, self.copy)
        self.tool = ROOT/'tools/PatcherFixup/bin/Release/net10.0/PatcherFixup.dll'

    def run_fixup(self):
        return subprocess.run(['dotnet', str(self.tool), str(self.copy)], cwd=self.directory,
                              capture_output=True, text=True)

    def system_copy(self):
        packages = Path(os.environ.get('NUGET_PACKAGES', Path.home()/'.nuget/packages'))
        self.source = packages/'cosmos.kernel.system/3.0.89/runtimes/linux-x64/lib/net10.0/Cosmos.Kernel.System.dll'
        self.copy = self.directory / self.source.name
        shutil.copyfile(self.source, self.copy)

    def test_real_port_candidate_adaptation_preserves_package(self):
        self.system_copy(); original = self.source.read_bytes()
        result = self.run_fixup()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('Adapted TCP port candidates', result.stdout)
        self.assertEqual(original, self.source.read_bytes())

    def test_seeded_port_adaptation_is_byte_idempotent(self):
        self.system_copy(); result = self.run_fixup()
        self.assertEqual(0, result.returncode, result.stderr)
        before = self.copy.read_bytes(); result = self.run_fixup()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertNotIn('Adapted TCP port candidates', result.stdout)
        self.assertEqual(before, self.copy.read_bytes())

    def test_changed_port_contract_is_not_rewritten(self):
        self.system_copy(); before = self.copy.read_bytes()
        self.assertIn(b's_nextPort', before)
        altered = before.replace(b's_nextPort', b'x_nextPort'); self.copy.write_bytes(altered)
        result = self.run_fixup()
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(altered, self.copy.read_bytes())

    def test_real_timeout_retirement_and_unchanged_package(self):
        original = self.source.read_bytes(); result = self.run_fixup()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('Adapted TCP connect timeout', result.stdout)
        self.assertEqual(original, self.source.read_bytes())

    def test_second_adaptation_is_byte_identical(self):
        result = self.run_fixup(); self.assertEqual(0, result.returncode, result.stderr)
        before = self.copy.read_bytes(); result = self.run_fixup()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertNotIn('Adapted TCP connect timeout', result.stdout)
        self.assertEqual(before, self.copy.read_bytes())

    def test_changed_failure_contract_is_not_rewritten(self):
        before = self.copy.read_bytes()
        old = 'Failed to open TCP connection!'.encode('utf-16le')
        new = 'Failed to open BAD connection!'.encode('utf-16le')
        self.assertEqual(len(old), len(new)); self.assertIn(old, before)
        altered = before.replace(old, new); self.copy.write_bytes(altered)
        result = self.run_fixup(); self.assertNotEqual(0, result.returncode)
        self.assertEqual(altered, self.copy.read_bytes())

if __name__ == '__main__': unittest.main()
