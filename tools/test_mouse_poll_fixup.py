"""Execute the adapted real polling wrapper/queue budget with only hardware I/O stubbed."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
ROOT=Path(__file__).resolve().parents[1]
REFERENCES=Path(os.environ.get('ZENITH_FIXUP_REFERENCE_DIR',ROOT/'output-x64'))

class MousePollingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.build=tempfile.TemporaryDirectory(prefix='zenith-mouse-poll-');folder=Path(cls.build.name)
        (folder/'check.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><Compile Include="'+str(ROOT/'tools/fixtures/mouse-poll.cs')+'"/><Reference Include="Mono.Cecil"><HintPath>'+str(ROOT/'tools/PatcherFixup/bin/Release/net10.0/Mono.Cecil.dll')+'</HintPath></Reference></ItemGroup></Project>')
        r=subprocess.run(['dotnet','build',str(folder/'check.csproj'),'-c','Release','--nologo','-p:NuGetAudit=false'],capture_output=True,text=True)
        if r.returncode:raise RuntimeError(r.stdout+r.stderr)
        cls.driver=folder/'bin/Release/net10.0/check.dll'
    @classmethod
    def tearDownClass(cls):cls.build.cleanup()
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.folder=Path(self.temp.name)
        packages=Path(os.environ.get('NUGET_PACKAGES',Path.home()/'.nuget/packages'))
        for name in ('Core','System','HAL'):
            src=packages/('cosmos.kernel.'+name.lower())/'3.0.89/runtimes/linux-x64/lib/net10.0'/('Cosmos.Kernel.'+name+'.dll')
            shutil.copyfile(src,self.folder/src.name)
        self.system=self.folder/'Cosmos.Kernel.System.dll';self.hal=self.folder/'Cosmos.Kernel.HAL.dll'
        self.tool=ROOT/'tools/PatcherFixup/bin/Release/net10.0/PatcherFixup.dll'
    def adapt(self,path):
        r=subprocess.run(['dotnet',str(self.tool),str(path)],cwd=REFERENCES,capture_output=True,text=True)
        self.assertEqual(0,r.returncode,r.stdout+r.stderr)
    def execute(self,path,mode):
        r=subprocess.run(['dotnet',str(self.driver),str(path),str(REFERENCES),mode],capture_output=True,text=True)
        self.assertEqual(0,r.returncode,r.stdout+r.stderr);return r.stdout.strip()
    def test_real_wrapper_masks_and_restores_full_irq_flags(self):
        self.adapt(self.system)
        self.assertEqual('insideEnabled=False;flags=246;save=1;restore=1;public=True;threw=False',self.execute(self.system,'scope'))
    def test_disabled_irq_state_is_preserved(self):
        self.adapt(self.system)
        self.assertEqual('insideEnabled=False;flags=46;save=1;restore=1;public=True;threw=False',self.execute(self.system,'disabled'))
    def test_driver_exception_restores_irq_flags(self):
        self.adapt(self.system)
        self.assertEqual('insideEnabled=False;flags=246;save=1;restore=1;public=True;threw=True',self.execute(self.system,'throw'))
    def test_real_bounded_helper_drains_backlog_across_polls_without_duplication(self):
        self.adapt(self.hal)
        self.assertEqual('first=64;total=100;available=0;zeroBudgetCalls=0;zeroResult=False;zeroId=0;zeroLength=0',self.execute(self.hal,'budget'))
    def test_adapters_are_byte_idempotent(self):
        for path in (self.system,self.hal):
            self.adapt(path);before=path.read_bytes();self.adapt(path);self.assertEqual(before,path.read_bytes())
    def test_polling_contract_drift_is_rejected_without_writing(self):
        self.execute(self.system,'drift');before=self.system.read_bytes()
        r=subprocess.run(['dotnet',str(self.tool),str(self.system)],cwd=REFERENCES,capture_output=True,text=True)
        self.assertNotEqual(0,r.returncode);self.assertIn('mouse polling contract changed',r.stderr);self.assertEqual(before,self.system.read_bytes())

    def test_application_bridge_calls_the_irq_scoped_manager(self):
        main=self.folder/'Zenith.dll';self.execute(main,'create-main')
        self.adapt(self.system);self.adapt(main)
        self.assertEqual('insideEnabled=False;flags=246;save=1;restore=1;public=True;threw=False',self.execute(main,'bind'))
    def test_application_bridge_without_adaptation_fails_closed(self):
        main=self.folder/'Zenith.dll';self.execute(main,'create-main')
        r=subprocess.run(['dotnet',str(self.driver),str(main),str(REFERENCES),'bind'],capture_output=True,text=True)
        self.assertNotEqual(0,r.returncode);self.assertIn('Zenith mouse polling adapter missing',r.stderr)

if __name__=='__main__':unittest.main()
