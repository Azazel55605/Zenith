#!/usr/bin/env python3
"""Run the production bounded C1 queues/math on host threads; IRQ/HLT is guest-only."""
import ctypes as ct
import os
from unittest.mock import patch
import platform
from pathlib import Path
import subprocess
import tempfile
import threading
import time
import unittest
ROOT=Path(__file__).resolve().parents[1]
MASK=(1<<64)-1

def checksum(n):
    v=0xcbf29ce484222325
    for i in range(n,0,-1):v=((v^i)*0x100000001b3)&MASK
    return v

def pixels(seed,n,rounds):
    values=[0xff000000|((seed*33+p*17)&0xffffff) for p in range(n)]
    for r in range(rounds):
        tint=0x203040+((r+seed)&15)*0x010101
        values=[0xff000000 | sum(((((v>>s)&255)*191+((tint>>s)&255)*64+127)//255)<<s for s in (0,8,16)) for v in values]
    return values

@unittest.skipUnless(platform.machine().lower() in ('x86_64','amd64'),'executes x64 C1 atomics/math')
class CpuWorkerNativeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp=tempfile.TemporaryDirectory(prefix='zn-cpu-queue-')
        lib=Path(cls.temp.name)/'queue.so'
        subprocess.run(['clang','-shared','-fPIC','-O2','-std=c11','-ffreestanding','-fno-builtin','-DZENITH_CPU_HOST_TEST',str(ROOT/'src/Native/Cpu/workers.c'),'-o',str(lib)],check=True)
        cls.lib=ct.CDLL(str(lib))
        for name,arity in [('host_init',1),('host_step',1),('host_quiesce',1),('host_lock',2),('host_state',3),('host_pause',1),('host_seen',0),('enqueue',6),('job',2),('control',2),('pixel',2),('field',2),('bsp',4),('stop',0)]:
            f=getattr(cls.lib,'zenith_cpu_'+name);f.argtypes=[ct.c_uint64]*arity;f.restype=None if name in ('host_init','stop','host_lock','host_state','host_pause') else ct.c_uint64
    @classmethod
    def tearDownClass(cls):cls.temp.cleanup()
    def setUp(self):self.lib.zenith_cpu_host_init(4)
    def enqueue(self,cpu=1,kind=1,n=1000,seed=0,rounds=1,held=0):return self.lib.zenith_cpu_enqueue(cpu,kind,n,seed,rounds,held)
    def state(self,t):return self.lib.zenith_cpu_job(t,0)
    def step(self,cpu=1):return self.lib.zenith_cpu_host_step(cpu)
    def control(self,t,a):return self.lib.zenith_cpu_control(t,a)
    def test_bounds_fail_closed(self):
        for args in [(0,1,1,0,1,0),(4,1,1,0,1,0),(1,0,1,0,1,0),(1,5,1,0,1,0),(1,1,0,0,1,0),(1,1,100000001,0,1,0),(1,2,4097,0,1,0),(1,2,1,0,129,0),(1,2,1,0,0,0)]:
            self.assertEqual(self.lib.zenith_cpu_enqueue(*args),0,args)
        self.assertEqual(self.state(0),0);self.assertEqual(self.control(1<<40,0),0)
    def test_unready_worker_refuses_admission(self):
        self.lib.zenith_cpu_host_state(1,0,0)
        self.assertEqual(self.enqueue(),0)
        self.assertEqual(self.lib.zenith_cpu_field(1,13),0)

    def test_reaped_token_stays_invalid_after_later_ap_fault(self):
        retained=self.enqueue();reaped=self.enqueue()
        self.step();self.step();self.assertEqual(self.control(reaped,2),1)
        self.lib.zenith_cpu_host_state(1,4,1)
        self.assertEqual(self.state(reaped),0)
        self.assertEqual(self.state(retained),5)
        self.assertEqual(self.control(retained,2),0)
        self.assertEqual(self.enqueue(),0)

    def test_capacity_includes_retained_completions(self):
        ts=[self.enqueue() for _ in range(8)]
        self.assertTrue(all(ts));self.assertEqual(self.enqueue(),0)
        for _ in ts:self.assertEqual(self.step(),1)
        self.assertEqual(self.enqueue(),0)
        self.assertEqual(self.lib.zenith_cpu_job(ts[0],1),checksum(1000))
        self.assertEqual(self.control(ts[0],2),1)
        self.assertNotEqual(self.enqueue(),0)
    def test_generation_and_terminal_ownership(self):
        old=self.enqueue();self.assertEqual(self.control(old,2),0);self.step()
        self.assertEqual(self.control(old,2),1)
        new=self.enqueue();self.assertNotEqual(new,old)
        self.assertEqual(self.control(old,0),0);self.assertEqual(self.state(old),0)
        self.step();self.assertEqual(self.state(new),3)
    def test_queued_cancel_and_held_start(self):
        first=self.enqueue(held=1);second=self.enqueue()
        self.assertEqual(self.step(),0);self.assertEqual(self.state(first),1)
        self.assertEqual(self.control(first,1),1);self.assertEqual(self.control(second,0),1)
        self.assertEqual(self.step(),1);self.assertEqual(self.state(first),3)
        self.assertEqual(self.step(),0);self.assertEqual(self.state(second),4)
        self.assertEqual(self.control(second,2),1)
    def test_running_cancellation_no_retry(self):
        self.lib.zenith_cpu_host_pause(1)
        t=self.enqueue(n=4096)
        worker=threading.Thread(target=self.step);worker.start()
        try:
            limit=time.monotonic()+2
            while not self.lib.zenith_cpu_host_seen() and time.monotonic()<limit:time.sleep(.0001)
            self.assertEqual(self.lib.zenith_cpu_host_seen(),1)
            self.assertEqual(self.control(t,0),1)
            worker.join(2);self.assertFalse(worker.is_alive());self.assertEqual(self.state(t),4)
            expected=0xcbf29ce484222325
            for i in range(4096,3840,-1):expected=((expected^i)*0x100000001b3)&MASK
            self.assertEqual(self.lib.zenith_cpu_job(t,1),expected)
        finally:self.control(t,0);self.lib.zenith_cpu_host_pause(0);worker.join(5)
    def test_contended_stop_never_acknowledges_before_terminal_jobs(self):
        token=self.enqueue()
        self.lib.zenith_cpu_host_lock(1,1);self.lib.zenith_cpu_stop()
        self.assertEqual(self.lib.zenith_cpu_host_quiesce(1),0)
        self.assertEqual(self.lib.zenith_cpu_field(1,12),0)
        self.lib.zenith_cpu_host_lock(1,0)
        self.assertEqual(self.lib.zenith_cpu_host_quiesce(1),1)
        self.assertEqual(self.state(token),4)
        self.assertEqual(self.lib.zenith_cpu_field(1,12),1)

    def test_global_stop_cancels_executing_and_queued_jobs(self):
        self.lib.zenith_cpu_host_pause(1)
        running=self.enqueue(n=4096)
        queued=[self.enqueue(cpu=cpu) for cpu in (1,1,2,3)]
        worker=threading.Thread(target=self.step);worker.start()
        try:
            limit=time.monotonic()+2
            while not self.lib.zenith_cpu_host_seen() and time.monotonic()<limit:time.sleep(.0001)
            self.assertEqual(self.lib.zenith_cpu_host_seen(),1)
            self.lib.zenith_cpu_stop();worker.join(2)
            self.assertFalse(worker.is_alive());self.assertEqual(self.state(running),4)
            for cpu in (1,2,3):self.step(cpu)
            self.assertTrue(all(self.state(t)==4 for t in queued))
            for cpu in (1,2,3):self.assertEqual(self.enqueue(cpu=cpu),0)
        finally:
            self.lib.zenith_cpu_stop();self.lib.zenith_cpu_host_pause(0);worker.join(5)

    def test_actual_pixel_buffer_independent_scalar_reference(self):
        for seed,n,rounds in [(0,1,1),(17,259,8),(255,4096,17)]:
            t=self.enqueue(kind=2,n=n,seed=seed,rounds=rounds)
            self.assertEqual(self.lib.zenith_cpu_pixel(t,0),0)
            self.step();self.assertEqual(self.state(t),3)
            values=pixels(seed,n,rounds)
            self.assertEqual([self.lib.zenith_cpu_pixel(t,i) for i in range(n)],values)
            self.assertEqual(self.lib.zenith_cpu_pixel(t,n),0)
            self.control(t,2)
    def test_parallel_consumers_and_ring_wrap(self):
        stop=threading.Event();threads=[]
        for cpu in (1,2,3):
            def consume(cpu=cpu):
                while not stop.is_set():
                    if not self.step(cpu):time.sleep(.0001)
            t=threading.Thread(target=consume);t.start();threads.append(t)
        try:
            for cycle in range(200):
                jobs=[self.enqueue(cpu=cpu,n=100+cycle) for cpu in (1,2,3)]
                self.assertTrue(all(jobs));deadline=time.monotonic()+2
                while any(self.state(t)<3 for t in jobs) and time.monotonic()<deadline:time.sleep(.0001)
                for t in jobs:
                    self.assertEqual(self.state(t),3);self.assertEqual(self.lib.zenith_cpu_job(t,1),checksum(100+cycle));self.assertEqual(self.control(t,2),1)
        finally:
            stop.set()
            for t in threads:t.join(5)
    def test_stop_closes_admission_and_cancels_queued(self):
        t=self.enqueue();self.lib.zenith_cpu_stop()
        self.assertEqual(self.enqueue(),0);self.step();self.assertEqual(self.state(t),4)
    def test_bsp_and_ap_equal_work(self):
        t=self.enqueue(kind=2,n=4096,seed=4,rounds=128);self.step()
        self.assertEqual(self.lib.zenith_cpu_job(t,1),self.lib.zenith_cpu_bsp(2,4096,4,128))
    def test_interrupted_compile_preserves_complete_object_and_retries(self):
        import importlib.util
        spec=importlib.util.spec_from_file_location('cpu_atomic_build',ROOT/'tools/build-cpu.py');build=importlib.util.module_from_spec(spec);spec.loader.exec_module(build)
        out=Path(self.temp.name)/'interrupted';out.mkdir();target=out/'cpu-workers.o'
        target.write_bytes(b'previous-complete-object');os.utime(target,(1,1))
        def interrupted(command,**kwargs):
            Path(command[-1]).write_bytes(b'partial-new-object')
            raise subprocess.CalledProcessError(1,command)
        with patch.object(build.subprocess,'run',side_effect=interrupted),self.assertRaises(subprocess.CalledProcessError):build.build(out)
        self.assertEqual(target.read_bytes(),b'previous-complete-object')
        self.assertEqual(list(out.glob('.cpu-*.tmp')),[])
        build.build(out)
        self.assertEqual(target.read_bytes()[:4],b'\x7fELF')

    def test_freestanding_imports_and_no_fp(self):
        import importlib.util
        spec=importlib.util.spec_from_file_location('cpu_build',ROOT/'tools/build-cpu.py');build=importlib.util.module_from_spec(spec);spec.loader.exec_module(build)
        out=Path(self.temp.name)/'kernel';build.build(out)
        asm=subprocess.check_output(['objdump','-d',str(out/'cpu-workers.o')],text=True)
        self.assertNotRegex(asm,r'%[xyz]mm|\b(?:fld|fst|fadd|fmul)\b')
        # A plain MFENCE orders memory, but Intel requires dispatch serialization
        # as well before x2APIC WRMSR. Check actual emitted instructions.
        self.assertRegex(asm,r'(?s)\bmfence\b.{0,500}\blfence\b.{0,500}\bwrmsr\b')

if __name__=='__main__':unittest.main()
