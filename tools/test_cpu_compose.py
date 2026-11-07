"""Exact premultiplied composition through the production immutable AP tile queue."""
import ctypes as ct
import importlib.util
from pathlib import Path
import unittest
ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('compose_queues',ROOT/'tools/test_cpu_workers.py')
queues=importlib.util.module_from_spec(spec);spec.loader.exec_module(queues)
class ComposeTests(queues.CpuWorkerNativeTests):
    @classmethod
    def setUpClass(cls):
        super().setUpClass()
        cls.lib.zenith_cpu_compose_submit.argtypes=[ct.POINTER(ct.c_uint32),ct.c_uint64,ct.POINTER(ct.c_uint32),ct.c_uint64,ct.c_uint64,ct.c_uint64]
        cls.lib.zenith_cpu_compose_submit.restype=ct.c_uint64
        cls.lib.zenith_cpu_blur_copy.argtypes=[ct.c_uint64,ct.POINTER(ct.c_uint32),ct.c_uint64];cls.lib.zenith_cpu_blur_copy.restype=ct.c_uint64
    def test_owned_snapshot_exact_alpha_and_row_strides(self):
        w,h,fs,bs=127,33,140,137
        fg=(ct.c_uint32*(fs*h))();bg=(ct.c_uint32*(bs*h))();expected=[]
        for y in range(h):
            for x in range(w):
                a=(x*17+y*31)%256;r=(x*7)% (a+1);g=(y*13)%(a+1);b=(x+y)%(a+1)
                f=(a<<24)|(r<<16)|(g<<8)|b;back=0xff2367ab
                fg[y*fs+x]=f;bg[y*bs+x]=back
                expected.append(f if a==255 else back if not a else 0xff000000|sum((((f>>s)&255)+(((back>>s)&255)*(255-a)+127)//255)<<s for s in (0,8,16)))
        t=self.lib.zenith_cpu_compose_submit(fg,fs,bg,bs,w,h);self.assertTrue(t)
        for i in range(len(fg)):fg[i]=0
        for i in range(len(bg)):bg[i]=0
        self.step((t&63)//8)
        out=(ct.c_uint32*(w*h))();self.assertEqual(self.lib.zenith_cpu_blur_copy(t,out,w*h),1)
        self.assertEqual(list(out),expected)
        self.assertEqual(self.lib.zenith_cpu_control(t,2),1)
        self.assertEqual(self.lib.zenith_cpu_blur_copy(t,out,w*h),0)
    def test_invalid_strides_bounds_stop_and_busy_refuse(self):
        p=(ct.c_uint32*(256*64))()
        for fs,bs,w,h in [(1,256,2,1),(256,1,2,1),(256,256,257,1),(256,256,1,65),(16385,256,1,1),(256,256,0,1)]:
            self.assertEqual(self.lib.zenith_cpu_compose_submit(p,fs,p,bs,w,h),0)
        tokens=[self.lib.zenith_cpu_compose_submit(p,256,p,256,256,64) for _ in range(3)]
        self.assertTrue(all(tokens));self.assertEqual(self.lib.zenith_cpu_compose_submit(p,256,p,256,1,1),0)
        self.lib.zenith_cpu_stop();self.assertEqual(self.lib.zenith_cpu_compose_submit(p,256,p,256,1,1),0)
if __name__=='__main__':unittest.main()
