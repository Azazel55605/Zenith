"""Execute the adapted pinned weak/dependent handle cleanup, not a model of it."""
import os
from pathlib import Path
import subprocess,tempfile,unittest
ROOT=Path(__file__).resolve().parents[1]
class WeakHandleTests(unittest.TestCase):
 def test_dead_target_does_not_free_an_owned_slot_and_frozen_keys_survive(self):
  with tempfile.TemporaryDirectory() as temporary:
   d=Path(temporary)
   (d/'check.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AllowUnsafeBlocks>true</AllowUnsafeBlocks><ImplicitUsings>enable</ImplicitUsings><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><Compile Include="'+str(ROOT/'tools/fixtures/weak-handles.cs')+'"/><Reference Include="Mono.Cecil"><HintPath>'+str(ROOT/'tools/PatcherFixup/bin/Release/net10.0/Mono.Cecil.dll')+'</HintPath></Reference></ItemGroup></Project>')
   subprocess.run(['dotnet','build',str(d/'check.csproj'),'-c','Release','--nologo'],check=True,stdout=subprocess.DEVNULL)
   packages=Path(os.environ.get('NUGET_PACKAGES',Path.home()/'.nuget/packages'))
   p=d/'Cosmos.Kernel.Core.dll';p.write_bytes((packages/'cosmos.kernel.core/3.0.89/runtimes/linux-x64/lib/net10.0/Cosmos.Kernel.Core.dll').read_bytes())
   tool=ROOT/'tools/PatcherFixup/bin/Release/net10.0/PatcherFixup.dll'
   r=subprocess.run(['dotnet',str(tool),str(p)],capture_output=True,text=True);self.assertEqual(r.returncode,0,r.stdout+r.stderr)
   r=subprocess.run(['dotnet',str(d/'bin/Release/net10.0/check.dll'),str(p),str(ROOT/'output-x64')],capture_output=True,text=True);self.assertEqual(r.returncode,0,r.stdout+r.stderr);self.assertIn('PASS actual weak/dependent sweep',r.stdout)
if __name__=='__main__':unittest.main()
