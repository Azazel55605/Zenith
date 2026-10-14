#!/usr/bin/env python3
"""Real signed dependency-only archives and legacy/extended catalog compatibility."""
import struct
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
TOOL=ROOT/'tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll'

class Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        subprocess.run(['dotnet','build',str(ROOT/'tools/Zenith.PackageTool'),'-c','Release','--no-restore'],check=True,stdout=subprocess.DEVNULL)
    def setUp(self):
        tmp=tempfile.TemporaryDirectory();self.addCleanup(tmp.cleanup);self.root=Path(tmp.name)
        self.run_tool('keygen',self.root/'keys')
        self.source=self.root/'definition';self.source.mkdir()
        (self.source/'requires.tsv').write_text('requires\tzenith-theme-default\t>=0.1.0\n')
        self.bundle=self.root/'bundle'
    def run_tool(self,*args,ok=True):
        result=subprocess.run(['dotnet',str(TOOL),*map(str,args)],capture_output=True,text=True,timeout=30)
        self.assertEqual(0 if ok else 1,result.returncode,result.stdout+result.stderr);return result
    def build(self):self.run_tool('build-meta','zenith-style','0.1.0',self.source,self.bundle,'--repo','core')
    def test_signed_meta_has_no_payload_and_tampering_fails(self):
        self.build();manifest=(self.bundle/'manifest.tsv').read_text()
        self.assertIn('kind\tmeta\n',manifest);self.assertNotIn('file\t',manifest);self.assertFalse((self.bundle/'payload').exists())
        archive=self.root/'style.zpk';self.run_tool('seal',self.bundle,self.root/'keys/private.pem',archive)
        self.run_tool('check',archive,self.root/'keys/public.pem')
        data=bytearray(archive.read_bytes());data[-1]^=1;archive.write_bytes(data)
        self.run_tool('check',archive,self.root/'keys/public.pem',ok=False)
    def test_meta_definition_refuses_arbitrary_records_and_links(self):
        (self.source/'requires.tsv').write_text('kind\tdata\n');self.run_tool('build-meta','zenith-style','0.1.0',self.source,self.bundle,ok=False)
        (self.source/'requires.tsv').unlink();(self.source/'requires.tsv').symlink_to(self.root/'keys/private.pem')
        self.run_tool('build-meta','zenith-style','0.1.0',self.source,self.bundle,ok=False)
    def test_legacy_catalog_omits_new_manifest_and_extended_includes_it(self):
        self.build();published=self.root/'published';target=published/'core/zenith-style/0.1.0';target.mkdir(parents=True)
        self.run_tool('seal',self.bundle,self.root/'keys/private.pem',target/'zenith-style.zpk')
        payload=self.root/'payload/usr/share/doc';payload.mkdir(parents=True);(payload/'example.txt').write_text('theme sample')
        self.run_tool('build','zenith-theme-default','0.1.0',self.root/'payload',self.root/'data-bundle','--repo','core')
        target=published/'core/zenith-theme-default/0.1.0';target.mkdir(parents=True)
        self.run_tool('seal',self.root/'data-bundle',self.root/'keys/private.pem',target/'zenith-theme-default.zpk')
        for mode,name,count in [('index-seal','legacy.zri',1),('index-seal-v3','extended.zri',2)]:
            result=self.run_tool(mode,'core','1','100','1000',published,self.root/'keys/private.pem',self.root/name)
            self.assertIn('entries='+str(count),result.stdout)
            self.run_tool('index-check',self.root/name,self.root/'keys/public.pem','core')

if __name__=='__main__':unittest.main()
