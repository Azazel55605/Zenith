"""Known-answer checks against the same freestanding primitives embedded in the SMB ELF."""
import hashlib
import hmac
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]

class SmbCryptoTests(unittest.TestCase):
    def test_wire_primitives(self):
        with tempfile.TemporaryDirectory() as folder:
            work = Path(folder)
            obj = work / 'crypto.o'
            subprocess.run(['python3', '-B', str(ROOT / 'tools/build-smb-crypto.py'), str(obj)], check=True)
            source = work / 'vectors.c'
            source.write_text(r'''
#include "crypto.h"
#include <stdio.h>
#include <string.h>

static void print_hex(const unsigned char *p, unsigned n)
{
    while (n--) printf("%02x", *p++);
    putchar('\n');
}

int main(void)
{
    const unsigned char key[16] = {0x2b,0x7e,0x15,0x16,0x28,0xae,0xd2,0xa6,0xab,0xf7,0x15,0x88,0x09,0xcf,0x4f,0x3c};
    const unsigned char message[16] = {0x6b,0xc1,0xbe,0xe2,0x2e,0x40,0x9f,0x96,0xe9,0x3d,0x7e,0x11,0x73,0x93,0x17,0x2a};
    unsigned char out[32];
    smb_md4("abc", 3, out); print_hex(out,16);
    smb_cmac(key, NULL, 0, out); print_hex(out,16);
    smb_cmac(key, message, sizeof(message), out); print_hex(out,16);
    smb_hmac_sha256(key, message, sizeof(message), out); print_hex(out,32);
    smb_hmac_md5(key, message, 3, message + 3, 7, message + 10, 6, out); print_hex(out,16);
    return 0;
}
''')
            binary = work / 'vectors'
            subprocess.run(['cc', '-no-pie', '-I', str(ROOT / 'userland/smb'),
                            str(source), str(obj), '-o', str(binary)], check=True)
            actual = subprocess.check_output([binary], text=True).splitlines()
            wire = work / 'wire-checks'
            smb = ROOT / 'userland/smb'
            subprocess.run(['cc', '-no-pie', '-I', str(smb), str(ROOT / 'tools/fixtures/smb/wire_checks.c'),
                            *[str(smb / name) for name in ('client.c', 'auth.c', 'shares.c')],
                            str(obj), '-o', str(wire)], check=True)
            subprocess.run([wire], check=True)
        key = bytes.fromhex('2b7e151628aed2a6abf7158809cf4f3c')
        msg = bytes.fromhex('6bc1bee22e409f96e93d7e117393172a')
        self.assertEqual(actual, [
            'a448017aaf21d8525fc10ae87aa6729d',
            'bb1d6929e95937287fa37d129b756746',
            '070a16b46b4d4144f79bdd9dd04a287c',
            hmac.new(key, msg, hashlib.sha256).hexdigest(),
            hmac.new(key, msg, hashlib.md5).hexdigest(),
        ])

if __name__ == '__main__':
    unittest.main()
