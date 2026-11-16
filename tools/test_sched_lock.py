#!/usr/bin/env python3
"""C5.1 scheduler lock (src/Native/Cpu/sched.c) on host threads acting as CPUs."""
import ctypes as ct
import platform
import subprocess
import tempfile
import threading
import unittest
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]


@unittest.skipUnless(platform.machine().lower() in ('x86_64', 'amd64'), 'executes x64 atomics')
class SchedulerLockTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix='zn-sched-')
        lib = Path(cls.temp.name) / 'sched.so'
        subprocess.run(['clang', '-shared', '-fPIC', '-O2', '-std=c11', '-ffreestanding', '-fno-builtin', '-Wall', '-Wextra', '-Werror',
                        '-DZENITH_SCHED_HOST_TEST', str(ROOT / 'src/Native/Cpu/sched.c'), '-o', str(lib)], check=True)
        cls.lib = ct.CDLL(str(lib))
        for name, arity in [('lock', 0), ('unlock', 0), ('state', 1), ('host_cpu', 1), ('host_init', 0), ('host_work', 2), ('host_counter', 0)]:
            f = getattr(cls.lib, 'zenith_sched_' + name); f.argtypes = [ct.c_uint64] * arity; f.restype = ct.c_uint64

    @classmethod
    def tearDownClass(cls): cls.temp.cleanup()

    def setUp(self): self.lib.zenith_sched_host_init()

    def test_excludes_other_cpus(self):
        rounds = 20000
        threads = [threading.Thread(target=self.lib.zenith_sched_host_work, args=(cpu, rounds)) for cpu in range(4)]
        for t in threads: t.start()
        for t in threads: t.join()
        self.assertEqual(4 * rounds, self.lib.zenith_sched_host_counter())
        self.assertEqual(0, self.lib.zenith_sched_state(5), 'free afterwards')
        self.assertEqual(0, self.lib.zenith_sched_state(3), 'no refusals')
        self.assertEqual(4 * rounds, self.lib.zenith_sched_state(0))
        self.assertGreaterEqual(self.lib.zenith_sched_state(4), 2, 'nesting recorded')

    def test_nests_on_one_cpu_and_releases_only_at_the_outermost(self):
        self.lib.zenith_sched_host_cpu(2)
        self.assertEqual(1, self.lib.zenith_sched_lock()); self.assertEqual(1, self.lib.zenith_sched_lock())
        self.assertEqual(3, self.lib.zenith_sched_state(5))
        self.assertEqual(1, self.lib.zenith_sched_unlock()); self.assertEqual(3, self.lib.zenith_sched_state(5))
        self.assertEqual(1, self.lib.zenith_sched_unlock()); self.assertEqual(0, self.lib.zenith_sched_state(5))

    def test_a_cpu_that_does_not_hold_it_cannot_release_it(self):
        self.lib.zenith_sched_host_cpu(1); self.assertEqual(1, self.lib.zenith_sched_lock())
        self.lib.zenith_sched_host_cpu(3); self.assertEqual(0, self.lib.zenith_sched_unlock())
        self.assertEqual(2, self.lib.zenith_sched_state(5), 'still held by its owner')
        self.assertEqual(1, self.lib.zenith_sched_state(3))
        self.lib.zenith_sched_host_cpu(1); self.assertEqual(1, self.lib.zenith_sched_unlock())

    def test_unknown_cpu_is_refused(self):
        self.lib.zenith_sched_host_cpu(8); self.assertEqual(0, self.lib.zenith_sched_lock())
        self.assertEqual(0, self.lib.zenith_sched_state(5))

    def test_kernel_object_has_only_allowed_imports(self):
        out = Path(self.temp.name) / 'cpu'
        subprocess.run(['python3', str(ROOT / 'tools/build-cpu.py'), str(out)], check=True)
        imports = subprocess.check_output(['nm', '-u', str(out / 'cpu-sched.o')], text=True).split()
        self.assertEqual({'U', 'zenith_cpu_logical_id', 'zenith_gc_poll', 'zenith_gc_state'}, set(imports))


if __name__ == '__main__': unittest.main()
