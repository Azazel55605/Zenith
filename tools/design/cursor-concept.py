#!/usr/bin/env python3
"""Writes docs/design/cursor-concept.svg: the proposed first Zenith cursor set ("Meridian").

Every shape is drawn on a 24-unit grid with its hotspot; the implementation rasterizes the same
definitions at 24, 30, 36, 42 and 48 px (one per display scale). Two variants share the shapes: Light (white body, near-black
outline) and Dark (near-black body, light outline)."""
import argparse
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ACCENT = '#8BAAFF'
DANGER = '#E5484D'

# name, label, hotspot, kind, data. kind "fill": a closed path filled with the body and outlined;
# "line": a stroked path (outline under body); "ring": busy ring with an accent arc.
SHAPES = [
    ('default', 'Arrow', (4, 3), 'fill', 'M4 3 L4 19.6 L8.3 15.7 L11.2 21.9 L14.2 20.5 L11.4 14.5 L17.3 14.5 Z'),
    ('text', 'Text', (12, 12), 'line', 'M8.6 4.5 H10.4 Q12 4.5 12 6 Q12 4.5 13.6 4.5 H15.4 M12 6 V18 M8.6 19.5 H10.4 Q12 19.5 12 18 Q12 19.5 13.6 19.5 H15.4'),
    ('pointer', 'Link', (9, 3), 'fill', 'M7.4 4.8 C7.4 3.2 10.2 3.2 10.2 4.8 L10.2 10.4 C10.2 9.3 12.6 9.3 12.6 10.6 C12.6 9.7 15 9.7 15 11.2 C15 10.3 17.2 10.4 17.2 11.9 L17.2 16.4 C17.2 19.6 15.2 21.6 12.4 21.6 L11.2 21.6 C9.2 21.6 8 20.8 7 19.4 L4.6 15.9 C3.9 14.9 5.1 13.6 6.1 14.4 L7.4 15.5 Z'),
    ('wait', 'Busy', (12, 12), 'ring', None),
    ('progress', 'Working', (4, 3), 'progress', None),
    ('not-allowed', 'Not allowed', (12, 12), 'blocked', None),
    ('move', 'Move', (12, 12), 'fill', 'M12 3 L15.4 6.8 L13.2 6.8 L13.2 10.8 L17.2 10.8 L17.2 8.6 L21 12 L17.2 15.4 L17.2 13.2 L13.2 13.2 L13.2 17.2 L15.4 17.2 L12 21 L8.6 17.2 L10.8 17.2 L10.8 13.2 L6.8 13.2 L6.8 15.4 L3 12 L6.8 8.6 L6.8 10.8 L10.8 10.8 L10.8 6.8 L8.6 6.8 Z'),
    ('ns-resize', 'Resize up/down', (12, 12), 'fill', 'M12 3 L16.2 7.8 L13.3 7.8 L13.3 16.2 L16.2 16.2 L12 21 L7.8 16.2 L10.7 16.2 L10.7 7.8 L7.8 7.8 Z'),
    ('ew-resize', 'Resize left/right', (12, 12), 'fill', 'M3 12 L7.8 7.8 L7.8 10.7 L16.2 10.7 L16.2 7.8 L21 12 L16.2 16.2 L16.2 13.3 L7.8 13.3 L7.8 16.2 Z'),
    ('nwse-resize', 'Resize diagonal', (12, 12), 'rotated', ('ns', -45)),
    ('nesw-resize', 'Resize diagonal', (12, 12), 'rotated', ('ns', 45)),
    ('crosshair', 'Precise', (12, 12), 'line', 'M12 3.5 V9 M12 15 V20.5 M3.5 12 H9 M15 12 H20.5'),
]
NS = SHAPES[7][4]
VARIANTS = {'Light': ('#FFFFFF', '#16181D'), 'Dark': ('#1E2026', '#F2F3F5')}


