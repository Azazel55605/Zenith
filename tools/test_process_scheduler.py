#!/usr/bin/env python3
"""Real native ready queues, affinity, transfer, exact tickets and retained leases."""
import ctypes as c
import importlib.util
from pathlib import Path
import threading
import time
import unittest
ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('queue_base',ROOT/'tools/test_cpu_workers.py')
base=importlib.util.module_from_spec(spec);spec.loader.exec_module(base)
class ProcessQueueTests(base.CpuWorkerNativeTests):
    @classmethod
    def setUpClass(cls):
        super().setUpClass()
        for name,arity in [('process_submit',2),('process_field',2),('process_control',2),('process_mask',0),('process_stats',2),('host_process_step',1)]:
            f=getattr(cls.lib,'zenith_cpu_'+name);f.argtypes=[c.c_uint64]*arity;f.restype=c.c_uint64
        cls.lib.zenith_cpu_host_process_fault.argtypes=[c.c_uint64]
    def submit(self,data,mask=254):return self.lib.zenith_cpu_process_submit(c.addressof(data),mask)
    def field(self,t,f=0):return self.lib.zenith_cpu_process_field(t,f)
    def ctl(self,t,a):return self.lib.zenith_cpu_process_control(t,a)
    def run_cpu(self,cpu):return self.lib.zenith_cpu_host_process_step(cpu)
    def test_queue_capacity_terminal_leases_and_stale_handle(self):
        data=c.c_uint64(4096);ts=[self.submit(data) for _ in range(8)]
        self.assertTrue(all(ts));self.assertEqual(self.submit(data),0)
        for _ in range(8):
            for cpu in (1,2,3):self.run_cpu(cpu)
        self.assertTrue(all(self.field(t)==3 for t in ts));self.assertEqual(self.submit(data),0)
        for cpu in (1,2,3):self.assertEqual(self.lib.zenith_cpu_process_stats(2,cpu),0)
        self.assertEqual(self.ctl(ts[0],2),1);new=self.submit(data);self.assertTrue(new);self.assertNotEqual(new,ts[0]);self.assertEqual(self.field(ts[0]),0)
    def test_ready_context_can_transfer_and_running_work_is_not_migrated(self):
        data=c.c_uint64(123);t=self.submit(data)
        self.assertEqual(self.run_cpu(3),1);self.assertEqual(self.field(t,2),3);self.assertEqual(self.field(t,1),123)
        self.assertEqual(self.lib.zenith_cpu_process_stats(1,0),1)
        self.assertEqual(self.run_cpu(1),0);self.assertEqual(self.run_cpu(2),0)
        self.ctl(t,2)
        self.assertTrue(all(self.lib.zenith_cpu_process_stats(3,cpu)==0 for cpu in (1,2,3)))
    def test_affinity_does_not_silently_expand(self):
        data=c.c_uint64(17);t=self.submit(data,2)
        self.assertEqual(self.run_cpu(2),0);self.assertEqual(self.run_cpu(3),0);self.assertEqual(self.field(t),1)
        self.assertEqual(self.run_cpu(1),1);self.assertEqual(self.field(t,2),1)
        for mask in (0,1,256,1<<40):self.assertEqual(self.submit(data,mask),0)
    def test_requeued_quantum_goes_behind_already_ready_generations(self):
        data=c.c_uint64(17);first=self.submit(data,2);second=self.submit(data,2)
        self.assertEqual(self.run_cpu(1),1);self.assertEqual(self.field(first),3);self.assertEqual(self.field(second),1)
        self.ctl(first,2);resumed=self.submit(data,2)
        self.assertEqual(self.run_cpu(1),1);self.assertEqual(self.field(second),3);self.assertEqual(self.field(resumed),1)
        self.assertEqual(self.run_cpu(1),1);self.assertEqual(self.field(resumed),3)
    def test_queued_cancel_and_stop_clear_each_runnable_ticket_once(self):
        data=c.c_uint64(4);ts=[self.submit(data,2) for _ in range(8)]
        self.assertEqual(self.lib.zenith_cpu_process_stats(2,1),8)
        self.assertEqual(self.ctl(ts[1],0),1);self.assertEqual(self.ctl(ts[1],0),1)
        self.assertEqual(self.field(ts[1]),4);self.assertEqual(self.lib.zenith_cpu_process_stats(2,1),7)
        self.lib.zenith_cpu_stop()
        self.assertTrue(all(self.field(t)==4 for t in ts));self.assertEqual(self.lib.zenith_cpu_process_stats(2,1),0)
        self.assertEqual(self.submit(data),0)
    def test_legacy_work_cannot_enter_a_process_lease_or_be_starved_by_one(self):
        data=c.c_uint64(4);legacy=self.enqueue(cpu=1,held=1)
        self.assertEqual(self.submit(data,2),0)
        task=self.submit(data,4);self.assertTrue(task);self.assertEqual(self.enqueue(cpu=2),0)
        self.ctl(task,0);self.ctl(task,2)
        self.assertTrue(self.enqueue(cpu=2));self.assertEqual(self.state(legacy),1)
    def test_parallel_consumers_never_execute_one_ready_generation_twice(self):
        data=c.c_uint64(99)
        for _ in range(50):
            ts=[self.submit(data) for _ in range(8)];self.assertTrue(all(ts))
            def consume(cpu):
                for _ in range(16):self.run_cpu(cpu)
            threads=[threading.Thread(target=consume,args=(cpu,)) for cpu in (1,2,3)]
            for thread in threads:thread.start()
            for thread in threads:thread.join(2);self.assertFalse(thread.is_alive())
            self.assertTrue(all(self.field(t)==3 for t in ts))
            for t in ts:self.assertEqual(self.ctl(t,2),1)
        self.assertEqual(self.lib.zenith_cpu_process_stats(0,0),400)
    def test_running_generation_keeps_its_cpu_until_cancel_acknowledges(self):
        data=c.c_uint64(99);t=self.submit(data,14)
        self.lib.zenith_cpu_host_pause(1)
        worker=threading.Thread(target=self.run_cpu,args=(1,));worker.start()
        try:
            deadline=time.monotonic()+2
            while not self.lib.zenith_cpu_host_seen() and time.monotonic()<deadline:time.sleep(.001)
            self.assertEqual(self.field(t),2);self.assertEqual(self.field(t,2),1)
            self.assertEqual(self.run_cpu(2),0);self.assertEqual(self.ctl(t,2),0)
            self.ctl(t,0);worker.join(2);self.assertFalse(worker.is_alive())
            self.assertEqual(self.field(t),4);self.assertEqual(self.field(t,2),1)
            self.assertEqual(self.lib.zenith_cpu_process_stats(2,1),0)
            self.assertEqual(self.ctl(t,2),1)
        finally:self.lib.zenith_cpu_host_pause(0);worker.join(2)
    def test_faulted_target_quarantines_descriptors_but_sibling_continues(self):
        data=c.c_uint64(7);failed=[self.submit(data,2) for _ in range(3)]
        self.lib.zenith_cpu_host_process_fault(1)
        self.assertTrue(all(self.field(t)==5 for t in failed))
        self.assertEqual(self.lib.zenith_cpu_process_stats(2,1),0)
        self.assertEqual(self.lib.zenith_cpu_process_stats(3,1),3)
        self.assertTrue(all(self.ctl(t,2)==0 for t in failed));self.assertEqual(self.submit(data,2),0)
        healthy=self.submit(data,4);self.assertTrue(healthy);self.assertEqual(self.run_cpu(2),1)
        self.assertEqual(self.field(healthy),3);self.assertEqual(self.ctl(healthy,2),1)
        self.lib.zenith_cpu_stop();self.assertEqual(self.lib.zenith_cpu_process_stats(2,1),0)
if __name__=='__main__':unittest.main()
