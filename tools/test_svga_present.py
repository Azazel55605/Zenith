"""Direct SVGA II presentation: damaged rows, integer scale, FIFO update command and refusals."""
import ctypes as ct
from pathlib import Path
import subprocess
import tempfile
import unittest
ROOT = Path(__file__).resolve().parents[1]
U32 = ct.c_uint32


class SvgaPresentTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.dir = tempfile.TemporaryDirectory()
        lib = Path(cls.dir.name) / 'svga.so'
        subprocess.run(['clang', '-shared', '-fPIC', '-O2', '-std=c11', '-ffreestanding', '-fno-builtin', '-DZENITH_CPU_HOST_TEST',
                        str(ROOT / 'src/Native/Cpu/workers.c'), '-o', str(lib)], check=True)
        cls.lib = ct.CDLL(str(lib))
        f = cls.lib.zenith_svga_test_present
        f.argtypes = [ct.POINTER(U32), ct.c_uint64, ct.c_uint64, ct.c_uint64, ct.POINTER(U32), ct.c_uint64,
                      ct.POINTER(U32), ct.c_uint64, ct.c_uint64, ct.c_uint64, ct.c_uint64, ct.c_uint64, ct.c_uint64]
        f.restype = ct.c_uint64
        cls.lib.zenith_svga_test_syncs.restype = ct.c_uint64

    @classmethod
    def tearDownClass(cls): cls.dir.cleanup()

    def setUp(self):
        # 64x40 visible, pitch 72 pixels; a small FIFO: 4 registers then 16 command words.
        self.width, self.height, self.pitch = 64, 40, 72 * 4
        self.fb = (U32 * (72 * 40))(*([0xdead] * (72 * 40)))
        self.fifo = (U32 * 20)()
        self.fifo[0], self.fifo[1], self.fifo[2], self.fifo[3] = 16, 80, 16, 16

    def present(self, src, stride, x, y, w, h, scale):
        return self.lib.zenith_svga_test_present(self.fb, self.pitch, self.width, self.height, self.fifo, 80,
                                                 src, stride, x, y, w, h, scale)

    def source(self, w, h, stride):
        return (U32 * (stride * h))(*[(r << 16) | c for r in range(h) for c in range(stride)])

    def command(self, at):
        return [self.fifo[(16 + ((at - 16 + 4 * i) % 64)) // 4] for i in range(5)]

    def test_damage_rows_only_and_update_rectangle(self):
        src = self.source(5, 3, 9)
        self.assertEqual(self.present(src, 9, 10, 7, 5, 3, 1), 1)
        for r in range(40):
            for c in range(72):
                want = ((r - 7) << 16 | (c - 10)) if 7 <= r < 10 and 10 <= c < 15 else 0xdead
                self.assertEqual(self.fb[r * 72 + c], want, (r, c))
        self.assertEqual(self.command(16), [1, 10, 7, 5, 3])
        self.assertEqual(self.fifo[2], 36)

    def test_integer_scale_expands_while_writing(self):
        src = self.source(3, 2, 3)
        self.assertEqual(self.present(src, 3, 4, 5, 3, 2, 2), 1)
        for r in range(10, 14):
            for c in range(8, 14):
                self.assertEqual(self.fb[r * 72 + c], ((r - 10) // 2) << 16 | (c - 8) // 2, (r, c))
        self.assertEqual(self.fb[9 * 72 + 8], 0xdead); self.assertEqual(self.fb[10 * 72 + 14], 0xdead)
        self.assertEqual(self.command(16), [1, 8, 10, 6, 4])

    def test_fifo_wraps_and_waits_for_room(self):
        src = self.source(1, 1, 1)
        self.fifo[2] = self.fifo[3] = 72   # two words before MAX
        self.assertEqual(self.present(src, 1, 0, 0, 1, 1, 1), 1)
        self.assertEqual(self.command(72), [1, 0, 0, 1, 1])
        self.assertEqual(self.fifo[2], 28)
        # Host has consumed nothing: stop just ahead of next leaves no room, so it syncs first.
        syncs = self.lib.zenith_svga_test_syncs()
        self.fifo[3] = 32
        self.assertEqual(self.present(src, 1, 1, 1, 1, 1, 1), 1)
        self.assertGreater(self.lib.zenith_svga_test_syncs(), syncs)

    def test_refusals_leave_pixels_and_fifo_alone(self):
        src = self.source(4, 4, 4)
        before = list(self.fb)
        for args in [(4, 61, 0, 4, 1, 1), (4, 0, 37, 1, 4, 1), (4, 0, 0, 0, 1, 1), (3, 0, 0, 4, 1, 1),
                     (4, 0, 0, 1, 1, 5), (4, 0, 0, 1, 1, 0), (4, 31, 0, 2, 1, 2)]:
            self.assertEqual(self.present(src, *args), 0, args)
        self.fifo[1] = 84   # MAX beyond the mapped FIFO
        self.assertEqual(self.present(src, 4, 0, 0, 1, 1, 1), 0)
        self.fifo[1] = 80; self.fifo[2] = 18   # misaligned NEXT_CMD
        self.assertEqual(self.present(src, 4, 0, 0, 1, 1, 1), 0)
        self.assertEqual(list(self.fb), before)


if __name__ == '__main__':
    unittest.main()
