import importlib.util, tempfile, unittest
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('icons', ROOT / 'tools/build-ui-icons.py'); icons = importlib.util.module_from_spec(spec); spec.loader.exec_module(icons)


class IconSetTests(unittest.TestCase):
    def test_sets_cover_every_icon_in_their_folder_at_another_weight(self):
        with tempfile.TemporaryDirectory() as d:
            icons.build_package(d); icons.build_package(d, 'bold'); icons.build_package(d, 'fine')
            base = Path(d) / icons.ICON_DIR
            names = sorted(p.name for p in base.glob('*.zic'))
            for s in ('bold', 'fine'):
                self.assertEqual(names, sorted(p.name for p in (base / s).glob('*.zic')))
                self.assertLessEqual(len(list((base / s).iterdir())) + 1, 64)   # with the about file
            def ink(path): data = path.read_bytes(); return sum(data[9:])   # rough coverage of the first sizes
            self.assertGreater(ink(base / 'bold/house.zic'), ink(base / 'house.zic'))
            self.assertLess(ink(base / 'fine/house.zic'), ink(base / 'house.zic'))


if __name__ == '__main__':
    unittest.main()
