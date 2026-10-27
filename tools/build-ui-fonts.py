#!/usr/bin/env python3
"""Rasterize bounded Inter atlases for the freestanding UI library (Pillow required)."""
from pathlib import Path
import argparse
import hashlib
import math
ROOT = Path(__file__).resolve().parents[1]

def build(output):
    try:
        from PIL import Image, ImageDraw, ImageFont
    except ImportError as error:
        raise SystemExit('UI atlas generation requires Python Pillow; install it for this build interpreter') from error
    lines = ['/* Generated Inter A8 atlases; see Resources/Fonts licenses. */', '#include "ui.h"']
    for name, face, size in [('body', 'Regular', 14), ('label', 'SemiBold', 14),
                             ('heading', 'SemiBold', 24), ('caption', 'Regular', 12)]:
        path = ROOT / ('Resources/Fonts/Inter-' + face + '.ttf')
        font = ImageFont.truetype(str(path), size, layout_engine=ImageFont.Layout.BASIC)
        ascent, descent = font.getmetrics()
        width = max(font.getbbox(chr(c))[2] for c in range(32, 127)) + 3
        height = ascent + descent
        pixels, advances = bytearray(), []
        for c in range(32, 127):
            image = Image.new('L', (width, height))
            ImageDraw.Draw(image).text((1, ascent), chr(c), font=font, fill=255, anchor='ls')
            pixels.extend(image.tobytes())
            advances.append(max(1, math.ceil(font.getlength(chr(c)))))
        lines.append('/* Source SHA256: ' + hashlib.sha256(path.read_bytes()).hexdigest() + ' */')
        lines.append('static const unsigned char ' + name + '_advance[]={' + ','.join(map(str, advances)) + '};')
        lines.append('static const unsigned char ' + name + '_pixels[]={')
        lines.extend(','.join(map(str, pixels[i:i+64])) + ',' for i in range(0, len(pixels), 64))
        lines.append('};')
        lines.append('const struct zui_font zui_' + name + '={' +
                     f'{width},{height},{ascent},{name}_advance,{name}_pixels' + '};')
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text('\n'.join(lines) + '\n')

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', type=Path)
    build(parser.parse_args().output)
