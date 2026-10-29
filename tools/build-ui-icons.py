#!/usr/bin/env python3
"""Rasterize vendored Lucide SVGs into 8-bit alpha masks for the freestanding UI library (standard library only)."""
from pathlib import Path
import argparse
import math
import re
import xml.etree.ElementTree as ET
ROOT = Path(__file__).resolve().parents[1]
SIZES = (16, 24, 32, 48)
SCALE = 8                      # supersampling factor per axis (64 coverage levels)
NUMBER = re.compile(r'[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?')
SHAPES = {'path', 'circle', 'rect', 'line', 'polyline', 'polygon', 'ellipse', 'g'}

def _tokens(d):
    if re.search(r'[^MmLlHhVvCcSsQqTtAaZz0-9eE.,+\-\s]', d):
        raise ValueError('unsupported path data')
    return [m.group(0) for m in re.finditer(r'[MmLlHhVvCcSsQqTtAaZz]|' + NUMBER.pattern, d)]

def _arc(x0, y0, rx, ry, phi, large, sweep, x, y):
    """Endpoint-to-center arc flattening (SVG 1.1 implementation notes F.6)."""
    if rx == 0 or ry == 0: return [(x, y)]
    rx, ry, p = abs(rx), abs(ry), math.radians(phi)
    c, s = math.cos(p), math.sin(p)
    dx, dy = (x0 - x) / 2, (y0 - y) / 2
    x1, y1 = c*dx + s*dy, -s*dx + c*dy
    lam = x1*x1/(rx*rx) + y1*y1/(ry*ry)
    if lam > 1: rx, ry = rx*math.sqrt(lam), ry*math.sqrt(lam)
    den = rx*rx*y1*y1 + ry*ry*x1*x1
    k = math.sqrt(max(0, (rx*rx*ry*ry - den) / den)) if den else 0
    if large == sweep: k = -k
    cx1, cy1 = k*rx*y1/ry, -k*ry*x1/rx
    cx, cy = c*cx1 - s*cy1 + (x0 + x)/2, s*cx1 + c*cy1 + (y0 + y)/2
    def angle(ux, uy, vx, vy):
        return math.atan2(ux*vy - uy*vx, ux*vx + uy*vy)
    t1 = angle(1, 0, (x1 - cx1)/rx, (y1 - cy1)/ry)
    dt = angle((x1 - cx1)/rx, (y1 - cy1)/ry, (-x1 - cx1)/rx, (-y1 - cy1)/ry)
    if not sweep and dt > 0: dt -= 2*math.pi
    if sweep and dt < 0: dt += 2*math.pi
    n = max(4, int(abs(dt) / (math.pi/16)) + 1)
    out = []
    for i in range(1, n + 1):
        t = t1 + dt*i/n
        out.append((cx + rx*math.cos(t)*c - ry*math.sin(t)*s, cy + rx*math.cos(t)*s + ry*math.sin(t)*c))
    return out

def _path(d):
    """Return (points, closed) polylines in viewBox units."""
    toks = _tokens(d); i = 0; cmd = None
    subpaths, pts = [], []
    x = y = sx = sy = 0.0; last_ctrl = None
    def num():
        nonlocal i
        if i >= len(toks) or toks[i].isalpha(): raise ValueError('truncated path data')
        i += 1; return float(toks[i-1])
    def flag():
        # Arc flags are single characters and may run into the next number ("005.5").
        nonlocal i
        if i >= len(toks) or toks[i][:1] not in ('0', '1'): raise ValueError('invalid arc flag')
        value, rest = toks[i][0] == '1', toks[i][1:]
        if rest: toks[i] = rest
        else: i += 1
        return value
    def curve(p0, p1, p2, p3):
        return [((1-t)**3*p0[0]+3*(1-t)**2*t*p1[0]+3*(1-t)*t*t*p2[0]+t**3*p3[0],
                 (1-t)**3*p0[1]+3*(1-t)**2*t*p1[1]+3*(1-t)*t*t*p2[1]+t**3*p3[1])
                for t in (k/16 for k in range(1, 17))]
    while i < len(toks):
        if toks[i].isalpha(): cmd = toks[i]; i += 1
        elif cmd is None: raise ValueError('path data without command')
        rel = cmd.islower(); C = cmd.upper()
        ox, oy = (x, y) if rel else (0.0, 0.0)
        if C == 'M':
            if len(pts) > 1: subpaths.append((pts, False))
            x, y = ox + num(), oy + num(); sx, sy = x, y; pts = [(x, y)]
            cmd = 'l' if rel else 'L'; last_ctrl = None
        elif C == 'L': x, y = ox + num(), oy + num(); pts.append((x, y)); last_ctrl = None
        elif C == 'H': x = ox + num(); pts.append((x, y)); last_ctrl = None
        elif C == 'V': y = oy + num(); pts.append((x, y)); last_ctrl = None
        elif C in 'CS':
            if C == 'C': c1 = (ox + num(), oy + num())
            else: c1 = (2*x - last_ctrl[0], 2*y - last_ctrl[1]) if last_ctrl else (x, y)
            c2 = (ox + num(), oy + num()); end = (ox + num(), oy + num())
            pts += curve((x, y), c1, c2, end); x, y = end; last_ctrl = c2
        elif C in 'QT':
            if C == 'Q': q = (ox + num(), oy + num())
            else: q = (2*x - last_ctrl[0], 2*y - last_ctrl[1]) if last_ctrl else (x, y)
            end = (ox + num(), oy + num())
            c1 = (x + 2/3*(q[0]-x), y + 2/3*(q[1]-y)); c2 = (end[0] + 2/3*(q[0]-end[0]), end[1] + 2/3*(q[1]-end[1]))
            pts += curve((x, y), c1, c2, end); x, y = end; last_ctrl = q
        elif C == 'A':
            rx, ry, phi = num(), num(), num(); large, sweep = flag(), flag()
            end = (ox + num(), oy + num())
            pts += _arc(x, y, rx, ry, phi, bool(large), bool(sweep), *end); x, y = end; last_ctrl = None
        elif C == 'Z':
            if pts: subpaths.append((pts, True))
            x, y = sx, sy; pts = [(x, y)]; last_ctrl = None
        else: raise ValueError('unsupported path command ' + cmd)
    if len(pts) > 1: subpaths.append((pts, False))
    return subpaths

