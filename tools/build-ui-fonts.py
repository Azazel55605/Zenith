#!/usr/bin/env python3
"""Rasterize bounded Inter atlases for the freestanding UI library (Pillow required)."""
from pathlib import Path
import argparse
import hashlib
import math
ROOT = Path(__file__).resolve().parents[1]

# Printable ASCII, then a few typographic marks the decoder in userland/ui/ui.c maps after it
# (middle dot, right arrow, em dash, ellipsis), in this order.
EXTRA = (0xB7, 0x2192, 0x2014, 0x2026)
CHARS = list(range(32, 127)) + list(EXTRA)

# The shipped fonts contain Latin-1 and these additional punctuation/arrow glyphs.
# Keep the small default atlas for the other packages and its original indices for zui.
BROWSER_MARKS = (0x2013, 0x2018, 0x2019, 0x201c, 0x201d, 0x2022, 0x2190, 0x2191, 0x2193)

def build(output, mono=False, browser=False):
    try:
        from PIL import Image, ImageDraw, ImageFont
    except ImportError as error:
        raise SystemExit('UI atlas generation requires Python Pillow; install it for this build interpreter') from error
    chars = list(CHARS)
    lines = ['/* Generated Inter A8 atlases; see Resources/Fonts licenses. */', '#include "ui.h"']
    if browser:
        chars.extend(c for c in (*range(0xa1, 0x100), *BROWSER_MARKS) if c not in CHARS)
        indices = {c: index for index, c in enumerate(chars)}
        indices[0xa0] = indices[ord(' ')]
        lines.extend([
            'unsigned zenith_page_glyph(unsigned codepoint) {',
            '  if (codepoint >= 32 && codepoint <= 126) return codepoint - 32;',
            '  static const unsigned char latin1[] = {' +
            ','.join(str(indices[c]) for c in range(0xa0, 0x100)) + '};',
            '  if (codepoint >= 160 && codepoint <= 255) return latin1[codepoint - 160];',
            '  switch (codepoint) {'])
        lines.extend(f'    case {c}: return {indices[c]};' for c in sorted(indices) if c > 255)
        lines.extend(["    default: return '?' - 32;", '  }', '}'])
    faces = [('body', 'Inter-Regular', 14), ('label', 'Inter-SemiBold', 14),
             ('heading', 'Inter-SemiBold', 24), ('caption', 'Inter-Regular', 12)]
    if mono: faces.append(('mono', 'Hack-Regular', 14))
    for name, face, size in faces:
        path = ROOT / ('Resources/Fonts/' + face + '.ttf')
        font = ImageFont.truetype(str(path), size, layout_engine=ImageFont.Layout.BASIC)
        ascent, descent = font.getmetrics()
        width = max(font.getbbox(chr(c))[2] for c in chars) + 3
        height = ascent + descent
        pixels, advances = bytearray(), []
        for c in chars:
            image = Image.new('L', (width, height))
            # A discretionary soft hyphen is invisible unless the layout breaks the word here.
            if c != 0xad:
                ImageDraw.Draw(image).text((1, ascent), chr(c), font=font, fill=255, anchor='ls')
            pixels.extend(image.tobytes())
            advances.append(0 if c == 0xad else max(1, math.ceil(font.getlength(chr(c)))))
        lines.append('/* Source SHA256: ' + hashlib.sha256(path.read_bytes()).hexdigest() + ' */')
        lines.append('static const unsigned char ' + name + '_advance[]={' + ','.join(map(str, advances)) + '};')
        # Store each glyph's ink box only; empty cells (space) keep a zero box.
        ink, boxes, offsets = bytearray(), [], []
        for g in range(len(chars)):
            cell = pixels[g*width*height:(g+1)*width*height]
            xs = [i % width for i, v in enumerate(cell) if v]; ys = [i // width for i, v in enumerate(cell) if v]
            offsets.append(len(ink))
            if not xs: boxes += [0, 0, 0, 0]; continue
            x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
            boxes += [x0, y0, x1 - x0 + 1, y1 - y0 + 1]
            for yy in range(y0, y1 + 1): ink.extend(cell[yy*width + x0:yy*width + x1 + 1])
        lines.append('static const unsigned char ' + name + '_pixels[]={')
        lines.extend(','.join(map(str, ink[i:i+64])) + ',' for i in range(0, len(ink), 64))
        lines.append('};')
        lines.append('static const unsigned char ' + name + '_boxes[]={' + ','.join(map(str, boxes)) + '};')
        lines.append('static const unsigned int ' + name + '_offsets[]={' + ','.join(map(str, offsets)) + '};')
        lines.append('const struct zui_font zui_' + name + '={' +
                     f'{width},{height},{ascent},{name}_advance,{name}_pixels,{name}_boxes,{name}_offsets' + '};')
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text('\n'.join(lines) + '\n')

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', type=Path)
    parser.add_argument('--mono', action='store_true', help='also rasterize the Hack terminal face')
    parser.add_argument('--browser', action='store_true', help='include web page glyphs and their Unicode indices')
    arguments = parser.parse_args()
    build(arguments.output, arguments.mono, arguments.browser)
