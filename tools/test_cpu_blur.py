#!/usr/bin/env python3
"""Production C4 snapshot/filter/ownership checks using real native queues."""
import ctypes as ct
import importlib.util
from pathlib import Path
import threading
import time
import unittest

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('blur_queues',ROOT/'tools/test_cpu_workers.py')
queues=importlib.util.module_from_spec(spec);spec.loader.exec_module(queues)

def oracle(source,w,h):
    current=source
    for horizontal in (True,False):
        result=[]
        for y in range(h):
            for x in range(w):
                samples=[current[(y if horizontal else max(0,min(h-1,y+k)))*w+(max(0,min(w-1,x+k)) if horizontal else x)] for k in range(-4,5)]
                result.append(0xff000000|sum(((sum((v>>s)&255 for v in samples)+4)//9)<<s for s in (0,8,16)))
        current=result
    return current

class BlurTests(queues.CpuWorkerNativeTests):
    # Reuse the real DSO lifecycle; inherited queue tests also exercise enlarged
    # work storage and the new completion type without bypassing admission.
    @classmethod
    def setUpClass(cls):
        super().setUpClass()
        cls.lib.zenith_cpu_blur_submit.argtypes=[ct.POINTER(ct.c_uint32)]+[ct.c_uint64]*6
        cls.lib.zenith_cpu_blur_submit.restype=ct.c_uint64
        cls.lib.zenith_cpu_blur_copy.argtypes=[ct.c_uint64,ct.POINTER(ct.c_uint32),ct.c_uint64]
        cls.lib.zenith_cpu_blur_copy.restype=ct.c_uint64
    def source(self,w,h):return (ct.c_uint32*(w*h))(*[((i*1664525+1013904223)^(i//w*731))&0xffffffff for i in range(w*h)])
    def submit(self,src,w,h,x=0,y=0,tw=None,th=None):return self.lib.zenith_cpu_blur_submit(src,w,h,x,y,tw or min(w,256),th or min(h,64))
    def copy(self,token,n):
        data=(ct.c_uint32*n)(*[0x12345678]*n);ok=self.lib.zenith_cpu_blur_copy(token,data,n)
        return ok,list(data)
    def test_blur_halos_match_oracle_including_seams_and_tiny_dimensions(self):
        for w,h in ((1,1),(1,73),(269,1),(271,69)):
            src=self.source(w,h);expected=oracle(list(src),w,h)
            for y in range(0,h,64):
                for x in range(0,w,256):
                    tw,th=min(256,w-x),min(64,h-y);t=self.submit(src,w,h,x,y,tw,th);self.assertTrue(t)
                    self.assertEqual(self.copy(t,tw*th)[0],0)
                    self.step((t&63)//8)
                    ok,actual=self.copy(t,tw*th);self.assertEqual(ok,1)
                    self.assertEqual(actual,[expected[(y+dy)*w+x+dx] for dy in range(th) for dx in range(tw)])
                    self.assertEqual(self.control(t,2),1)
    def test_snapshot_owns_input_and_completed_capacity_until_reaped(self):
        src=self.source(17,9);expected=oracle(list(src),17,9)
        tokens=[self.submit(src,17,9) for _ in range(3)]
        self.assertEqual({(t&63)//8 for t in tokens},{1,2,3});self.assertEqual(self.submit(src,17,9),0)
        for i in range(len(src)):src[i]=0
        for t in tokens:
            self.step((t&63)//8);self.assertEqual(self.copy(t,153),(1,expected))
        self.assertEqual(self.submit(src,17,9),0)
        self.control(tokens[1],2);new=self.submit(src,17,9)
        self.assertTrue(new);self.assertNotEqual(new,tokens[1]);self.assertEqual(self.copy(tokens[1],153)[0],0)
    def test_dimensions_generic_type_and_wrong_output_length_refuse(self):
        src=self.source(300,80)
        for args in ((0,80,0,0,1,1),(4097,1,0,0,1,1),(300,80,0,0,257,1),(300,80,0,0,1,65),(300,80,299,79,2,1),(300,80,2**64-1,0,1,1)):
            self.assertEqual(self.lib.zenith_cpu_blur_submit(src,*args),0,args)
        self.assertEqual(self.enqueue(kind=6),0)
        t=self.submit(src,300,80);self.step((t&63)//8)
        self.assertEqual(self.copy(t,16383),(0,[0x12345678]*16383))
    def test_busy_failed_and_stopped_owners_are_not_selected(self):
        src=self.source(17,9);held=self.enqueue(cpu=1,held=1)
        self.lib.zenith_cpu_host_state(2,4,1)
        t=self.submit(src,17,9);self.assertEqual((t&63)//8,3)
        self.assertEqual(self.submit(src,17,9),0)
        self.lib.zenith_cpu_stop();self.step(3)
        self.assertEqual(self.copy(t,153)[0],0);self.assertEqual(self.submit(src,17,9),0)
        self.assertEqual(self.state(t),4);self.assertEqual(self.state(held),1)
    def test_running_blur_cancellation_never_publishes_partial_pixels(self):
        src=self.source(256,64);self.lib.zenith_cpu_host_pause(1);t=self.submit(src,256,64)
        worker=threading.Thread(target=lambda:self.step((t&63)//8));worker.start()
        try:
            limit=time.monotonic()+2
            while not self.lib.zenith_cpu_host_seen() and time.monotonic()<limit:time.sleep(.0001)
            self.assertEqual(self.lib.zenith_cpu_host_seen(),1);self.control(t,0);worker.join(2)
            self.assertFalse(worker.is_alive());self.assertEqual(self.state(t),4)
            self.assertEqual(self.copy(t,16384),(0,[0x12345678]*16384))
        finally:self.lib.zenith_cpu_host_pause(0);worker.join(5)

if __name__=='__main__':unittest.main()
