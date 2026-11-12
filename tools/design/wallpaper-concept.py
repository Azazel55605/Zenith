#!/usr/bin/env python3
"""Writes docs/design/wallpaper-concept.svg: proposed dynamic wallpapers.

Each wallpaper is a formula over the screen, rendered by the compositor (no image files): it
follows light/dark mode and the accent color the moment they change. The previews here are made
by the same per-pixel formulas the kernel would use (numpy, float32)."""
import argparse, base64, io
from pathlib import Path
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
W, H = 576, 360


def rgb(hexcode):
    return np.array([int(hexcode[i:i + 2], 16) for i in (0, 2, 4)], np.float32)


def grid():
    y, x = np.mgrid[0:H, 0:W].astype(np.float32)
    return x / W, y / H


def mix(a, b, t):
    t = np.clip(t, 0, 1)[..., None]
    return a * (1 - t) + b * t


def glow(u, v, cx, cy, r):
    d = np.sqrt(((u - cx) * W / H) ** 2 + (v - cy) ** 2) / r
    t = np.clip(1 - d, 0, 1)
    return t * t * (3 - 2 * t)


PALETTES = {
    # base top, base bottom, accent, second glow, ink (lines/ridges)
    'dark': dict(top='1B2036', bottom='0E1018', accent='8BAAFF', second='3FB6B2', third='B6A0FF', ink='C9D3FF'),
    'light': dict(top='F8F4EC', bottom='EDE5D6', accent='3157B7', second='E9A87A', third='7042BE', ink='5D564B'),
}


def zenith(mode):
    """The current wallpaper, refined: a diagonal gradient with an accent glow and a second glow."""
    p = {k: rgb(v) for k, v in PALETTES[mode].items()}; u, v = grid()
    img = mix(p['top'], p['bottom'], (u * .45 + v * .75))
    a = .55 if mode == 'dark' else .28
    img = mix(img, p['accent'], glow(u, v, .2, .22, .95) * a)
    img = mix(img, p['second'], glow(u, v, .9, .88, .8) * (a * .85))
    return img


def dunes(mode):
    """Layered ridges, lighter toward the horizon, each with a soft rim of light: sky, then five dunes."""
    p = {k: rgb(v) for k, v in PALETTES[mode].items()}; u, v = grid()
    sky = mix(p['top'], mix(p['top'], p['accent'], .45 if mode == 'dark' else .20), np.clip(1 - v * 1.7, 0, 1))
    sky = mix(sky, p['second'], glow(u, v, .70, .40, .60) * (.50 if mode == 'dark' else .40))
    img = sky.copy()
    near = mix(p['bottom'], p['accent'], .10) if mode == 'dark' else mix(p['bottom'], p['second'], .45)
    far = mix(p['top'], p['accent'], .38) if mode == 'dark' else mix(p['bottom'], p['top'], .55)
    rim = mix(p['second'], p['top'], .2) if mode == 'dark' else p['top']
    for k in range(5):
        level = .46 + k * .115
        ridge = level + .05 * np.sin(u * (2.6 + k * .9) + k * 2.3) + .022 * np.sin(u * (6.1 + k * 1.3) + k * .7)
        depth = v - ridge
        shade = mix(far, near, k / 4.0)
        # Lit crest fading down the slope, and a darker lee side lower on each dune.
        body = mix(shade, rim, np.clip(1 - depth * 14, 0, 1) * (.42 if mode == 'dark' else .55))
        body = mix(body, mix(shade, np.zeros(3, np.float32) if mode == 'dark' else p['ink'], .5), np.clip(depth * 3, 0, 1) * .35)
        img = np.where((depth > 0)[..., None], body, img)
    return img


def aurora(mode):
    """Curtains of light: each has a bright, wavy lower hem and rays fading upward."""
    p = {k: rgb(v) for k, v in PALETTES[mode].items()}; u, v = grid()
    img = mix(p['top'], p['bottom'], v * 1.1)
    if mode == 'dark':
        img = mix(img, np.zeros(3, np.float32), np.clip(v - .55, 0, 1) * .6)
    for color, hem, freq, phase, left, right in ((p['second'], .50, 5.5, 0.3, .05, .62), (p['accent'], .42, 4.2, 2.1, .30, .92),
                                                  (p['third'], .34, 6.8, 4.0, .55, 1.05)):
        h = hem + .07 * np.sin(u * freq + phase) + .025 * np.sin(u * 17 + phase)
        above = np.clip(h - v, 0, None)
        curtain = np.where(v < h, np.exp(-above / .16), np.exp(-(v - h) / .04) * .85)
        span = np.clip((u - left) / .12, 0, 1) * np.clip((right - u) / .12, 0, 1)
        rays = .62 + .38 * np.sin(u * 140 + 2.5 * np.sin(u * 9 + phase) + v * 2) ** 2
        img = mix(img, color, curtain * span * rays * (.80 if mode == 'dark' else .42))
    return img


def contour(mode):
    """Topographic lines over a soft gradient: calm, with structure."""
    p = {k: rgb(v) for k, v in PALETTES[mode].items()}; u, v = grid()
    img = mix(p['top'], p['bottom'], v * .9 + u * .2)
    img = mix(img, p['accent'], glow(u, v, .78, .3, .9) * (.30 if mode == 'dark' else .14))
    x, y = u * W / H * 3.2, v * 3.2
    f = (np.sin(x * 1.3 + np.sin(y * 1.7)) + np.sin(y * 1.1 - x * .6 + 1.3) + .6 * np.sin(x * 2.3 + y * 2.1 + .7)) * 2.2
    frac = np.abs(f - np.round(f))
    line = np.clip(1 - frac / .045, 0, 1)
    major = (np.abs(np.round(f)) % 4 == 0)
    strength = np.where(major, .55, .28) * (1.0 if mode == 'dark' else .8)
    return mix(img, p['ink'] if mode == 'light' else mix(p['ink'], p['accent'], .5), line * strength)


