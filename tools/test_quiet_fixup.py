#!/usr/bin/env python3
"""Real pinned allocator trace adaptation, idempotence and drift refusal."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]

class QuietTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        packages=Path(os.environ.get('NUGET_PACKAGES',Path.home()/'.nuget/packages'))
        self.source=packages/'cosmos.kernel.core/3.0.89/runtimes/linux-x64/lib/net10.0/Cosmos.Kernel.Core.dll'
        self.copy=Path(self.temp.name)/self.source.name;shutil.copyfile(self.source,self.copy)
        self.tool=ROOT/'tools/PatcherFixup/bin/Release/net10.0/PatcherFixup.dll'

    def run_fixup(self):
        return subprocess.run(['dotnet',str(self.tool),str(self.copy)],capture_output=True,text=True)

    def test_real_released_trace_adapts_without_modifying_the_dependency(self):
        original=self.source.read_bytes();result=self.run_fixup()
        self.assertEqual(0,result.returncode,result.stderr)
        self.assertIn('Quieted pinned per-allocation success trace',result.stdout)
        self.assertNotEqual(original,self.copy.read_bytes())
        self.assertEqual(original,self.source.read_bytes())

    def test_second_adaptation_is_byte_identical(self):
        self.assertEqual(0,self.run_fixup().returncode)
        before=self.copy.read_bytes();self.assertEqual(0,self.run_fixup().returncode)
        self.assertEqual(before,self.copy.read_bytes())

    def test_unknown_trace_contract_is_refused_without_writing(self):
        original=self.copy.read_bytes()
        old='[PageAllocator] AllocPages - Type: '.encode('utf-16le')
        new='[PageAllocator] AllocPages - BADD: '.encode('utf-16le')
        self.assertEqual(len(old),len(new));self.assertIn(old,original)
        changed=original.replace(old,new);self.copy.write_bytes(changed)
        self.assertNotEqual(0,self.run_fixup().returncode)
        self.assertEqual(changed,self.copy.read_bytes())

    def test_unknown_wait_trace_is_refused_without_writing(self):
        original=self.copy.read_bytes()
        old='[CV] WaitTimeout BEGIN thread='.encode('utf-16le')
        new='[CV] WaitTimeout BADD! thread='.encode('utf-16le')
        self.assertEqual(len(old),len(new));self.assertIn(old,original)
        changed=original.replace(old,new);self.copy.write_bytes(changed)
        self.assertNotEqual(0,self.run_fixup().returncode)
        self.assertEqual(changed,self.copy.read_bytes())


class MonitorQuietTests(QuietTests):
    def setUp(self):
        super().setUp()
        for source in (ROOT/'output-x64').glob('*.dll'):
            shutil.copyfile(source,Path(self.temp.name)/source.name)
        packages=Path(os.environ.get('NUGET_PACKAGES',Path.home()/'.nuget/packages'))
        self.source=packages/'cosmos.kernel.plugs/3.0.89/runtimes/linux-x64/lib/net10.0/Cosmos.Kernel.Plugs.dll'
        self.copy=Path(self.temp.name)/self.source.name;shutil.copyfile(self.source,self.copy)

    def run_fixup(self):
        return subprocess.run(['dotnet',str(self.tool),str(self.copy)],cwd=self.temp.name,capture_output=True,text=True)

    def test_real_released_trace_adapts_without_modifying_the_dependency(self):
        original=self.source.read_bytes();result=self.run_fixup()
        self.assertEqual(0,result.returncode,result.stderr)
        self.assertIn('Quieted pinned monitor interop success traces',result.stdout)
        self.assertNotEqual(original,self.copy.read_bytes())
        self.assertEqual(original,self.source.read_bytes())

    def test_unknown_trace_contract_is_refused_without_writing(self):
        original=self.copy.read_bytes()
        old='[LowLevelMonitor] Acquire BEGIN\n'.encode('utf-16le')
        new='[LowLevelMonitor] Acquire BADD!\n'.encode('utf-16le')
        self.assertEqual(len(old),len(new));self.assertIn(old,original)
        changed=original.replace(old,new);self.copy.write_bytes(changed)
        self.assertNotEqual(0,self.run_fixup().returncode)
        self.assertEqual(changed,self.copy.read_bytes())

    def test_unknown_wait_trace_is_refused_without_writing(self):
        original=self.copy.read_bytes()
        old='[LowLevelMonitor] Wait BEGIN\n'.encode('utf-16le')
        new='[LowLevelMonitor] Wait BADD!\n'.encode('utf-16le')
        self.assertEqual(len(old),len(new));self.assertIn(old,original)
        changed=original.replace(old,new);self.copy.write_bytes(changed)
        self.assertNotEqual(0,self.run_fixup().returncode)
        self.assertEqual(changed,self.copy.read_bytes())


class PatchedCoreLibQuietTests(MonitorQuietTests):
    def setUp(self):
        super().setUp()
        self.source=ROOT/'obj/Release/net10.0/linux-x64/cosmos/ref/System.Private.CoreLib.dll'
        self.copy=Path(self.temp.name)/self.source.name;shutil.copyfile(self.source,self.copy)

    def test_real_released_trace_adapts_without_modifying_the_dependency(self):
        # The kernel build must adapt the transplanted CoreLib methods, not
        # merely the source plug DLL (which ILC never executes at this point).
        before=self.source.read_bytes();self.assertIn(b'_zenithQuietMonitorWrite',before)
        result=self.run_fixup();self.assertEqual(0,result.returncode,result.stderr)
        self.assertIn('Quieted pinned monitor interop success traces',result.stdout)
        self.assertEqual(before,self.copy.read_bytes());self.assertEqual(before,self.source.read_bytes())

if __name__=='__main__':unittest.main()
