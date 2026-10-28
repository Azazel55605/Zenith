import importlib.util
from pathlib import Path
import sys
import unittest
from PIL import Image,ImageDraw
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'tools'))
spec=importlib.util.spec_from_file_location('maximize_replay',ROOT/'tools/maximize-input-replay.py')
replay=importlib.util.module_from_spec(spec);spec.loader.exec_module(replay)

class GeometryTests(unittest.TestCase):
    def pixels(self,x,y):
        image=Image.new('RGB',(1280,800),(30,30,30));draw=ImageDraw.Draw(image)
        draw.line((x+31,y+60,x+160,y+60),fill='#7ecac7')
        draw.point((x+80,y+61),fill='#2a2c31')
        return image.tobytes()
    def test_restored_search_field_identifies_actual_window_geometry(self):
        self.assertEqual((368,178,1280,800),replay.restored_geometry(1280,800,self.pixels(368,178)))
    def test_stale_maximized_search_cannot_acknowledge_restore(self):
        self.assertIsNone(replay.restored_geometry(1280,800,self.pixels(0,44)))
    def test_unfocused_or_incomplete_field_does_not_supply_coordinates(self):
        self.assertIsNone(replay.restored_geometry(1280,800,Image.new('RGB',(1280,800),(30,30,30)).tobytes()))
        self.assertIsNone(replay.restored_geometry(1280,800,self.pixels(1000,178)))

    def test_maximized_chrome_is_independent_of_native_page_content(self):
        image=Image.new('RGB',(1280,800),(30,30,30));draw=ImageDraw.Draw(image);cx=1280-58;ink=(150,160,170)
        draw.line((cx-3,16,cx+4,16),fill=ink);draw.line((cx+5,16,cx+5,23),fill=ink)
        for j in range(-4,5):draw.point((1258+j,22+j),fill=ink);draw.point((1258+j,22-j),fill=ink)
        self.assertTrue(replay.maximized_chrome(1280,800,image.tobytes()))
        draw.point((cx+5,16),fill=(30,30,30))
        self.assertFalse(replay.maximized_chrome(1280,800,image.tobytes()))
    def test_restored_window_cannot_satisfy_maximized_chrome(self):
        self.assertFalse(replay.maximized_chrome(1280,800,self.pixels(368,178)))

if __name__=='__main__':unittest.main()
