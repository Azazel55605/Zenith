#!/usr/bin/env python3
"""Execute the production integer loop/cancel publications without privileged AP entry.

The appended harness supplies a user-space slot; no production instruction is
replaced. AP startup, fault entry and halt acknowledgement need the QEMU gate.
"""
import ctypes
import platform
from pathlib import Path
import subprocess
import tempfile
import threading
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
HARNESS = r'''
.text
.globl test_prepare
.type test_prepare,@function
test_prepare:
    mov %rdi, .Literations(%rip)
    movq $0, .Lcancel(%rip)
    movq $0, .Lstop(%rip)
    lea .Lslots(%rip), %rdi
    xor %eax, %eax
    mov $8, %ecx
    rep stosq
    ret
.globl test_run
.type test_run,@function
test_run:
    push %rbx
    push %r12
    sub $8, %rsp
    lea .Lslots(%rip), %r12
    call .Lwork
    add $8, %rsp
    pop %r12
    pop %rbx
    ret
.globl test_stopped
.type test_stopped,@function
test_stopped:
    mov .Lstop(%rip), %rax
    ret
'''


@unittest.skipUnless(platform.machine().lower() in ("x86_64", "amd64"), "executes x64 production assembly")
class CpuWorkerNativeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.scratch = tempfile.TemporaryDirectory(prefix='zenith-cpu-native-')
        source = Path(cls.scratch.name)/'workers.s'
        source.write_text((ROOT/'src/Native/x64/CpuWorkers.s').read_text()+HARNESS)
        library = source.with_suffix('.so')
        subprocess.run(['clang', '-shared', '-fPIC', str(source), '-o', str(library)], check=True)
        cls.native = ctypes.CDLL(str(library))
        cls.native.test_prepare.argtypes = [ctypes.c_uint64]
        cls.native.zenith_cpu_field.argtypes = [ctypes.c_uint64, ctypes.c_uint64]
        cls.native.zenith_cpu_field.restype = ctypes.c_uint64
        cls.native.test_stopped.restype = ctypes.c_uint64

    @classmethod
    def tearDownClass(cls):
        cls.scratch.cleanup()

    def field(self, index):
        return self.native.zenith_cpu_field(0, index)

    def test_real_loop_checksum_and_success_after_cancel(self):
        for cancel in (True, False, True, False):
            self.native.test_prepare(100003)
            if cancel:
                self.native.zenith_cpu_cancel()
            self.native.test_run()
            self.assertEqual(self.field(7), int(cancel))
            self.assertLess(self.field(3), self.field(4))
            if not cancel:
                expected = 0xcbf29ce484222325
                for i in range(100003, 0, -1):
                    expected = ((expected ^ i) * 0x100000001b3) & ((1 << 64)-1)
                self.assertEqual(self.field(5), expected)

    def test_cancel_running_job_and_join_without_replay(self):
        self.native.test_prepare(1000000000)
        worker = threading.Thread(target=self.native.test_run)
        worker.start()
        try:
            deadline = time.monotonic()+2
            while not self.field(3) and time.monotonic() < deadline:
                time.sleep(0.001)
            self.assertNotEqual(self.field(3), 0, 'worker entered real loop')
            self.native.zenith_cpu_cancel()
            worker.join(2)
            self.assertFalse(worker.is_alive(), 'bounded cancellation acknowledgement')
            self.assertEqual(self.field(7), 1)
        finally:
            self.native.zenith_cpu_cancel()
            worker.join(5)

    def test_stop_publishes_cancel_before_quiescence_request(self):
        self.native.test_prepare(100003)
        self.native.zenith_cpu_stop()
        self.assertEqual(self.native.test_stopped(), 1)
        self.native.test_run()
        self.assertEqual(self.field(7), 1)

    def test_slot_access_bounds(self):
        self.assertEqual(self.native.zenith_cpu_field(8, 0), 0)
        self.assertEqual(self.native.zenith_cpu_field(0, 8), 0)


if __name__ == '__main__':
    unittest.main()
