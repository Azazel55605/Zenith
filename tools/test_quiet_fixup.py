#!/usr/bin/env python3
"""Real pinned trace adaptation, idempotence and drift refusal."""
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

class TcpRejectionQuietTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        temp = tempfile.TemporaryDirectory(prefix='zenith-tcp-trace-inspector-')
        cls.addClassCleanup(temp.cleanup)
        folder = Path(temp.name)
        binaries = ROOT / 'tools/PatcherFixup/bin/Release/net10.0'
        cls.tool = binaries / 'PatcherFixup.dll'
        project = folder / 'check.csproj'
        project.write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
            '</PropertyGroup><ItemGroup>'
            f'<Compile Include="{ROOT / "tools/fixtures/tcp-rejection-trace.cs"}"/>'
            f'<Reference Include="Mono.Cecil"><HintPath>{binaries / "Mono.Cecil.dll"}</HintPath>'
            '</Reference></ItemGroup></Project>')
        subprocess.run(['dotnet', 'build', str(project), '-c', 'Release', '--nologo'],
                       capture_output=True, check=True)
        cls.driver = folder / 'bin/Release/net10.0/check.dll'

    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        packages = Path(os.environ.get('NUGET_PACKAGES', Path.home() / '.nuget/packages'))
        self.source = (packages / 'cosmos.kernel.system/3.0.89/runtimes/linux-x64/lib/net10.0/'
                       'Cosmos.Kernel.System.dll')
        self.copy = Path(temp.name) / self.source.name
        self.references = Path(os.environ.get('ZENITH_FIXUP_REFERENCES', ROOT / 'output-x64'))
        shutil.copyfile(self.source, self.copy)

    def adapt(self):
        return subprocess.run(['dotnet', str(self.tool), str(self.copy)], cwd=self.references,
                              capture_output=True, text=True)

    def inspect(self, *args):
        run = subprocess.run(['dotnet', str(self.driver), str(self.copy), *map(str, args)],
                             cwd=self.references, capture_output=True, text=True)
        self.assertEqual(0, run.returncode, run.stderr)

    def test_rejected_packets_keep_reset_and_ack_branches_across_repeated_adaptation(self):
        original = self.source.read_bytes()
        self.inspect('original')
        result = self.adapt()
        self.assertEqual(0, result.returncode, result.stderr)
        self.inspect('adapted')
        adapted = self.copy.read_bytes()
        result = self.adapt()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(adapted, self.copy.read_bytes())
        self.assertEqual(original, self.source.read_bytes())

    def test_changed_rejection_literal_is_refused_without_writing(self):
        original = self.copy.read_bytes()
        text = '[TCP] Sequence number or segment data invalid, packet passed.\n'
        old = text.encode('utf-16le')
        changed = original.replace(old, text.replace('passed', 'drifts').encode('utf-16le'))
        self.assertNotEqual(original, changed)
        self.copy.write_bytes(changed)
        self.assertNotEqual(0, self.adapt().returncode)
        self.assertEqual(changed, self.copy.read_bytes())

    def test_changed_rejection_sink_is_refused_without_writing(self):
        altered = self.copy.with_name('drift.dll')
        self.inspect('change-sink', altered)
        altered.replace(self.copy)
        before = self.copy.read_bytes()
        self.assertNotEqual(0, self.adapt().returncode)
        self.assertEqual(before, self.copy.read_bytes())


    def test_changed_payload_accessor_is_refused_without_writing(self):
        altered = self.copy.with_name('drift.dll')
        self.inspect('change-payload', altered)
        altered.replace(self.copy)
        before = self.copy.read_bytes()
        result = self.adapt()
        self.assertNotEqual(0, result.returncode)
        self.assertIn('TCP contiguous delivery changed', result.stderr)
        self.assertEqual(before, self.copy.read_bytes())


if __name__=='__main__':unittest.main()
