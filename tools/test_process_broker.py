#!/usr/bin/env python3
"""Execute the production BSP broker; privileged AP delivery stays a guest check."""
from pathlib import Path
import platform
import subprocess
import tempfile
import unittest
ROOT=Path(__file__).resolve().parents[1]

@unittest.skipUnless(platform.machine().lower() in ('x86_64','amd64'),'production broker requires x64')
class BrokerTests(unittest.TestCase):
    def test_no_borrowed_context_escapes_during_callback_or_collection_failure(self):
        with tempfile.TemporaryDirectory(prefix='zn-process-broker-') as temporary:
            d=Path(temporary);native=d/'broker.so'
            (d/'native.c').write_text('''
#include <stdlib.h>
typedef unsigned long long u64;
static u64 state,result;static int admitted,reaped,attempts;
void host_reset(void){state=result=0;admitted=reaped=attempts=0;}
int host_count(int field){return field==0?admitted:field==1?reaped:field==2?attempts:(int)state;}
u64 zenith_cpu_process_submit(u64 context,u64 mask){if(!(mask&2))return 0;admitted++;state=2;return 64;}
u64 zenith_cpu_process_field(u64 token,u64 field){return field==0?state:field==1?result:field==2?1:0;}
u64 zenith_cpu_process_control(u64 token,u64 action){
 if(action==0){state=4;return 1;}
 if(action==2&&state>=3){attempts++;if(attempts<3)return 0;reaped++;state=0;return 1;}
 return 0;
}
u64 zenith_cpu_process_wait(u64 token){if(state==2){state=3;result=4096;}return 1;}
u64 zenith_cpu_process_mask(void){return 2;}
void zenith_gc_fail_stop(void){_Exit(90);}
''')
            subprocess.run(['clang','-shared','-fPIC','-O2',str(d/'native.c'),'-o',str(native)],check=True)
            sources=[ROOT/'tools/fixtures/process-broker.cs',ROOT/'src/Core/Multiprocessing/CpuWorkers.Process.cs']
            (d/'test.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup>'+''.join('<Compile Include="'+str(p)+'" />' for p in sources)+'</ItemGroup></Project>')
            r=subprocess.run(['dotnet','run','--project',str(d/'test.csproj'),'-c','Release','--',str(native)],capture_output=True,text=True,timeout=120)
            self.assertEqual(r.returncode,0,r.stdout+r.stderr)
            self.assertIn('PASS protected broker:',r.stdout)
if __name__=='__main__':unittest.main()