def glyph(name, kind, data, body, outline):
    """SVG for one cursor in a 24-unit box (no transform)."""
    shadow = '<g opacity=".28" transform="translate(.6 1.2)" filter="url(#soft)">{}</g>'
    if kind == 'fill':
        shape = f'<path d="{data}" fill="{body}" stroke="{outline}" stroke-width="1.5" stroke-linejoin="round"/>'
        return shadow.format(f'<path d="{data}" fill="#000"/>') + shape
    if kind == 'rotated':
        base, angle = data
        inner = glyph(name, 'fill', NS, body, outline)
        return f'<g transform="rotate({angle} 12 12)">{inner}</g>'
    if kind == 'line':
        return (shadow.format(f'<path d="{data}" fill="none" stroke="#000" stroke-width="4.4" stroke-linecap="round"/>')
                + f'<path d="{data}" fill="none" stroke="{outline}" stroke-width="4.4" stroke-linecap="round" stroke-linejoin="round"/>'
                + f'<path d="{data}" fill="none" stroke="{body}" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"/>'
                + (f'<circle cx="12" cy="12" r="1.6" fill="{body}" stroke="{outline}" stroke-width="1.2"/>' if name == 'crosshair' else ''))
    if kind == 'ring':
        return ring(12, 12, 7.2, body, outline, shadow)
    if kind == 'progress':
        arrow = SHAPES[0][4]
        return (glyph('default', 'fill', arrow, body, outline).replace('<path', '<path transform="scale(.86)"', 2)
                + ring(17.6, 17.4, 4.2, body, outline, shadow, small=True))
    if kind == 'blocked':
        d = 'M6.6 17.4 L17.4 6.6'
        return (shadow.format('<circle cx="12" cy="12" r="7.6" fill="none" stroke="#000" stroke-width="4.4"/>')
                + f'<circle cx="12" cy="12" r="7.6" fill="none" stroke="{outline}" stroke-width="4.6"/>'
                + f'<path d="{d}" stroke="{outline}" stroke-width="4.6" stroke-linecap="round"/>'
                + f'<circle cx="12" cy="12" r="7.6" fill="none" stroke="{DANGER}" stroke-width="2.2"/>'
                + f'<path d="{d}" stroke="{DANGER}" stroke-width="2.2" stroke-linecap="round"/>')
    raise ValueError(kind)


def ring(cx, cy, r, body, outline, shadow, small=False):
    w_out, w_in = (3.8, 1.9) if small else (4.6, 2.4)
    circumference = 2 * 3.14159 * r
    return (shadow.format(f'<circle cx="{cx}" cy="{cy}" r="{r}" fill="none" stroke="#000" stroke-width="{w_out}"/>')
            + f'<circle cx="{cx}" cy="{cy}" r="{r}" fill="none" stroke="{outline}" stroke-width="{w_out}"/>'
            + f'<circle cx="{cx}" cy="{cy}" r="{r}" fill="none" stroke="{body}" stroke-width="{w_in}"/>'
            + f'<circle cx="{cx}" cy="{cy}" r="{r}" fill="none" stroke="{ACCENT}" stroke-width="{w_in}" stroke-linecap="round" '
            + f'stroke-dasharray="{circumference * .32:.2f} {circumference:.2f}" transform="rotate(-90 {cx} {cy})"/>')


def cursor(name, x, y, size, variant):
    entry = next(s for s in SHAPES if s[0] == name)
    body, outline = VARIANTS[variant]
    scale = size / 24
    return f'<g transform="translate({x} {y}) scale({scale:.4f})">{glyph(name, entry[3], entry[4], body, outline)}</g>'


def text(x, y, s, size=13, weight=400, color='#B4B7C0', anchor='start', family='Inter, sans-serif'):
    return (f'<text x="{x}" y="{y}" fill="{color}" font-family="{family}" font-size="{size}" font-weight="{weight}" '
            f'text-anchor="{anchor}" xml:space="preserve">{s}</text>')


