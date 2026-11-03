#!/usr/bin/env python3
"""A partial/stale graphical frame must not pass C3 presentation acceptance."""
import importlib.util
from pathlib import Path
import unittest
ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('cpu_frame',ROOT/'tools/guest_cpu_frame.py')
a=importlib.util.module_from_spec(spec);spec.loader.exec_module(a)
class FrameTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):cls.rows=a.expected()
    def ppm(self,corrupt=None):
        w,h=160,150;data=bytearray(w*h*3)
        for row,source in enumerate(self.rows):data[((row+5)*w+7)*3:((row+5)*w+135)*3]=source
        if corrupt is not None:data[corrupt]^=1
        return ('P6\n'+str(w)+' '+str(h)+'\n255\n').encode()+data
    def test_complete_frame_is_located_without_window_geometry_assumption(self):
        self.assertEqual(a.locate(self.ppm(),self.rows),(7,5))
    def test_single_wrong_or_incomplete_pixel_refuses(self):
        self.assertIsNone(a.locate(self.ppm(((5+80)*160+7+90)*3),self.rows))
    def test_live_prompt_requires_public_user_host_and_focused_caret_row(self):
        from PIL import Image
        import io
        image=Image.new('RGB',(160,20),(20,22,32))
        for x in range(20,61):
            for y in range(5,15):image.putpixel((x,y),(80,200,100))
        for x in range(94,107):
            for y in range(8,11):image.putpixel((x,y),(200,200,200))
        data=io.BytesIO();image.save(data,format='PPM')
        self.assertTrue(a.live_prompt(data.getvalue(),(110,4,112,16)))
        for x in range(20,61):
            for y in range(5,15):image.putpixel((x,y),(200,200,200))
        data=io.BytesIO();image.save(data,format='PPM')
        self.assertFalse(a.live_prompt(data.getvalue(),(110,4,112,16)))
    def test_unrelated_background_refuses(self):
        self.assertIsNone(a.locate(b'P6\n160 150\n255\n'+bytes(160*150*3),self.rows))
if __name__=='__main__':unittest.main()
