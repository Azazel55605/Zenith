#!/usr/bin/env python3
"""Actual signing tool, immutable output and native RSA-PSS verification checks."""
import ctypes
import hashlib
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
LIB = Path(sys.argv.pop(1)).resolve() if len(sys.argv) > 1 else None

class SignedToolTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        subprocess.run(['dotnet', 'build', str(ROOT / 'tools/Zenith.PackageTool'), '-c', 'Release'], check=True, stdout=subprocess.DEVNULL)
        cls.tool = ['dotnet', str(ROOT / 'tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll')]
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='zenith-signed-tool-'); self.root = Path(self.temp.name)
        self.run_tool('keygen', self.root / 'keys')
        source = self.root / 'source/usr/share/zenith/scripts'; source.mkdir(parents=True)
        (source / 'hello.sh').write_text('echo hello-package\n')
        self.run_tool('build', 'hello-tools', '1.0.0', self.root / 'source', self.root / 'bundle', '--repo', 'core')
        self.run_tool('seal', self.root / 'bundle', self.root / 'keys/private.pem', self.root / 'hello.zpk')
    def tearDown(self): self.temp.cleanup()
    def run_tool(self, *args, success=True):
        result = subprocess.run([*self.tool, *map(str, args)], capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr); return result
    def test_round_trip_and_private_key_permissions(self):
        result = self.run_tool('check', self.root / 'hello.zpk', self.root / 'keys/public.pem')
        self.assertIn('repo=core', result.stdout)
        self.assertEqual((self.root / 'keys/private.pem').stat().st_mode & 0o777, 0o600)
        self.assertEqual((self.root / 'keys').stat().st_mode & 0o777, 0o700)
    def test_tamper_and_no_output_overwrite(self):
        data = bytearray((self.root / 'hello.zpk').read_bytes()); data[-1] ^= 1
        (self.root / 'bad.zpk').write_bytes(data)
        self.run_tool('check', self.root / 'bad.zpk', self.root / 'keys/public.pem', success=False)
        self.run_tool('seal', self.root / 'bundle', self.root / 'keys/private.pem', self.root / 'hello.zpk', success=False)
        self.run_tool('keygen', self.root / 'keys', success=False)
    def test_payload_tampering_before_seal_is_refused(self):
        (self.root / 'bundle/payload/usr/share/zenith/scripts/hello.sh').write_text('changed\n')
        self.run_tool('seal', self.root / 'bundle', self.root / 'keys/private.pem', self.root / 'changed.zpk', success=False)
        self.assertFalse((self.root / 'changed.zpk').exists())
    def test_special_node_and_link_sources_refused(self):
        (self.root / 'key-link').symlink_to(self.root / 'keys/private.pem')
        self.run_tool('seal', self.root / 'bundle', self.root / 'key-link', self.root / 'linked.zpk', success=False)
    @unittest.skipUnless(LIB, 'native shared bridge not supplied')
    def test_native_verifier_and_entropy_independence(self):
        bridge = ctypes.CDLL(str(LIB)); verify = bridge.zenith_pkg_verify
        verify.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t]; verify.restype = ctypes.c_int
        archive = (self.root / 'hello.zpk').read_bytes(); header, body = archive.split(b'\n', 1)
        length, siglen = map(int, header.split(b'\t')[-2:]); key = (self.root / 'keys/public.pem').read_bytes() + b'\0'
        digest = hashlib.sha256(header + b'\n' + body[:length]).digest(); signature = body[length:]
        bridge.zenith_tls_test_entropy_fault(1)
        self.assertEqual(verify(key, len(key), digest, signature, siglen), 0)
        self.assertEqual(verify(key, len(key), digest, None, 0), 0)
        bad = bytes([digest[0] ^ 1]) + digest[1:]
        self.assertNotEqual(verify(key, len(key), bad, signature, siglen), 0)
        self.assertNotEqual(verify(b'bad\0', 4, digest, signature, siglen), 0)
        self.assertNotEqual(verify(key, len(key), digest, signature, siglen - 1), 0)
        self.assertEqual(verify(key, len(key), digest, signature, siglen), 0)
        bridge.zenith_tls_test_entropy_fault(0)

if __name__ == '__main__': unittest.main()
