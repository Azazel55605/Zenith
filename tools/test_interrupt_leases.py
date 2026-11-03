#!/usr/bin/env python3
"""Pinned central IRQ registry adapter and the production native ownership policy."""
import os
import platform
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]
@unittest.skipUnless(platform.machine().lower() in ('x86_64','amd64'),'x64 IRQ model')
class InterruptLeaseTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp=tempfile.TemporaryDirectory(prefix='zn-irq-lease-');work=Path(cls.temp.name)
        (work/'test.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><Compile Include="'+str(ROOT/'tools/fixtures/interrupt-lease.cs')+'"/><Reference Include="Mono.Cecil"><HintPath>'+str(ROOT/'tools/PatcherFixup/bin/Release/net10.0/Mono.Cecil.dll')+'</HintPath></Reference></ItemGroup></Project>')
        subprocess.run(['dotnet','build',str(work/'test.csproj'),'-c','Release'],check=True,stdout=subprocess.DEVNULL)
        cls.driver=work/'bin/Release/net10.0/test.dll';cls.native=work/'queue.so'
        subprocess.run(['clang','-shared','-fPIC','-O2','-std=c11','-ffreestanding','-fno-builtin','-DZENITH_CPU_HOST_TEST',str(ROOT/'src/Native/Cpu/workers.c'),'-o',str(cls.native)],check=True)
        cls.source=Path(os.environ.get('NUGET_PACKAGES',Path.home()/'.nuget/packages'))/'cosmos.kernel.core/3.0.89/runtimes/linux-x64/lib/net10.0/Cosmos.Kernel.Core.dll'
        cls.patcher=ROOT/'tools/PatcherFixup/bin/Release/net10.0/PatcherFixup.dll'
    @classmethod
    def tearDownClass(cls):cls.temp.cleanup()
    def test_pinned_registration_ownership_and_reserved_vectors_before_body(self):
        original=self.source.read_bytes()
        with tempfile.TemporaryDirectory() as folder:
            copy=Path(folder)/self.source.name;copy.write_bytes(original)
            for _ in range(2):
                result=subprocess.run(['dotnet',str(self.patcher),str(copy)],capture_output=True,text=True)
                self.assertEqual(result.returncode,0,result.stderr)
            result=subprocess.run(['dotnet',str(self.driver),'check',str(copy),str(self.native)],capture_output=True,text=True)
            self.assertEqual(result.returncode,0,result.stdout+result.stderr)
            self.assertIn('accepted=17 denied=118',result.stdout)
        self.assertEqual(self.source.read_bytes(),original)
    def test_changed_allocator_registration_or_free_body_refuses_adapter(self):
        for method in ('SetHandler','AllocateVector','FreeVector'):
            with self.subTest(method=method),tempfile.TemporaryDirectory() as folder:
                copy=Path(folder)/self.source.name;copy.write_bytes(self.source.read_bytes())
                subprocess.run(['dotnet',str(self.driver),'drift',str(copy),method],check=True)
                result=subprocess.run(['dotnet',str(self.patcher),str(copy)],capture_output=True,text=True)
                self.assertNotEqual(result.returncode,0)
                self.assertIn('centralized IRQ lease contract changed: '+method,result.stderr)
if __name__=='__main__':unittest.main()
