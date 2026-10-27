"""Keep the actual documentation graph and master inventory coherent in CI."""
import importlib.util
from pathlib import Path
import unittest

class DocumentationTests(unittest.TestCase):
    def test_current_documentation_and_tracker_boundaries(self):
        path=Path(__file__).with_name('check-documentation.py')
        spec=importlib.util.spec_from_file_location('documentation_check',path)
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        self.assertEqual(module.check(),0,'repair the reported documentation/work-owner errors')

if __name__=='__main__':unittest.main()
