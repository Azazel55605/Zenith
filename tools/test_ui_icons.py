#!/usr/bin/env python3
import importlib.util, tempfile, unittest
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]

def tool():
    spec = importlib.util.spec_from_file_location('ui_icons', ROOT/'tools/build-ui-icons.py')
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module); return module

class IconTests(unittest.TestCase):
    def test_every_vendored_icon_rasterizes_deterministically(self):
        t = tool()
        with tempfile.TemporaryDirectory() as d:
            a, b = Path(d)/'a.c', Path(d)/'b.c'
            t.build(a); t.build(b)
            self.assertEqual(a.read_bytes(), b.read_bytes())
            text = a.read_text()
            for svg in (ROOT/'userland/ui/icons').glob('*.svg'):
                for size in t.SIZES: self.assertIn(f'{{"{svg.stem}",{size},', text)

    def test_x_icon_is_symmetric_and_inked(self):
        mask = tool().rasterize((ROOT/'userland/ui/icons/x.svg').read_text(), 24)
        self.assertEqual(len(mask), 576)
        self.assertGreater(mask[12*24+12], 200)          # crossing point is solid
        self.assertEqual(mask[0], 0)                      # corner is empty
        for y in range(24):
            for x in range(24): self.assertLessEqual(abs(mask[y*24+x]-mask[y*24+23-x]), 8)

    def test_run_length_round_trip(self):
        t = tool()
        for mask in (bytes(576), bytes([255])*576, bytes([0]*300 + [7]*300 + [0]*24), t.rasterize((ROOT/'userland/ui/icons/zap.svg').read_text(), 48)):
            self.assertEqual(t.decode(t.encode(mask), len(mask)), mask)

    def test_unsupported_svg_is_refused(self):
        with self.assertRaises(ValueError):
            tool().rasterize('<svg viewBox="0 0 24 24"><text>x</text></svg>', 16)
        with self.assertRaises(ValueError):
            tool().rasterize('<svg viewBox="0 0 24 24"><path d="M 1 1 B 2 2"/></svg>', 16)
        with self.assertRaises(ValueError):
            tool().rasterize('<!DOCTYPE s [<!ENTITY a "aaaa">]><svg viewBox="0 0 24 24"/>', 16)

if __name__ == '__main__': unittest.main()