def _ellipse(cx, cy, rx, ry):
    return [(cx + rx*math.cos(2*math.pi*k/64), cy + ry*math.sin(2*math.pi*k/64)) for k in range(65)]

def _shapes(element):
    tag = element.tag.split('}')[-1]
    a = dict(element.attrib)
    f = lambda k, d=0.0: float(a.get(k, d))
    if tag not in SHAPES: raise ValueError('unsupported element ' + tag)
    if 'transform' in a: raise ValueError('transforms are not supported')
    if tag == 'path': yield from _path(a['d'])
    elif tag == 'circle': yield (_ellipse(f('cx'), f('cy'), f('r'), f('r')), True)
    elif tag == 'ellipse': yield (_ellipse(f('cx'), f('cy'), f('rx'), f('ry')), True)
    elif tag == 'line': yield ([(f('x1'), f('y1')), (f('x2'), f('y2'))], False)
    elif tag in ('polyline', 'polygon'):
        v = [float(n) for n in NUMBER.findall(a['points'])]
        yield (list(zip(v[0::2], v[1::2])), tag == 'polygon')
    elif tag == 'rect':
        x, y, w, h = f('x'), f('y'), f('width'), f('height')
        r = min(f('rx', a.get('ry', 0)), w/2, h/2)
        if r <= 0: yield ([(x, y), (x+w, y), (x+w, y+h), (x, y+h)], True)
        else:
            pts = []
            for cx, cy, start in ((x+w-r, y+r, -90), (x+w-r, y+h-r, 0), (x+r, y+h-r, 90), (x+r, y+r, 180)):
                pts += [(cx + r*math.cos(math.radians(start + 90*k/8)), cy + r*math.sin(math.radians(start + 90*k/8))) for k in range(9)]
            yield (pts, True)
    for child in element: yield from _shapes(child)

def _stroke(samples, big, segments, radius):
    """Mark supersample centers within radius of any segment (exact round caps/joins)."""
    r2 = radius * radius
    for (ax, ay), (bx, by) in segments:
        dx, dy = bx - ax, by - ay; length2 = dx*dx + dy*dy
        x0 = max(0, int(min(ax, bx) - radius)); x1 = min(big - 1, int(max(ax, bx) + radius) + 1)
        y0 = max(0, int(min(ay, by) - radius)); y1 = min(big - 1, int(max(ay, by) + radius) + 1)
        for sy in range(y0, y1 + 1):
            py = sy + 0.5; row = sy * big
            for sx in range(x0, x1 + 1):
                if samples[row + sx]: continue
                px = sx + 0.5
                t = ((px - ax)*dx + (py - ay)*dy) / length2 if length2 else 0.0
                t = 0.0 if t < 0 else 1.0 if t > 1 else t
                ex, ey = px - (ax + t*dx), py - (ay + t*dy)
                if ex*ex + ey*ey <= r2: samples[row + sx] = 1

def rasterize(svg, size):
    if '<!' in svg: raise ValueError('DTDs, entities and comments are refused')
    root = ET.fromstring(svg)
    if root.attrib.get('viewBox', '').split() != ['0', '0', '24', '24']: raise ValueError('expected 24x24 viewBox')
    big = size * SCALE; k = big / 24; radius = float(root.attrib.get('stroke-width', 2)) * k / 2
    segments = []
    for child in root:
        for pts, closed in _shapes(child):
            p = [(px*k, py*k) for px, py in pts] + ([(pts[0][0]*k, pts[0][1]*k)] if closed else [])
            segments += list(zip(p, p[1:])) if len(p) > 1 else [(p[0], p[0])]
    samples = bytearray(big * big)
    _stroke(samples, big, segments, radius)
    out = bytearray(size * size)
    for y in range(size):
        for x in range(size):
            hits = 0
            for sy in range(y*SCALE, (y + 1)*SCALE):
                row = sy * big + x*SCALE; hits += sum(samples[row:row + SCALE])
            out[y*size + x] = (hits * 255 + SCALE*SCALE // 2) // (SCALE*SCALE)
    return bytes(out)

def build(output):
    lines = ['/* Generated Lucide 8-bit alpha icon masks; see userland/ui/icons/LICENSE (ISC). */', '#include "ui.h"']
    entries = []
    for svg in sorted((ROOT/'userland/ui/icons').glob('*.svg')):
        for size in SIZES:
            name = 'icon_' + svg.stem.replace('-', '_') + '_' + str(size)
            data = rasterize(svg.read_text(), size)
            lines.append(f'static const unsigned char {name}[{len(data)}]={{' + ','.join(map(str, data)) + '};')
            entries.append(f'{{"{svg.stem}",{size},{name}}}')
    lines.append('const struct zui_icon_mask zui_icon_masks[]={' + ','.join(entries) + '};')
    lines.append(f'const int zui_icon_mask_count={len(entries)};')
    Path(output).write_text('\n'.join(lines) + '\n')

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__); parser.add_argument('output', type=Path)
    build(parser.parse_args().output)
