#!/usr/bin/env python3
"""Exercise the pinned boot assembly adapter independently of kernel compilation."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
ROOT=Path(__file__).resolve().parents[1]
class BootstrapTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp=tempfile.TemporaryDirectory();d=Path(cls.temp.name)
        cecil=ROOT/'tools/PatcherFixup/bin/Release/net10.0/Mono.Cecil.dll'
        (d/'check.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><Compile Include="'+str(ROOT/'tools/fixtures/cpu-bootstrap.cs')+'"/><Reference Include="Mono.Cecil"><HintPath>'+str(cecil)+'</HintPath></Reference></ItemGroup></Project>')
        subprocess.run(['dotnet','build',str(d/'check.csproj'),'-c','Release','--nologo'],check=True,stdout=subprocess.DEVNULL)
        cls.driver=d/'bin/Release/net10.0/check.dll';cls.tool=ROOT/'tools/PatcherFixup/bin/Release/net10.0/PatcherFixup.dll'
        packages=Path(os.environ.get('NUGET_PACKAGES',Path.home()/'.nuget/packages'))
        cls.source=packages/'cosmos.kernel/3.0.89/runtimes/linux-x64/lib/net10.0/Cosmos.Kernel.dll'
    @classmethod
    def tearDownClass(cls):cls.temp.cleanup()
    def test_bsp_bootstrap_is_idempotent_and_keeps_owner_setup(self):
        with tempfile.TemporaryDirectory() as d:
            p=Path(d)/self.source.name;p.write_bytes(self.source.read_bytes())
            subprocess.run(['dotnet',str(self.tool),str(p)],check=True,stdout=subprocess.DEVNULL)
            first=p.read_bytes()
            subprocess.run(['dotnet',str(self.tool),str(p)],check=True,stdout=subprocess.DEVNULL)
            self.assertEqual(first,p.read_bytes())
            subprocess.run(['dotnet',str(self.driver),str(p)],check=True,stdout=subprocess.DEVNULL)
    def test_changed_boot_signature_refuses_without_writing(self):
        with tempfile.TemporaryDirectory() as d:
            p=Path(d)/self.source.name
            subprocess.run(['dotnet',str(self.driver),str(self.source),str(p)],check=True)
            before=p.read_bytes();r=subprocess.run(['dotnet',str(self.tool),str(p)],capture_output=True,text=True)
            self.assertNotEqual(r.returncode,0);self.assertIn('bootstrap contract changed',r.stderr)
            self.assertEqual(before,p.read_bytes())
if __name__=='__main__':unittest.main()
