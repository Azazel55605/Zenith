import importlib.util, struct, tempfile, unittest
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('build_sounds', ROOT / 'tools/build-sounds.py'); sounds = importlib.util.module_from_spec(spec); spec.loader.exec_module(sounds)


class BuildSoundsTests(unittest.TestCase):
    def test_pack_is_valid_bounded_and_reproducible(self):
        with tempfile.TemporaryDirectory() as a, tempfile.TemporaryDirectory() as b:
            sounds.main(a); sounds.main(b)
            pack = Path(a) / 'usr/share/zenith/sounds/chimes'
            names = sorted(p.stem for p in pack.glob('*.wav'))
            self.assertEqual(sorted(sounds.SOUNDS), names)
            for wav in pack.glob('*.wav'):
                data = wav.read_bytes()
                self.assertEqual(data, (Path(b) / 'usr/share/zenith/sounds/chimes' / wav.name).read_bytes())
                self.assertLess(len(data), 256 * 1024 - 32 * 1024)        # package file limit with headroom
                self.assertEqual(data[:4], b'RIFF'); self.assertEqual(data[36:40], b'data')
                channels, rate = struct.unpack('<HI', data[22:28]); bits = struct.unpack('<H', data[34:36])[0]
                self.assertEqual((channels, rate, bits), (1, 24000, 16))
                samples = struct.unpack('<%dh' % ((len(data) - 44) // 2), data[44:])
                self.assertLessEqual(max(abs(s) for s in samples), int(32767 * 10 ** (-12 / 20)) + 1)   # -12 dBFS
                self.assertLessEqual(len(samples) / 24000, 1.41)
            self.assertIn('event\tlogin\n', (pack / 'pack.tsv').read_text())


if __name__ == '__main__':
    unittest.main()
