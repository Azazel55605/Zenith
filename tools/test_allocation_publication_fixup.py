#!/usr/bin/env python3
"""Execute pinned allocation entry points with a pending-IRQ publication observer."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]

class AllocationPublicationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp=tempfile.TemporaryDirectory(prefix='zenith-allocation-')
        folder=Path(cls.temp.name)
        (folder/'check.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
            '<ImplicitUsings>enable</ImplicitUsings><AllowUnsafeBlocks>true</AllowUnsafeBlocks>'
            '</PropertyGroup><ItemGroup><Compile Include="'+str(ROOT/'tools/fixtures/allocation-publication.cs')+'" />'
            '<Reference Include="Mono.Cecil"><HintPath>'+str(ROOT/'tools/PatcherFixup/bin/Release/net10.0/Mono.Cecil.dll')+'</HintPath>'
            '</Reference></ItemGroup></Project>')
        subprocess.run(['dotnet','build',str(folder/'check.csproj'),'-c','Release','--nologo'],check=True,capture_output=True)
        cls.driver=folder/'bin/Release/net10.0/check.dll'
    @classmethod
    def tearDownClass(cls):cls.temp.cleanup()
    def setUp(self):
        self.tempdir=tempfile.TemporaryDirectory();self.addCleanup(self.tempdir.cleanup)
        packages=Path(os.environ.get('NUGET_PACKAGES',Path.home()/'.nuget/packages'))
        self.source=packages/'cosmos.kernel.core/3.0.89/runtimes/linux-x64/lib/net10.0/Cosmos.Kernel.Core.dll'
        self.copy=Path(self.tempdir.name)/self.source.name;shutil.copyfile(self.source,self.copy)
    def adapt(self):
        return subprocess.run(['dotnet',str(ROOT/'tools/PatcherFixup/bin/Release/net10.0/PatcherFixup.dll'),str(self.copy)],capture_output=True,text=True)
    def run_entry(self,mode):
        result=subprocess.run(['dotnet',str(self.driver),str(self.copy),str(ROOT/'output-x64'),mode],capture_output=True,text=True)
        self.assertEqual(0,result.returncode,result.stderr);return result.stdout.strip()
    def test_all_six_original_entry_points_expose_uninitialized_objects(self):
        for mode in ('fast','checked','ptr','array-out','object','object-out'):
            with self.subTest(mode=mode):self.assertIn('uninitializedVisible=True',self.run_entry(mode))
    def test_all_six_adapted_entry_points_publish_before_restoring_irqs(self):
        original=self.source.read_bytes();result=self.adapt();self.assertEqual(0,result.returncode,result.stderr)
        for mode in ('fast','checked','ptr','array-out','object','object-out'):
            with self.subTest(mode=mode):
                self.assertEqual('uninitializedVisible=False;publishedBeforeRestore=True;irqEnabled=True;threw=False',self.run_entry(mode))
        self.assertEqual(original,self.source.read_bytes())
    def test_existing_disabled_irq_state_is_preserved(self):
        self.assertEqual(0,self.adapt().returncode)
        self.assertEqual('uninitializedVisible=False;publishedBeforeRestore=True;irqEnabled=False;threw=False',self.run_entry('disabled'))
    def test_allocation_exception_restores_irq_state(self):
        self.assertEqual(0,self.adapt().returncode)
        self.assertIn('irqEnabled=True;threw=True',self.run_entry('throw'))
    def test_header_layout_drift_is_rejected_before_modifying_input(self):
        self.run_entry('drift');before=self.copy.read_bytes()
        result=self.adapt();self.assertNotEqual(0,result.returncode)
        self.assertIn('allocation publication contract changed',result.stderr)
        self.assertEqual(before,self.copy.read_bytes())
    def test_negative_array_length_restores_irq_state_without_publication(self):
        self.assertEqual(0,self.adapt().returncode)
        self.assertEqual('uninitializedVisible=False;publishedBeforeRestore=False;irqEnabled=True;threw=False',self.run_entry('negative'))
    def test_arm64_allocation_metadata_also_adapts(self):
        arm=self.source.parents[3]/'linux-arm64/lib/net10.0'/self.source.name
        before=arm.read_bytes();shutil.copyfile(arm,self.copy)
        result=self.adapt();self.assertEqual(0,result.returncode,result.stderr)
        self.assertIn('Adapted allocation publication',result.stdout)
        self.assertEqual(before,arm.read_bytes())
    def test_second_adaptation_is_a_noop(self):
        self.assertEqual(0,self.adapt().returncode);before=self.copy.read_bytes()
        self.assertEqual(0,self.adapt().returncode);self.assertEqual(before,self.copy.read_bytes())

if __name__=='__main__':unittest.main()
