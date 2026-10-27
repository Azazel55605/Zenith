import importlib.util
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('graphical_replay',ROOT/'tools/graphical-input-replay.py')
replay=importlib.util.module_from_spec(spec);spec.loader.exec_module(replay)

class GraphicalReplayTests(unittest.TestCase):
    def test_definitions_do_not_run_builds_or_live_setup(self):
        definitions,sequence=replay.desktop_plan((ROOT/'tools/native-graphics-smoke.py').read_text())
        env={};exec(definitions,env)
        self.assertIn('native run gui-probe | logger; echo G_CLOSE=$? | logger',env['scripts'])
        self.assertNotIn('subprocess',env);self.assertNotIn('served',env)
        self.assertIn('audit',sequence.co_names)
    def test_v024_script_contract_indices_are_preserved(self):
        # CI can use a shallow source checkout without release tag refs. The real
        # immutable harness is separately exercised by the integration replay.
        source=(ROOT/'tools/native-graphics-smoke.py').read_text()
        definitions,sequence=replay.desktop_plan(source);env={};exec(definitions,env)
        self.assertEqual('g4',env['scripts']['native run gui-probe | logger; echo G_INTERACTIVE=$? | logger'])
        self.assertIn('boot_image',sequence.co_names)
    def test_desktop_environment_supplies_audit_dependencies(self):
        import tempfile
        from types import SimpleNamespace
        source="""def main():
    gui_texts=[]
    quota=''
    texts=[]
    texts += []
    scripts={}
    try:
        m=h.Machine(keep/'desktop')
        assert hashlib.sha256(b'x').hexdigest()
        import types
        audit=types.SimpleNamespace(returncode=0)
        assert audit.returncode==0
    finally:
        pass
"""
        with tempfile.TemporaryDirectory() as directory:
            folder=Path(directory);harness=folder/'harness.py';harness.write_text(source)
            replay.full_replay(None,folder/'m.sock',folder,harness,folder/'output-x64/Zenith.elf',SimpleNamespace())
    def test_source_drift_fails_before_boot(self):
        source=(ROOT/'tools/native-graphics-smoke.py').read_text()
        for altered in (source.replace("keep/'desktop'","keep/'other'"),source.replace('audit.returncode==0','True')):
            with self.assertRaises(ValueError):replay.desktop_plan(altered)

if __name__=='__main__':unittest.main()
