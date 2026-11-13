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
    def test_display_and_monitor_requests_yield_to_bsp_services_on_every_private_bridge(self):
        # Display, monitor and owned framebuffer services, one inclusive range.
        for cpu in range(1,8):
            bridge=b.variant(self.source,cpu)
            self.assertIn('cmp $640, %rax\n    jb 11f\n    cmp $649, %rax\n    jbe p'+str(cpu)+'_z_service\n11:',bridge)

    def test_resumed_frames_rebind_selectors_after_kernel_gdt_growth(self):
        # Execute the real copy/selector-rebind instructions without privileged
        # IRET/FXRSTOR. Every other saved field must survive unchanged, including
        # the user flags and stack; neither a stale selector nor a source-frame
        # edit is permitted. Cover BSP and all generated private AP bridges.
        source=[]
        for cpu in range(8):
            prefix='' if cpu==0 else 'p'+str(cpu)+'_'
            bridge=self.source if cpu==0 else b.variant(self.source,cpu)
            block=bridge.split(prefix+'z_resume_user:\n',1)[1].split('    mov 184(%rbx), %rax',1)[0]
            source.append('.text\n.globl host_resume_'+str(cpu)+'\nhost_resume_'+str(cpu)+':\n'
                'push %rbx\nmov %rdi,%rbx\nmov %rsp,%r8\n'+block+
                'mov %rsp,%rax\nmov %r8,%rsp\npop %rbx\nret\n.data\n'+prefix+'z_user_cs: .word '+str(0x4b+cpu*16)+'\n'+
                prefix+'z_user_ss: .word '+str(0x53+cpu*16)+'\n.bss\n.balign 16\n'+prefix+'z_interrupt_stack: .zero 69632\n')
        d=Path(self.temp.name);asm=d/'resume.s';asm.write_text('\n'.join(source)+'\n.section .note.GNU-stack,"",@progbits\n')
        subprocess.run(['clang','-shared',str(asm),'-o',str(d/'resume.so')],check=True)
        lib=c.CDLL(str(d/'resume.so'))
        for cpu in range(8):
            frame=(c.c_uint64*22)(*[0x100+i for i in range(22)])
            frame[17]=0x40482b;frame[18]=0x2b;frame[19]=0x246;frame[20]=0x7ffefde8;frame[21]=0x33
            before=list(frame);context=(c.c_uint64*24)();context[21]=0x75;context[22]=c.addressof(frame)
            call=getattr(lib,'host_resume_'+str(cpu));call.argtypes=[c.c_void_p];call.restype=c.c_void_p
            result=(c.c_uint64*22).from_address(call(context))
            expected=before.copy();expected[14]=0x75;expected[18]=0x4b+cpu*16;expected[21]=0x53+cpu*16
            self.assertEqual(list(result),expected)
            before[14]=0x75;self.assertEqual(list(frame),before)

    def test_source_drift_is_rejected_and_bsp_source_remains_identical(self):
        with self.assertRaisesRegex(RuntimeError,'source changed'):b.variant(self.source+'\n',1)
        self.assertEqual(hashlib.sha256((ROOT/'src/Native/x64/Process.s').read_bytes()).hexdigest(),b.SOURCE_SHA)
if __name__=='__main__':unittest.main()
