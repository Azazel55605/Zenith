#!/usr/bin/env python3
"""Launcher contract tests with a fake QEMU executable, without touching host networking."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
ROOT=Path(__file__).resolve().parents[1]
class Launcher(unittest.TestCase):
    def launch(self, **overrides):
        with tempfile.TemporaryDirectory(prefix='zenith-run-') as tmp:
            tmp=Path(tmp); disk=tmp/'disk.img';disk.touch()
            qemu=tmp/'qemu';qemu.write_text('#!/usr/bin/env python3\nimport json,sys\nprint(json.dumps(sys.argv[1:]))\n');qemu.chmod(0o755)
            env={**os.environ,'DISK':str(disk),'QEMU':str(qemu),'UI':'headless','NETWORK':'user',**overrides}
            result=subprocess.run(['bash',str(ROOT/'tools/run.sh'),'live'],env=env,capture_output=True,text=True)
            return result,json.loads(result.stdout) if result.returncode==0 else []
    def test_user_has_one_supported_adapter_and_no_monitor_on_com1(self):
        result,args=self.launch();self.assertEqual(result.returncode,0)
        self.assertIn('e1000e,netdev=net0,id=nic0',args);self.assertIn('user,id=net0',args)
        self.assertIn('none',args);self.assertNotIn('virtio-gpu-pci',args)
        self.assertEqual(args[args.index('-monitor')+1],'none');self.assertEqual(args[args.index('-serial')+1],'stdio')
    def test_tap_uses_preconfigured_interface_without_scripts(self):
        result,args=self.launch(NETWORK='tap',TAP_IFACE='zenith0');self.assertEqual(result.returncode,0)
        self.assertIn('tap,id=net0,ifname=zenith0,script=no,downscript=no',args)
    def test_off_and_desktop(self):
        result,args=self.launch(NETWORK='off',UI='desktop');self.assertEqual(result.returncode,0)
        self.assertNotIn('-netdev',args);self.assertIn('virtio-gpu-pci',args)
    def test_invalid_profile_and_interface_rejected(self):
        for overrides in [dict(NETWORK='bridge'),dict(UI='bad'),dict(NETWORK='tap',TAP_IFACE='x,script=evil'),dict(NETWORK='tap',TAP_IFACE='')]:
            result,_=self.launch(**overrides);self.assertEqual(result.returncode,2)
if __name__=='__main__':unittest.main()
