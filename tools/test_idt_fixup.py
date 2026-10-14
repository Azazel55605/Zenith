#!/usr/bin/env python3
"""Verify the real released IDT import adaptation and reject signature drift."""
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]

class IdtFixupTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        packages=Path(os.environ.get('NUGET_PACKAGES',Path.home()/'.nuget/packages'))
        cls.source=packages/'cosmos.kernel.core.x64/3.0.89/lib/net10.0/Cosmos.Kernel.Core.X64.dll'
        cls.tool=ROOT/'tools/PatcherFixup/bin/Release/net10.0/PatcherFixup.dll'
        if not cls.source.is_file() or not cls.tool.is_file():
            raise RuntimeError('build the Release kernel first (released Cosmos 3.0.89 and PatcherFixup required)')

    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.copy=Path(self.temp.name)/self.source.name;shutil.copyfile(self.source,self.copy)

    def run_fixup(self):
        return subprocess.run(['dotnet',str(self.tool),str(self.copy)],capture_output=True,text=True)

    def test_released_import_is_redirected_without_touching_package_cache(self):
        original=hashlib.sha256(self.source.read_bytes()).hexdigest()
        result=self.run_fixup()
        self.assertEqual(0,result.returncode,result.stderr)
        self.assertIn('Adapted IDT loader: preserve interrupt state',result.stdout)
        self.assertIn(b'zenith_load_idt_preserve_irq',self.copy.read_bytes())
        self.assertEqual(original,hashlib.sha256(self.source.read_bytes()).hexdigest())

    def test_already_adapted_import_is_byte_identical(self):
        self.assertEqual(0,self.run_fixup().returncode)
        before=self.copy.read_bytes();second=self.run_fixup()
        self.assertEqual(0,second.returncode,second.stderr)
        self.assertNotIn('Adapted IDT loader',second.stdout)
        self.assertEqual(before,self.copy.read_bytes())

    def test_unknown_native_import_is_rejected_without_rewriting(self):
        raw=self.copy.read_bytes()
        old=b'_native_x64_load_idt';new=b'_native_x64_load_bad'
        self.assertEqual(len(old),len(new));self.assertIn(old,raw)
        self.copy.write_bytes(raw.replace(old,new));before=self.copy.read_bytes()
        result=self.run_fixup()
        self.assertNotEqual(0,result.returncode)
        self.assertIn('Cosmos IDT loader signature changed',result.stderr)
        self.assertEqual(before,self.copy.read_bytes())

if __name__=='__main__':unittest.main()
