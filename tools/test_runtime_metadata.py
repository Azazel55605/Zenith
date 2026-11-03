#!/usr/bin/env python3
"""Regression for the actual metadata key-before-resolver exception race."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
ROOT=Path(__file__).resolve().parents[1]
class MetadataTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp=tempfile.TemporaryDirectory();d=Path(cls.temp.name)
        (d/'check.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><Compile Include="'+str(ROOT/'tools/fixtures/runtime-metadata.cs')+'"/><Reference Include="Mono.Cecil"><HintPath>'+str(ROOT/'tools/PatcherFixup/bin/Release/net10.0/Mono.Cecil.dll')+'</HintPath></Reference></ItemGroup></Project>')
        subprocess.run(['dotnet','build',str(d/'check.csproj'),'-c','Release','--nologo'],check=True,stdout=subprocess.DEVNULL)
        cls.driver=d/'bin/Release/net10.0/check.dll';cls.tool=ROOT/'tools/PatcherFixup/bin/Release/net10.0/PatcherFixup.dll'
        packages=Path(os.environ.get('NUGET_PACKAGES',Path.home()/'.nuget/packages'))
        cls.source=packages/'cosmos.kernel.core/3.0.89/runtimes/linux-x64/lib/net10.0/Cosmos.Kernel.Core.dll'
    @classmethod
    def tearDownClass(cls):cls.temp.cleanup()
    def run_mode(self,mode):
        with tempfile.TemporaryDirectory() as d:
            p=Path(d)/self.source.name;p.write_bytes(self.source.read_bytes())
            r=subprocess.run(['dotnet',str(self.tool),str(p)],capture_output=True,text=True)
            self.assertEqual(r.returncode,0,r.stderr)
            r=subprocess.run(['dotnet',str(self.driver),str(p),str(ROOT/'output-x64'),mode],capture_output=True,text=True)
            self.assertEqual(r.returncode,0,r.stderr);return r.stdout
    def test_resolver_is_complete_before_key_and_cache_hit(self):self.assertIn('constructors=1 bodies=2 enters=2 exits=2 irq=512',self.run_mode('normal'))
    def test_throwing_constructor_does_not_poison_cache_or_leak_gate(self):self.assertIn('constructors=2 bodies=2 enters=2 exits=2 irq=512',self.run_mode('throw'))
    def test_second_reader_waits_until_complete_publication(self):self.assertIn('constructors=1 bodies=2 enters=2 exits=2 irq=512',self.run_mode('concurrent'))
    def test_bsp_keeps_uart_and_early_graphics_output(self):self.assertIn('writes=1',self.run_mode('serial-bsp'))
    def test_ap_cannot_write_uart_or_early_graphics(self):self.assertIn('writes=0',self.run_mode('serial-ap'))
    def test_unknown_cpu_cannot_write_uart_or_early_graphics(self):self.assertIn('writes=0',self.run_mode('serial-unknown'))
if __name__=='__main__':unittest.main()
