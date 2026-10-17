#!/usr/bin/env python3
"""Host algorithm checks only; native descriptor/containment proof is in the guest gate."""
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class SunbeamStreams(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix="zenith-sunbeam-")
        folder = Path(cls.temp.name)
        wrapper = folder / "host.c"
        wrapper.write_text(r'''
#define ZENITH_NATIVE_H
#include <unistd.h>
#include <fcntl.h>
#include <sys/stat.h>
#include <errno.h>
#include <stdlib.h>
#define Z_OPEN_READ 0UL
#define Z_OPEN_DIRECTORY 1UL
#define Z_OPEN_WRITE 2UL
#define Z_OPEN_CREATE 4UL
#define Z_OPEN_TRUNCATE 8UL
#define Z_OPEN_APPEND 16UL
struct zenith_stat { unsigned long size; unsigned int mode, uid, gid, reserved; };
struct zenith_dirent { struct zenith_stat stat; char name[256]; };
static long zenith_isatty(unsigned long fd) { return isatty(fd); }
static long zenith_write(const void *p, unsigned long n) { return write(1,p,n); }
static void zenith_exit(int code) { exit(code); }
static long zenith_identity(long n) { return n==39?getpid():n==102?getuid():getgid(); }
static long zenith_call(long n, unsigned long a, unsigned long b, unsigned long c) {
    long r=-1;
    if(n==0)r=read(a,(void *)b,c);
    else if(n==1)r=write(a,(void *)b,c);
    else if(n==3)r=close(a);
    else if(n==2){char path[1025];if(b>1024)return -36;for(unsigned long i=0;i<b;i++)path[i]=((char *)a)[i];path[b]=0;r=open(path,O_RDONLY);}
    else return -38;
    return r<0?-errno:r;
}
''' + '#include "' + str(ROOT / 'userland/sunbeam/main.c') + '"\n')
        cls.binary = folder / "sunbeam"
        compiled = subprocess.run(["gcc", "-O2", str(wrapper), "-o", str(cls.binary)], capture_output=True, text=True)
        if compiled.returncode: raise RuntimeError(compiled.stderr)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def run_tool(self, *args, data=b""):
        return subprocess.run([str(self.binary), *args], input=data, capture_output=True, timeout=5)

    def test_wc_binary_and_word_boundaries(self):
        self.assertEqual(b"2 4 18\n", self.run_tool("wc", data=b"one two\nthree\nlast").stdout)
        self.assertEqual(b"256\n", self.run_tool("wc", "-c", data=bytes(range(256))).stdout)
        self.assertEqual(b"1\n", self.run_tool("wc", "-w", data=b"a" * 10000).stdout)
        self.assertEqual(b"0 0 0\n", self.run_tool("wc").stdout)

    def test_head_zero_does_not_read_and_preserves_partial_line(self):
        self.assertEqual(b"", self.run_tool("head", "-n", "0", data=b"abc").stdout)
        self.assertEqual(b"one\ntwo\n", self.run_tool("head", "-n", "2", data=b"one\ntwo\nthree").stdout)
        self.assertEqual(b"partial", self.run_tool("head", data=b"partial").stdout)

    def test_tail_wrapping_and_unterminated_last_line(self):
        self.assertEqual(b"two\nthree", self.run_tool("tail", "-n", "2", data=b"one\ntwo\nthree").stdout)
        self.assertEqual(b"two\nthree\n", self.run_tool("tail", "-n", "2", data=b"one\ntwo\nthree\n").stdout)
        self.assertEqual(b"last\n", self.run_tool("tail", "-n", "1", data=b"long line\n" * 40000 + b"last\n").stdout)
        self.assertEqual(b"", self.run_tool("tail", "-n", "0", data=b"abc").stdout)

    def test_tail_is_bounded_and_errors_use_stderr(self):
        result = self.run_tool("tail", data=b"x" * 65537)
        self.assertEqual(1, result.returncode)
        self.assertEqual(b"", result.stdout)
        self.assertIn(b"64 KiB", result.stderr)
        for value in ("1025", "-1", "9999999999999999999999", "nope"):
            self.assertEqual(1, self.run_tool("head", "-n", value).returncode)

    def test_file_operands_and_utf8_bytes(self):
        path = Path(self.temp.name) / "input"
        path.write_bytes("a\nü\n終".encode())
        self.assertEqual("ü\n終".encode(), self.run_tool("tail", "-n", "2", str(path)).stdout)
        self.assertEqual(b"2", self.run_tool("wc", "-l", str(path)).stdout.split(b" ")[0])


if __name__ == "__main__":
    unittest.main()
