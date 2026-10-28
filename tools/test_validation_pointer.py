import importlib.util
from pathlib import Path
import struct
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('pointer',ROOT/'deploy/distributor/validation-pointer.py')
pointer=importlib.util.module_from_spec(spec);spec.loader.exec_module(pointer)

class PointerTests(unittest.TestCase):
    address=0xffffffff804ee6d8
    def code(self):
        return ('mov 0x35f5fd(%rip),%edi # '+format(self.address,'x')+' <'+pointer.MANAGER+'>\n'
                'mov 0x4(%rdx),%edx\nrex movzbl 0xc(%rdi),%edi\nmovzbl 0xd(%rdx),%edx\n')
    def test_exact_native_access_contract_is_required(self):
        rows=format(self.address,'x')+' d '+pointer.MANAGER+'\n'
        with patch.object(pointer.subprocess,'run',side_effect=[SimpleNamespace(stdout=rows),SimpleNamespace(stdout=self.code())]) as run:
            self.assertEqual(self.address,pointer.layout(Path('fixture.elf')))
        self.assertEqual('nm',run.call_args_list[0].args[0][0])
        self.assertEqual('objdump',run.call_args_list[1].args[0][0])
        for before,after in (('0x4(%rdx)','0x8(%rdx)'),('0xc(%rdi)','0xe(%rdi)'),(format(self.address,'x'),'ffffffff804ee000')):
            with patch.object(pointer.subprocess,'run',side_effect=[SimpleNamespace(stdout=rows),SimpleNamespace(stdout=self.code().replace(before,after))]):
                with self.assertRaises(ValueError):pointer.layout(Path('fixture.elf'))
    def test_missing_or_ambiguous_slot_is_not_guessed(self):
        for text in ('', (format(self.address,'x')+' d '+pointer.MANAGER+'\n')*2):
            with patch.object(pointer.subprocess,'run',return_value=SimpleNamespace(stdout=text)):
                with self.assertRaises(ValueError):pointer.layout(Path('fixture.elf'))
    def test_read_is_bounded_to_mouse_coordinates_and_buttons(self):
        calls=[]
        def read(monitor,address,count):
            calls.append((monitor,address,count));return struct.pack('<iiiBBBB',910,156,123,1,0,255,255)
        row=pointer.observe('monitor',self.address,read)
        self.assertEqual([('monitor',self.address,2)],calls)
        self.assertEqual(dict(x=910,y=156,left=True,right=False,observation_only=True),row)
        self.assertNotIn('scroll',row);self.assertNotIn('middle',row)
    def test_invalid_or_incomplete_state_is_not_reported(self):
        for raw in (b'',struct.pack('<iiiBBBB',-1,156,0,1,0,0,0),struct.pack('<iiiBBBB',10,156,0,7,0,0,0)):
            with self.assertRaises(ValueError):pointer.observe('monitor',self.address,lambda *args:raw)

if __name__=='__main__':unittest.main()
