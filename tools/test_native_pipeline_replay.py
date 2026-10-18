import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('pipeline_replay',ROOT/'tools/native-pipeline-replay.py')
r=importlib.util.module_from_spec(spec);spec.loader.exec_module(r)

class ReplayTests(unittest.TestCase):
    def test_current_harness_warmup_is_selected_without_install_build_or_password_phases(self):
        selected=r.warmup((ROOT/'tools/native-process-smoke.py').read_text())
        constants=str(selected.co_consts)
        self.assertIn('N_TIMEOUT',constants);self.assertIn('N_STREAM_CANCEL',constants)
        self.assertNotIn('N_INSTALL',selected.co_consts);self.assertNotIn('install sata0 --yes',constants);self.assertNotIn('passwd root',constants)
        self.assertNotIn('N_STREAM_BYTES_',constants)

    def test_warmup_uses_actual_order_and_refuses_ambiguous_or_missing_boundaries(self):
        text="""try:
    boot('live')
    boot('installed', disk)
    status('first', 'A')
    command('second', 'B')
    for attempt in range(64): pass
finally: pass
"""
        calls=[]
        exec(r.warmup(text),dict(status=lambda *a:calls.append(a),command=lambda *a:calls.append(a)))
        self.assertEqual([('first','A'),('second','B')],calls)
        for changed in (text.replace("'installed'","'other'"),text.replace('range(64)','range(32)'),text.replace("    status('first', 'A')","    boot('installed', disk)")):
            with self.assertRaises(ValueError):r.warmup(changed)

    def test_existing_evidence_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as folder,patch.object(r.boot_image,'verify') as verify:
            keep=Path(folder);sentinel=keep/'old.log';sentinel.write_text('retain')
            with self.assertRaises(FileExistsError):r.replay(keep/'disk.img',keep/'kernel.elf',keep)
            self.assertEqual('retain',sentinel.read_text());verify.assert_not_called()

    def test_wrong_elf_refuses_before_boot_or_public_package_extraction(self):
        with tempfile.TemporaryDirectory() as folder:
            base=Path(folder);disk=base/'disk.img';disk.write_bytes(b'original fixture');elf=base/'kernel.elf';elf.write_bytes(b'wrong kernel')
            with patch.object(r.boot_image,'verify',return_value=dict(current='different')),patch.object(r,'load') as load,patch.object(r,'public_packages') as packages:
                with self.assertRaisesRegex(ValueError,'does not match'):r.replay(disk,elf,base/'new')
                load.assert_not_called();packages.assert_not_called()
            self.assertEqual(b'original fixture',disk.read_bytes())

    def test_truncated_root_refuses_instead_of_looping_or_modifying_input(self):
        with tempfile.TemporaryDirectory() as folder:
            base=Path(folder);disk=base/'disk.img';disk.write_bytes(b'short')
            with patch.object(r.boot_image,'verify',return_value=dict(root='root')),patch.object(r.boot_image,'partitions',return_value=[('root','root',1,2)]):
                with self.assertRaisesRegex(ValueError,'truncated'):r.public_packages(disk,base)
            self.assertEqual(b'short',disk.read_bytes())

if __name__=='__main__':unittest.main()