def sheet():
    W = 1400; out = []
    out.append(f'<rect width="{W}" height="1640" fill="#17181C"/>')
    out.append(text(48, 55, 'Zenith / Cursor concept: the Meridian set', 28, 600, '#F1F2F4'))
    out.append(text(48, 84, 'Proposal, not running UI · 12 shapes on a 24-unit grid with rounded joins and a soft shadow · Light and Dark variants · '
                    'rendered at 24, 32 and 48 px for 100–200% scaling', 14))
    y0 = 120
    for row, variant in enumerate(('Light', 'Dark')):
        top = y0 + row * 470
        tile_bg = '#5E6370' if variant == 'Light' else '#C9CCD3'
        out.append(text(48, top + 20, variant + ' · ' + ('white body, near-black outline (default)' if variant == 'Light' else 'dark body, light outline'), 17, 600, '#F1F2F4'))
        for i, (name, label, hot, kind, data) in enumerate(SHAPES):
            col, line = i % 6, i // 6
            x, y = 48 + col * 220, top + 40 + line * 210
            out.append(f'<rect x="{x}" y="{y}" width="200" height="190" rx="12" fill="{tile_bg}"/>')
            out.append(f'<rect x="{x}" y="{y}" width="200" height="190" rx="12" fill="url(#grid)" opacity=".35"/>')
            out.append(cursor(name, x + 52, y + 22, 96, variant))
            hx, hy = x + 52 + hot[0] * 4, y + 22 + hot[1] * 4
            out.append(f'<circle cx="{hx}" cy="{hy}" r="3.2" fill="{ACCENT}" stroke="#101216" stroke-width="1.2"/>')
            out.append(text(x + 14, y + 150, label, 14, 600, '#F7F8FA' if variant == 'Light' else '#1B1D22'))
            out.append(text(x + 14, y + 172, name + ' · hotspot ' + str(hot[0]) + ',' + str(hot[1]), 12, 400,
                            '#E3E5EA' if variant == 'Light' else '#3B3F48', family='Hack, monospace'))
    top = y0 + 2 * 470 + 10
    out.append(text(48, top, 'At actual size: 24 px (100%) and 48 px (200%) on the dark wallpaper, a cream light window and a dark window', 17, 600, '#F1F2F4'))
    backgrounds = [('url(#wall)', 'Wallpaper', 'Light'), ('#F8F4EC', 'Light window (cream)', 'Light'), ('#202125', 'Dark window', 'Light'),
                   ('#F8F4EC', 'Light window, Dark variant', 'Dark')]
    for i, (fill, label, variant) in enumerate(backgrounds):
        x, y = 48 + (i % 2) * 660, top + 24 + (i // 2) * 190
        out.append(f'<rect x="{x}" y="{y}" width="640" height="170" rx="12" fill="{fill}"/>')
        out.append(text(x + 16, y + 26, label, 13, 600, '#2A2621' if fill == '#F8F4EC' else '#E8EAF0'))
        for j, (name, *_rest) in enumerate(SHAPES):
            out.append(cursor(name, x + 18 + j * 50, y + 44, 24, variant))
        for j, name in enumerate(('default', 'text', 'pointer', 'wait', 'move', 'nwse-resize')):
            out.append(cursor(name, x + 18 + j * 100, y + 84, 48, variant))
    top += 24 + 2 * 190 + 30
    cols = [('What changes', ['Today there is one 12 × 19 px arrow with hard pixel edges.',
                              'The set adds text, link, busy, working, blocked, move,',
                              'four resize directions and a precise crosshair.']),
            ('Sizes and HiDPI', ['Drawn as vectors, rasterized at 24, 32 and 48 px with',
                                 'antialiasing, so 125–200% scaling stays sharp. The',
                                 'virtio-gpu hardware cursor gets the same 64 px image.']),
            ('Where they appear', ['Text fields, links, window edges and corners, busy apps',
                                   '(an accent arc turns) and refused drops. Meridian picks',
                                   'Light or Dark and the size under Style › Cursor.'])]
    for i, (head, lines) in enumerate(cols):
        x = 48 + i * 440
        out.append(text(x, top, head, 17, 600, '#F1F2F4'))
        for k, line in enumerate(lines):
            out.append(text(x, top + 28 + k * 20, line, 13))
    defs = ('<defs><filter id="soft" x="-20%" y="-20%" width="140%" height="140%"><feGaussianBlur stdDeviation=".7"/></filter>'
            '<pattern id="grid" width="16" height="16" patternUnits="userSpaceOnUse"><path d="M16 0H0V16" fill="none" stroke="#000" stroke-opacity=".25"/></pattern>'
            '<linearGradient id="wall" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#2A3358"/><stop offset=".55" stop-color="#151A2C"/>'
            '<stop offset="1" stop-color="#103B45"/></linearGradient></defs>')
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="1640" viewBox="0 0 {W} 1640">' + defs + ''.join(out) + '</svg>\n')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', nargs='?', type=Path, default=ROOT / 'docs/design/cursor-concept.svg')
    args = parser.parse_args()
    args.output.write_text(sheet())
    print('wrote ' + str(args.output))
