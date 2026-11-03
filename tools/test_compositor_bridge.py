#!/usr/bin/env python3
"""Execute the actual C# compositor bridge against native host-thread queues."""
import platform
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]

@unittest.skipUnless(platform.machine().lower() in ('x86_64','amd64'),'x64 queue model')
class CompositorBridgeTests(unittest.TestCase):
    def test_complete_copy_and_admission_against_production_native_queues(self):
        with tempfile.TemporaryDirectory(prefix='zn-compositor-bridge-') as temporary:
            work=Path(temporary);native=work/'queues.so'
            subprocess.run(['clang','-shared','-fPIC','-O2','-std=c11','-ffreestanding','-fno-builtin','-DZENITH_CPU_HOST_TEST',str(ROOT/'src/Native/Cpu/workers.c'),'-o',str(native)],check=True)
            sources=[ROOT/'tools/fixtures/compositor-bridge.cs',ROOT/'src/Core/Multiprocessing/BoxBlur.cs',ROOT/'src/Core/Multiprocessing/CpuWorkers.Blur.cs']
            (work/'test.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AllowUnsafeBlocks>true</AllowUnsafeBlocks><Nullable>enable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup>'+''.join('<Compile Include="'+str(p)+'" />' for p in sources)+'</ItemGroup></Project>')
            result=subprocess.run(['dotnet','run','--project',str(work/'test.csproj'),'-c','Release','--',str(native)],capture_output=True,text=True,timeout=120)
            self.assertEqual(result.returncode,0,result.stdout+result.stderr)
            self.assertIn('PASS C4 production C# bridge',result.stdout)
if __name__=='__main__':unittest.main()
