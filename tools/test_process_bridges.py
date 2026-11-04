#!/usr/bin/env python3
"""Compile the actual seven AP bridges and execute their capability refusal path."""
import ctypes as c
import hashlib
import importlib.util
from pathlib import Path
import platform
import subprocess
import tempfile
import unittest
ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('bridges',ROOT/'tools/build-process-bridges.py')
b=importlib.util.module_from_spec(spec);spec.loader.exec_module(b)

@unittest.skipUnless(platform.machine().lower() in ('x86_64','amd64'),'executes x64 refusal only')
class BridgeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp=tempfile.TemporaryDirectory(prefix='zn-process-bridges-');d=Path(cls.temp.name)
        cls.source=(ROOT/'src/Native/x64/Process.s').read_text();s=d/'ProcessAp.s';b.build(s)
        stub=d/'stub.s'
        stub.write_text('''.text
.globl zenith_cpu_logical_id
zenith_cpu_logical_id: mov identity(%rip),%eax; ret
.globl host_identity
host_identity: mov %edi,identity(%rip); ret
.globl zenith_cpu_process_allowed
zenith_cpu_process_allowed: xor %eax,%eax; ret
.globl zenith_cpu_process_prepare
zenith_cpu_process_prepare: ud2
.globl zenith_cpu_process_period
zenith_cpu_process_period: ud2
.globl zenith_cpu_process_bad_irq
zenith_cpu_process_bad_irq: ud2
.data
identity: .long 0
.section .note.GNU-stack,"",@progbits
''')
        subprocess.run(['clang','-shared','-Wl,-Bsymbolic',str(s),str(stub),'-o',str(d/'bridges.so')],check=True)
        cls.symbols=subprocess.check_output(['nm',str(d/'bridges.so')],text=True)
        cls.lib=c.CDLL(str(d/'bridges.so'));cls.lib.host_identity.argtypes=[c.c_uint32]
        cls.lib.zenith_process_ap_dispatch.argtypes=[c.c_void_p];cls.lib.zenith_process_ap_dispatch.restype=c.c_uint64
    @classmethod
    def tearDownClass(cls):cls.temp.cleanup()
    def test_unauthorized_cpu_or_unleased_context_is_refused_before_cli(self):
        for cpu in (0,1,2,3,4,5,6,7,8,0xffffffff):
            context=(c.c_uint64*40)(*([0x123456789]*40));self.lib.host_identity(cpu)
            self.lib.zenith_process_ap_dispatch(context)
            self.assertEqual(context[8],95);self.assertEqual(context[12],5)
            self.assertTrue(all(x==0x123456789 for i,x in enumerate(context) if i not in (8,12)))
    def test_each_raw_save_area_and_interrupt_table_has_one_cpu_owner(self):
        for cpu in range(1,8):
            for name in ('z_context','z_saved_rsp','z_old_cr3','z_old_idtr','z_tss','z_user_fp','z_old_fp'):
                self.assertTrue('p'+str(cpu)+'_'+name in self.symbols,name)
        self.assertNotIn(' zenith_process_run\n',self.symbols)
    def test_display_requests_yield_to_bsp_services_on_every_private_bridge(self):
        for cpu in range(1,8):
            bridge=b.variant(self.source,cpu)
            self.assertIn('cmp $640, %rax\n    je p'+str(cpu)+'_z_service',bridge)
            self.assertIn('cmp $641, %rax\n    je p'+str(cpu)+'_z_service',bridge)

    def test_source_drift_is_rejected_and_bsp_source_remains_identical(self):
        with self.assertRaisesRegex(RuntimeError,'source changed'):b.variant(self.source+'\n',1)
        self.assertEqual(hashlib.sha256((ROOT/'src/Native/x64/Process.s').read_bytes()).hexdigest(),b.SOURCE_SHA)
if __name__=='__main__':unittest.main()
