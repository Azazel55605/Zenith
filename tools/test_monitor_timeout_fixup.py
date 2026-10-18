"""Pinned monitor timeout must reach the caller, including transplanted CoreLib."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]

class TimeoutTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp=tempfile.TemporaryDirectory(prefix='zenith-monitor-driver-')
        folder=Path(cls.temp.name)
        (folder/'check.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><Compile Include="'+str(ROOT/'tools/fixtures/monitor-timeout.cs')+'"/><Reference Include="Mono.Cecil"><HintPath>'+str(ROOT/'tools/PatcherFixup/bin/Release/net10.0/Mono.Cecil.dll')+'</HintPath></Reference></ItemGroup></Project>')
        subprocess.run(['dotnet','build',str(folder/'check.csproj'),'-c','Release','--nologo'],check=True,capture_output=True)
        cls.driver=folder/'bin/Release/net10.0/check.dll'

    @classmethod
    def tearDownClass(cls): cls.temp.cleanup()

    def setUp(self):
        self.tempdir=tempfile.TemporaryDirectory();self.addCleanup(self.tempdir.cleanup)
        self.folder=Path(self.tempdir.name)
        for f in (ROOT/'output-x64').glob('*.dll'): shutil.copyfile(f,self.folder/f.name)
        packages=Path(os.environ.get('NUGET_PACKAGES',Path.home()/'.nuget/packages'))
        self.source=packages/'cosmos.kernel.plugs/3.0.89/runtimes/linux-x64/lib/net10.0/Cosmos.Kernel.Plugs.dll'
        self.copy=self.folder/self.source.name;shutil.copyfile(self.source,self.copy)

    def adapt(self):
        return subprocess.run(['dotnet',str(ROOT/'tools/PatcherFixup/bin/Release/net10.0/PatcherFixup.dll'),str(self.copy)],cwd=self.folder,capture_output=True,text=True)

    def execute(self, type_name="Cosmos.Kernel.Plugs.System.InteropSysPlug"):
        r=subprocess.run(['dotnet',str(self.driver),str(self.copy),type_name,str(self.folder)],capture_output=True,text=True)
        self.assertEqual(0,r.returncode,r.stderr)
        return r.stdout

    def test_actual_timeout_and_signal_results_and_infinite_wait(self):
        original=self.source.read_bytes()
        self.assertIn('timeout=1;signal=False;result=True;calls=1',self.execute())
        r=self.adapt();self.assertEqual(0,r.returncode,r.stderr)
        output=self.execute()
        for row in ('timeout=1;signal=False;result=False;calls=1','timeout=1;signal=True;result=True;calls=1','timeout=0;signal=False;result=False;calls=1','timeout=-1;signal=False;result=True;calls=1'):
            self.assertIn(row,output)
        self.assertEqual(original,self.source.read_bytes())

    def test_transplanted_corelib_preserves_both_results(self):
        self.copy=self.folder/'System.Private.CoreLib.dll'
        shutil.copyfile(ROOT/'obj/Release/net10.0/linux-x64/cosmos/ref/System.Private.CoreLib.dll',self.copy)
        r=self.adapt();self.assertEqual(0,r.returncode,r.stderr)
        output=self.execute('Interop/Sys')
        self.assertIn('timeout=1;signal=False;result=False;calls=1',output)
        self.assertIn('timeout=1;signal=True;result=True;calls=1',output)
        self.assertIn('timeout=-1;signal=False;result=True;calls=1',output)

    def test_repeated_adaptation_is_identical(self):
        self.assertEqual(0,self.adapt().returncode);before=self.copy.read_bytes()
        self.assertEqual(0,self.adapt().returncode);self.assertEqual(before,self.copy.read_bytes())

    def test_return_contract_drift_is_refused_without_write(self):
        before=self.copy.read_bytes();changed=before.replace(b'LowLevelMonitor_TimedWait',b'LowLevelMonitor_XimedWait')
        self.assertNotEqual(before,changed);self.copy.write_bytes(changed)
        self.assertNotEqual(0,self.adapt().returncode);self.assertEqual(changed,self.copy.read_bytes())

if __name__=='__main__':unittest.main()
