#!/usr/bin/env python3
"""C2 checks against pinned IRQ source and actual native CPU/bridge code."""
import ctypes as ct
import importlib.util
import os
from pathlib import Path
import platform
import subprocess
import tempfile
import threading
import unittest
ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('cpu_interrupts',ROOT/'tools/adapt-cpu-interrupts.py')
adapter=importlib.util.module_from_spec(spec);spec.loader.exec_module(adapter)
PACKAGES=Path(os.environ.get('NUGET_PACKAGES',Path.home()/'.nuget/packages'))
SOURCE=PACKAGES/'cosmos.kernel.native.x64/3.0.89/build/CPU/Interrupts.s'

@unittest.skipUnless(platform.machine().lower() in ('x86_64','amd64'),'x64 native execution')
class ExecutionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp=tempfile.TemporaryDirectory(prefix='zn-execution-');d=Path(cls.temp.name)
        cls.original=SOURCE.read_bytes();adapted=d/'Interrupts.s'
        adapted.write_text(adapter.adapt(cls.original.decode()))
        subprocess.run(['clang','--target=x86_64-none-elf','-c',str(adapted),'-o',str(d/'irq.o')],check=True)
        cls.symbols=subprocess.check_output(['nm',str(d/'irq.o')],text=True)
        # Link the production slot accessor and real process bridge into a host
        # DSO. This test only enters the rejected ownership path, before CLI.
        # RIP-relative globals require a locally bound DSO.
        stub=d/'stub.s'
        stub.write_text('''.text
.globl zenith_cpu_mp_response
zenith_cpu_mp_response:
    lea response(%rip),%rax
    ret
.globl zenith_cpu_timer_probe
zenith_cpu_timer_probe: xor %eax,%eax; ret
.globl _native_set_context_switch_sp
_native_set_context_switch_sp: ret
.globl _native_set_context_switch_new_thread
_native_set_context_switch_new_thread: ret
.globl _native_get_context_switch_sp
_native_get_context_switch_sp: xor %eax,%eax; ret
.data
response: .quad 0
    .long 0, 0xffffffff
    .quad 0,0
.section .note.GNU-stack,"",@progbits
''')
        subprocess.run(['clang','-shared','-Wl,-Bsymbolic',str(ROOT/'src/Native/x64/ExecutionState.s'),str(ROOT/'src/Native/x64/Process.s'),str(stub),'-o',str(d/'native.so')],check=True)
        cls.lib=ct.CDLL(str(d/'native.so'))
        cls.lib.zenith_execution_slot.restype=ct.c_void_p
        cls.lib.zenith_process_run.argtypes=[ct.c_void_p]
        cls.lib.zenith_execution_process_allowed.restype=ct.c_uint64
    @classmethod
    def tearDownClass(cls):cls.temp.cleanup()

    def test_pinned_source_unchanged_and_unknown_source_refused(self):
        self.assertEqual(self.original,SOURCE.read_bytes())
        with self.assertRaisesRegex(RuntimeError,'source changed'):adapter.adapt(self.original.decode()+'\n')

    def test_adapted_object_has_no_global_switch_or_error_scratch(self):
        for name in ('_context_switch_target_rsp','_context_switch_is_new_thread','_temp_is_new_thread','_irq_last_error_code'):
            self.assertNotIn(name,self.symbols)
        self.assertIn(' U zenith_execution_slot',self.symbols)
        for symbol in ('irq0_stub','irq255_stub','_native_get_context_switch_sp','_native_x64_get_last_error_code'):
            self.assertIn(symbol,self.symbols)

    def test_slot_is_stable_and_cache_line_aligned(self):
        if hasattr(os,'sched_getaffinity'):
            affinity=os.sched_getaffinity(0)
            try:
                os.sched_setaffinity(0,{min(affinity)})
                a=self.lib.zenith_execution_slot()
                self.assertEqual(a%64,0)
                for _ in range(100):self.assertEqual(a,self.lib.zenith_execution_slot())
            finally:os.sched_setaffinity(0,affinity)

    def test_real_two_cpu_slot_writes_do_not_alias(self):
        if not hasattr(os,'sched_getaffinity'):self.skipTest('requires Linux CPU affinity')
        affinity=os.sched_getaffinity(0);found={}
        try:
            for cpu in sorted(affinity):
                os.sched_setaffinity(0,{cpu});found.setdefault(self.lib.zenith_execution_slot(),cpu)
                if len(found)==2:break
        finally:os.sched_setaffinity(0,affinity)
        if len(found)<2:self.skipTest('host does not expose two distinct legacy APIC IDs')
        barrier=threading.Barrier(2);errors=[]
        def run(address,cpu,value):
            try:
                os.sched_setaffinity(0,{cpu});slot=(ct.c_uint64*8).from_address(address)
                for _ in range(1000):
                    slot[0]=value;slot[1]=value+1;slot[2]=value+2
                    barrier.wait(timeout=10)
                    if list(slot)[:3]!=[value,value+1,value+2]:raise AssertionError('CPU-local state overwritten')
                    barrier.wait(timeout=10)
            except BaseException as error:errors.append(error);barrier.abort()
        threads=[threading.Thread(target=run,args=(a,c,1000+i*1000)) for i,(a,c) in enumerate(found.items())]
        for t in threads:t.start()
        for t in threads:t.join(30)
        self.assertFalse(any(t.is_alive() for t in threads));self.assertEqual(errors,[])

    def test_first_nonowner_process_entry_refused_before_privileged_or_shared_writes(self):
        self.assertEqual(self.lib.zenith_execution_process_allowed(),0)
        context=(ct.c_uint64*32)(*[0xabc]*32)
        self.lib.zenith_process_run(ct.addressof(context))
        expected=[0xabc]*32;expected[8]=95;expected[12]=5
        self.assertEqual(list(context),expected)
        # A repeated refused entry cannot establish ownership or overwrite state.
        self.lib.zenith_process_run(ct.addressof(context));self.assertEqual(list(context),expected)


class ManagedStateFixupTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp=tempfile.TemporaryDirectory(prefix='zn-cpu-il-');d=Path(cls.temp.name)
        cecil=ROOT/'tools/PatcherFixup/bin/Release/net10.0/Mono.Cecil.dll'
        (d/'check.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><Compile Include="'+str(ROOT/'tools/fixtures/cpu-state.cs')+'"/><Reference Include="Mono.Cecil"><HintPath>'+str(cecil)+'</HintPath></Reference></ItemGroup></Project>')
        subprocess.run(['dotnet','build',str(d/'check.csproj'),'-c','Release','--nologo'],check=True,stdout=subprocess.DEVNULL)
        cls.driver=d/'bin/Release/net10.0/check.dll'
        cls.tool=ROOT/'tools/PatcherFixup/bin/Release/net10.0/PatcherFixup.dll'
        cls.core=PACKAGES/'cosmos.kernel.core/3.0.89/runtimes/linux-x64/lib/net10.0/Cosmos.Kernel.Core.dll'
        cls.x64=PACKAGES/'cosmos.kernel.core.x64/3.0.89/lib/net10.0/Cosmos.Kernel.Core.X64.dll'
    @classmethod
    def tearDownClass(cls):cls.temp.cleanup()
    def setUp(self):
        self.folder=tempfile.TemporaryDirectory();self.addCleanup(self.folder.cleanup)
        self.copy=Path(self.folder.name)/self.core.name;self.copy.write_bytes(self.core.read_bytes())
    def fix(self,p=None):return subprocess.run(['dotnet',str(self.tool),str(p or self.copy)],capture_output=True,text=True)
    def run_driver(self,mode,p=None):
        r=subprocess.run(['dotnet',str(self.driver),str(p or self.copy),mode,str(ROOT/'output-x64')],capture_output=True,text=True)
        self.assertEqual(r.returncode,0,r.stderr);return r.stdout
    def test_identity_uses_hardware_leaf_and_never_aliases_unknown_to_zero(self):
        self.assertEqual(self.fix().returncode,0)
        for cpu in (0,1,3,4294967295):
            raw=self.run_driver('identity:'+str(cpu))
            self.assertIn('native=zenith_cpu_logical_id',raw);self.assertIn('current='+str(cpu),raw)
    def test_complete_fp_layout_and_valid_initial_environment(self):
        before=self.core.read_bytes();self.assertEqual(self.fix().returncode,0)
        raw=self.run_driver('layout')
        self.assertIn('buffer=512',raw);self.assertIn('size=704 r15=512',raw);self.assertIn('cw=37f mxcsr=1f80',raw)
        self.assertEqual(before,self.core.read_bytes())
    def test_identity_and_fp_adaptation_are_idempotent(self):
        self.assertEqual(self.fix().returncode,0);raw=self.copy.read_bytes()
        self.assertEqual(self.fix().returncode,0);self.assertEqual(raw,self.copy.read_bytes())
    def test_identity_contract_drift_refused_without_write(self):
        self.run_driver('drift');before=self.copy.read_bytes();result=self.fix()
        self.assertNotEqual(result.returncode,0);self.assertIn('current-CPU identity contract changed',result.stderr)
        self.assertEqual(before,self.copy.read_bytes())
    def test_timer_and_device_irq_use_matching_logical_identity_and_fp_offset(self):
        p=Path(self.folder.name)/self.x64.name;p.write_bytes(self.x64.read_bytes());before=self.x64.read_bytes()
        result=self.fix(p);self.assertEqual(result.returncode,0,result.stderr)
        raw=self.run_driver('x64',p)
        self.assertIn('X64InterruptController: logical=1 area512=1',raw)
        self.assertIn('LocalApic: logical=1 area512=1',raw)
        self.assertEqual(before,self.x64.read_bytes())
        adapted=p.read_bytes();self.assertEqual(self.fix(p).returncode,0);self.assertEqual(adapted,p.read_bytes())

if __name__=='__main__':unittest.main()