DESIGNS = [('Zenith', zenith, 'Today\'s wallpaper, refined: a diagonal gradient with an accent glow. Now also light.'),
           ('Dunes', dunes, 'Layered ridges that pale toward the horizon; dusk in dark mode, warm sand in light.'),
           ('Aurora', aurora, 'Three curtains with bright, wavy hems in the accent and two companion colors.'),
           ('Contour', contour, 'Topographic lines over a soft gradient; quiet enough for busy desktops.')]
SOLIDS = ['202125', '1B2036', '243B36', '3A2433', 'F8F4EC', 'E8EEF6', 'EDE5D6', '8BAAFF']


def jpeg(img):
    buffer = io.BytesIO(); Image.fromarray(np.clip(img, 0, 255).astype(np.uint8)).save(buffer, 'JPEG', quality=86)
    return 'data:image/jpeg;base64,' + base64.b64encode(buffer.getvalue()).decode()


def text(x, y, s, size=13, weight=400, color='#B4B7C0'):
    return f'<text x="{x}" y="{y}" fill="{color}" font-family="Inter, sans-serif" font-size="{size}" font-weight="{weight}" xml:space="preserve">{s}</text>'


def sheet(previews=None):
    out = ['<rect width="1400" height="2400" fill="#17181C"/>',
           text(48, 55, 'Zenith / Wallpaper concept: dynamic wallpapers', 28, 600, '#F1F2F4'),
           text(48, 84, 'Proposal · each is a formula drawn by the compositor, no image files · follows light/dark and the accent the moment they change · '
                'previews made with the same per-pixel math', 14)]
    y = 120
    for name, render, note in DESIGNS:
        out.append(text(48, y + 20, name, 20, 600, '#F1F2F4'))
        out.append(text(150, y + 20, note, 14))
        for i, mode in enumerate(('dark', 'light')):
            img = render(mode)
            if previews: Image.fromarray(np.clip(img, 0, 255).astype(np.uint8)).save(previews / f'{name.lower()}-{mode}.png')
            x = 48 + i * 660
            out.append(f'<clipPath id="c{name}{mode}"><rect x="{x}" y="{y + 36}" width="640" height="400" rx="12"/></clipPath>')
            out.append(f'<image x="{x}" y="{y + 36}" width="640" height="400" preserveAspectRatio="none" clip-path="url(#c{name}{mode})" href="{jpeg(img)}"/>')
            # A window and the taskbar for scale.
            win = '#202125' if mode == 'dark' else '#F8F4EC'; edge = '#45474F' if mode == 'dark' else '#D9D0C0'
            out.append(f'<rect x="{x + 150}" y="{y + 110}" width="300" height="190" rx="10" fill="{win}" stroke="{edge}"/>')
            out.append(f'<rect x="{x + 150}" y="{y + 110}" width="300" height="30" rx="10" fill="{win}"/>')
            out.append(text(x + 164, y + 130, 'Polaris', 12, 600, '#F1F2F4' if mode == 'dark' else '#27231D'))
            out.append(f'<rect x="{x + 16}" y="{y + 396}" width="608" height="30" rx="10" fill="{win}" fill-opacity=".86"/>')
            out.append(text(x + 16, y + 456, mode.capitalize(), 13, 600, '#E8EAF0'))
        y += 486
    out.append(text(48, y + 20, 'Solid colors', 20, 600, '#F1F2F4'))
    out.append(text(178, y + 20, 'Plain fills for minimal desktops; any color can be typed in as hex.', 14))
    for i, c in enumerate(SOLIDS):
        out.append(f'<rect x="{48 + i * 166}" y="{y + 40}" width="150" height="90" rx="10" fill="#{c}" stroke="#45474F"/>')
        out.append(text(60 + i * 166, y + 150, '#' + c, 12, 400, '#B4B7C0'))
    y += 190
    cols = [('How it changes', ['Light/dark and accent changes redraw the wallpaper at once,',
                                'no reboot. A future day/night service (S3) can switch the mode',
                                'by time; the wallpaper follows it.']),
            ('Meridian › Style › Wallpaper', ['Dynamic (pick one of these), Solid color (swatches or hex),',
                                              'and later Image. The login screen uses the same choice.']),
            ('Cost', ['Drawn once per change at screen resolution, on the worker CPUs;',
                      'no per-frame cost. Contour and Aurora are the heaviest (~3x Zenith).'])]
    for i, (head, lines) in enumerate(cols):
        out.append(text(48 + i * 440, y, head, 17, 600, '#F1F2F4'))
        for k, line in enumerate(lines): out.append(text(48 + i * 440, y + 28 + k * 20, line, 13))
    return '<svg xmlns="http://www.w3.org/2000/svg" width="1400" height="2400" viewBox="0 0 1400 2400">' + ''.join(out) + '</svg>\n'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', nargs='?', type=Path, default=ROOT / 'docs/design/wallpaper-concept.svg')
    parser.add_argument('--previews', type=Path, help='also write each preview as PNG here')
    args = parser.parse_args()
    if args.previews: args.previews.mkdir(parents=True, exist_ok=True)
    args.output.write_text(sheet(args.previews))
    print('wrote ' + str(args.output))
