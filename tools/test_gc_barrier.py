#!/usr/bin/env python3
"""Run production C3 gate/epoch state transitions on real host threads."""
import ctypes as ct
from pathlib import Path
import platform
import subprocess
import tempfile
import threading
import time
import unittest
ROOT=Path(__file__).resolve().parents[1]

@unittest.skipUnless(platform.machine().lower() in ('x86_64','amd64'),'x64 C3 native atomics')
class BarrierTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp=tempfile.TemporaryDirectory(prefix='zn-gc-epoch-');d=Path(cls.temp.name)
        subprocess.run(['clang','-O2','-shared','-fPIC','-DZENITH_GC_HOST_TEST',str(ROOT/'src/Native/Cpu/gc.c'),'-o',str(d/'gc.so')],check=True)
        cls.lib=ct.CDLL(str(d/'gc.so'))
        for name,arity in [('enter',0),('exit',0),('configure',5),('managed_enter',0),('managed_leave',0),('park',2),('begin',0),('end',1),('root',2),('state',1),('host_cpu',1),('host_init',0),('host_notify',1),('host_budget',1),('abort',1)]:
            f=getattr(cls.lib,'zenith_gc_'+name);f.argtypes=[ct.c_uint64]*arity;f.restype=ct.c_uint64
    @classmethod
    def tearDownClass(cls):cls.temp.cleanup()
    def setUp(self):
        self.lib.zenith_gc_host_init();self.stack=ct.create_string_buffer(32768);self.irq=ct.create_string_buffer(8192)
        self.low=ct.addressof(self.stack);self.irq_low=ct.addressof(self.irq)
        self.assertEqual(self.lib.zenith_gc_configure(1,self.low,32768,self.irq_low,8192),1)
        self.errors=[];self.threads=[]
    def tearDown(self):
        self.lib.zenith_gc_host_cpu(0)
        epoch=self.lib.zenith_gc_state(0)
        if epoch:self.lib.zenith_gc_end(epoch)
        for t in self.threads:t.join(5);self.assertFalse(t.is_alive())
        self.assertEqual(self.errors,[])
    def spawn(self,cpu,body):
        def run():
            try:self.lib.zenith_gc_host_cpu(cpu);body()
            except BaseException as e:self.errors.append(repr(e))
        t=threading.Thread(target=run);self.threads.append(t);t.start();return t
    def wait(self,predicate):
        deadline=time.monotonic()+5
        while not predicate():
            if self.errors:raise AssertionError(self.errors)
            if time.monotonic()>deadline:raise AssertionError('host state deadline')
            time.sleep(.0001)
    def mutator(self,cycles=1,frame=None,sp=None):
        ready=threading.Event()
        def run():
            self.assertEqual(self.lib.zenith_gc_managed_enter(),1);ready.set()
            old=0
            for _ in range(cycles):
                self.wait(lambda:self.lib.zenith_gc_state(0)>old)
                old=self.lib.zenith_gc_state(0)
                self.lib.zenith_gc_park(frame or self.irq_low+1024,sp or self.low+4096)
            self.assertEqual(self.lib.zenith_gc_managed_leave(),1)
        self.spawn(1,run);self.assertTrue(ready.wait(5))
    def test_recursive_gate_and_nonowner_release(self):
        self.assertEqual(self.lib.zenith_gc_enter(),1);self.assertEqual(self.lib.zenith_gc_enter(),1)
        self.lib.zenith_gc_host_cpu(1);self.assertEqual(self.lib.zenith_gc_exit(),0)
        self.lib.zenith_gc_host_cpu(0);self.assertEqual(self.lib.zenith_gc_exit(),1)
        self.assertEqual(self.lib.zenith_gc_state(5),1);self.assertEqual(self.lib.zenith_gc_exit(),1)
        self.assertEqual(self.lib.zenith_gc_state(5),0);self.assertEqual(self.lib.zenith_gc_exit(),0)
    def test_cross_cpu_publication_gate_excludes_partial_header(self):
        self.assertEqual(self.lib.zenith_gc_enter(),1);entered=threading.Event()
        def allocate():
            self.assertEqual(self.lib.zenith_gc_enter(),1);entered.set();self.assertEqual(self.lib.zenith_gc_exit(),1)
        self.spawn(1,allocate);time.sleep(.02);self.assertFalse(entered.is_set())
        self.lib.zenith_gc_exit();self.assertTrue(entered.wait(5))
    def test_stopped_epoch_exposes_only_valid_snapshot_until_resume(self):
        self.mutator();epoch=self.lib.zenith_gc_begin();self.assertGreater(epoch,0)
        self.assertEqual([self.lib.zenith_gc_root(1,i) for i in range(4)],[self.irq_low+1024,79,self.low+4096,self.low+32768])
        self.assertEqual(self.lib.zenith_gc_end(epoch+1),0)
        self.assertEqual(self.lib.zenith_gc_end(epoch),1)
        self.assertEqual(self.lib.zenith_gc_root(1,0),0);self.assertEqual(self.lib.zenith_gc_end(epoch),0)
    def test_repeated_epochs_never_accept_old_ack(self):
        self.mutator(cycles=100)
        for _ in range(100):
            epoch=self.lib.zenith_gc_begin();self.assertGreater(epoch,0)
            self.assertEqual(self.lib.zenith_gc_root(1,1),79);self.assertEqual(self.lib.zenith_gc_end(epoch),1)
        self.assertEqual(self.lib.zenith_gc_state(2),100)
    def test_lost_notification_refuses_without_exposing_or_sweeping(self):
        self.mutator();self.lib.zenith_gc_host_notify(0)
        self.assertEqual(self.lib.zenith_gc_begin(),0);self.assertEqual(self.lib.zenith_gc_root(1,0),0)
        self.assertEqual(self.lib.zenith_gc_state(3),1)
        # The refused epoch released the gate; a future complete epoch is legal.
        self.lib.zenith_gc_host_notify((1<<64)-1)
        epoch=self.lib.zenith_gc_begin();self.assertGreater(epoch,0);self.lib.zenith_gc_end(epoch)
    def test_invalid_stack_does_not_acknowledge(self):
        ready=threading.Event();finished=threading.Event()
        def run():
            self.lib.zenith_gc_managed_enter();ready.set()
            self.wait(lambda:self.lib.zenith_gc_state(0)>0)
            self.lib.zenith_gc_park(self.irq_low+1024,self.low-8);finished.set()
            self.wait(lambda:self.lib.zenith_gc_state(0)==0)
            self.lib.zenith_gc_managed_leave()
        self.spawn(1,run);self.assertTrue(ready.wait(5));self.lib.zenith_gc_host_budget(30000000)
        self.assertEqual(self.lib.zenith_gc_begin(),0);self.assertTrue(finished.wait(5))
        self.assertEqual(self.lib.zenith_gc_state(2),0);self.assertEqual(self.lib.zenith_gc_root(1,0),0)
    def test_collector_abort_never_releases_epoch_or_restarts_allocation(self):
        ready=threading.Event();done=threading.Event()
        def mutator():
            self.assertEqual(self.lib.zenith_gc_managed_enter(),1);ready.set()
            self.wait(lambda:self.lib.zenith_gc_state(0)>0)
            self.lib.zenith_gc_park(self.irq_low+1024,self.low+4096)
            # Host-only return models the native fault-IST terminal path.
            self.assertEqual(self.lib.zenith_gc_state(7),1);done.set()
        self.spawn(1,mutator);self.assertTrue(ready.wait(5))
        epoch=self.lib.zenith_gc_begin();self.assertGreater(epoch,0)
        self.lib.zenith_gc_abort(epoch);self.assertTrue(done.wait(5))
        self.assertEqual(self.lib.zenith_gc_end(epoch),0)
        self.assertEqual(self.lib.zenith_gc_enter(),0)
        self.assertEqual(self.lib.zenith_gc_root(1,0),0)
        self.assertEqual(self.lib.zenith_gc_state(0),epoch)

    def test_ap_collection_deferred_and_unknown_cpu_fails_closed(self):
        self.lib.zenith_gc_host_cpu(1);self.assertEqual(self.lib.zenith_gc_begin(),0)
        self.lib.zenith_gc_host_cpu(99);self.assertEqual(self.lib.zenith_gc_enter(),0)
        self.assertEqual(self.lib.zenith_gc_managed_enter(),0)
        self.lib.zenith_gc_host_cpu(0);self.assertEqual(self.lib.zenith_gc_state(4),1)
    def test_managed_entry_waits_for_empty_participant_collection_to_finish(self):
        epoch=self.lib.zenith_gc_begin();self.assertGreater(epoch,0);entered=threading.Event()
        def run():
            self.assertEqual(self.lib.zenith_gc_managed_enter(),1);entered.set()
            self.assertEqual(self.lib.zenith_gc_managed_leave(),1)
        self.spawn(1,run);time.sleep(.02);self.assertFalse(entered.is_set())
        self.assertEqual(self.lib.zenith_gc_state(1),0);self.lib.zenith_gc_end(epoch);self.assertTrue(entered.wait(5))


class ManagedBarrierFixupTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import os
        cls.temp=tempfile.TemporaryDirectory(prefix='zn-gc-il-');d=Path(cls.temp.name)
        cecil=ROOT/'tools/PatcherFixup/bin/Release/net10.0/Mono.Cecil.dll'
        (d/'check.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><Compile Include="'+str(ROOT/'tools/fixtures/gc-barrier.cs')+'"/><Reference Include="Mono.Cecil"><HintPath>'+str(cecil)+'</HintPath></Reference></ItemGroup></Project>')
        subprocess.run(['dotnet','build',str(d/'check.csproj'),'-c','Release','--nologo'],check=True,stdout=subprocess.DEVNULL)
        cls.driver=d/'bin/Release/net10.0/check.dll'
        cls.tool=ROOT/'tools/PatcherFixup/bin/Release/net10.0/PatcherFixup.dll'
        packages=Path(os.environ.get('NUGET_PACKAGES',Path.home()/'.nuget/packages'))
        cls.source=packages/'cosmos.kernel.core/3.0.89/runtimes/linux-x64/lib/net10.0/Cosmos.Kernel.Core.dll'
    @classmethod
    def tearDownClass(cls):cls.temp.cleanup()
    def setUp(self):
        self.tempdir=tempfile.TemporaryDirectory();self.addCleanup(self.tempdir.cleanup)
        self.copy=Path(self.tempdir.name)/self.source.name;self.copy.write_bytes(self.source.read_bytes())
        result=subprocess.run(['dotnet',str(self.tool),str(self.copy)],capture_output=True,text=True)
        self.assertEqual(result.returncode,0,result.stderr)
    def run_mode(self,mode):
        result=subprocess.run(['dotnet',str(self.driver),str(self.copy),str(ROOT/'output-x64'),mode],capture_output=True,text=True)
        self.assertEqual(result.returncode,0,result.stderr);return result.stdout
    def test_refusal_never_enters_mark_or_sweep_and_restores_irq(self):
        result=self.run_mode('refuse');self.assertIn('result=0',result);self.assertIn('body=0 end=0 irq=512',result)
    def test_admitted_collection_completes_epoch_and_restores_irq(self):
        result=self.run_mode('normal');self.assertIn('result=7',result);self.assertIn('body=1 end=1 irq=512',result)
    def test_collector_exception_keeps_world_stopped_and_fail_stops(self):
        result=self.run_mode('throw');self.assertIn('fatal=collector',result);self.assertIn('body=1 end=0 irq=0',result);self.assertIn('aborts=1',result)
    def test_stopped_root_scanner_executes_valid_il_and_skips_absent_cpus(self):
        self.assertIn('body=0 end=0 irq=512 roots=7',self.run_mode('scan'))
    def test_suspended_boot_ui_stack_is_scanned_without_an_allocated_stack(self):
        self.assertIn('ranges=1 start=4128 limit=8192',self.run_mode('boot-valid'))
    def test_boot_stack_fallback_does_not_scan_other_threads_or_current_cpu_stack(self):
        for mode in ('boot-other','boot-ap','boot-running'):
            self.assertIn('ranges=0',self.run_mode(mode))
    def test_invalid_suspended_boot_stack_bounds_fail_before_reading_memory(self):
        for mode in ('boot-low','boot-high','boot-unaligned'):
            result=self.run_mode(mode)
            self.assertIn('fatal=boot-bounds',result);self.assertIn('ranges=0',result)
    def test_unmanaged_heap_uses_same_gate_and_restores_irq(self):
        result=self.run_mode('heap')
        self.assertIn('result=7',result);self.assertIn('irq=512 roots=0 enters=1 exits=1',result)
    def test_unmanaged_heap_exception_releases_owner_before_unwind(self):
        result=self.run_mode('heap-throw')
        self.assertIn('thrown=body',result);self.assertIn('irq=512 roots=0 enters=1 exits=1',result)
    def test_partial_safety_adapter_is_refused_even_with_existing_contract_marker(self):
        self.run_mode('break-contract')
        broken=Path(str(self.copy)+'.incomplete.dll');before=broken.read_bytes()
        result=subprocess.run(['dotnet',str(self.tool),str(broken)],capture_output=True,text=True)
        self.assertNotEqual(result.returncode,0)
        self.assertIn('Incomplete C3 runtime ownership contract',result.stderr)
        self.assertEqual(before,broken.read_bytes())
    def test_gc_adaptation_is_idempotent(self):
        before=self.copy.read_bytes()
        result=subprocess.run(['dotnet',str(self.tool),str(self.copy)],capture_output=True,text=True)
        self.assertEqual(result.returncode,0,result.stderr);self.assertEqual(before,self.copy.read_bytes())
    def test_missing_suspended_boot_stack_scan_is_rejected_on_reapplication(self):
        self.run_mode('break-boot-contract')
        broken=Path(str(self.copy)+'.incomplete.dll');before=broken.read_bytes()
        result=subprocess.run(['dotnet',str(self.tool),str(broken)],capture_output=True,text=True)
        self.assertNotEqual(result.returncode,0)
        self.assertIn('Incomplete suspended boot-stack root adaptation',result.stderr)
        self.assertEqual(before,broken.read_bytes())

if __name__=='__main__':unittest.main()
