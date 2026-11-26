#!/usr/bin/env python3
"""Writes docs/design/browser-concept.svg: the proposed Zenith browser UI around the NetSurf engine.

A proposal, not running UI. It replaces the first port's text-button toolbar and status strip with
Zenith chrome: icon buttons, an address field that shows the site and its security, a hover pill instead of a
permanent status line, and honest states for what the engine and kernel do not do yet (scripts, the connection
budget, downloads). Sample pages are fictional (example.* addresses), never a real site."""
from pathlib import Path
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[2]
W, H = 1440, 2820

DARK = dict(desk='#151619', surface='#202125', raised='#2A2C31', subtle='#25262B', border='#45474F', text='#F1F2F4',
            second='#B4B7C0', quiet='#8A8E98', accent='#8BAAFF', on_accent='#151619', tint='#2B3350', ok='#8BCBA4',
            warn='#E7BB70', danger='#F39D9D', page='#FBFAF7', ink='#23242A', ink2='#5C606B', link='#2F5BC4')
LIGHT = dict(desk='#E8E9EC', surface='#FAFAFB', raised='#FFFFFF', subtle='#EFF0F3', border='#CFD1D8', text='#202126',
             second='#5C606B', quiet='#7A7E88', accent='#3157B7', on_accent='#FFFFFF', tint='#DDE5F8', ok='#277347',
             warn='#886010', danger='#B23A3A', page='#FFFFFF', ink='#23242A', ink2='#5C606B', link='#2F5BC4')

# Outline icons on a 24 grid (one family, 2 px stroke), drawn at 16 or 20.
ICONS = {
    'back': '<path d="m12 19-7-7 7-7"/><path d="M19 12H5"/>',
    'forward': '<path d="M5 12h14"/><path d="m12 5 7 7-7 7"/>',
    'reload': '<path d="M21 12a9 9 0 1 1-9-9c2.5 0 4.8 1 6.5 2.7L21 8"/><path d="M21 3v5h-5"/>',
    'stop': '<path d="M18 6 6 18"/><path d="m6 6 12 12"/>',
    'lock': '<rect x="4" y="11" width="16" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/>',
    'unlock': '<rect x="4" y="11" width="16" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 7.5-2"/>',
    'file': '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/>',
    'star': '<path d="m12 3 2.8 5.7 6.2.9-4.5 4.4 1 6.2L12 17.3 6.5 20.2l1-6.2L3 9.6l6.2-.9z"/>',
    'more': '<circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/>',
    'globe': '<circle cx="12" cy="12" r="10"/><path d="M2 12h20"/><path d="M12 2a15 15 0 0 1 0 20 15 15 0 0 1 0-20"/>',
    'search': '<circle cx="11" cy="11" r="8"/><path d="m21 21-4.3-4.3"/>',
    'clock': '<circle cx="12" cy="12" r="10"/><path d="M12 6v6l4 2"/>',
    'alert': '<path d="m21.7 18-8-14a2 2 0 0 0-3.4 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.7-3"/><path d="M12 9v4"/><path d="M12 17h.01"/>',
    'shield': '<path d="M20 13c0 5-3.5 7.5-7.7 9a1 1 0 0 1-.6 0C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.2-2.7a1.2 1.2 0 0 1 1.6 0C14.5 3.8 17 5 19 5a1 1 0 0 1 1 1z"/>',
    'code': '<path d="m16 18 6-6-6-6"/><path d="m8 6-6 6 6 6"/>',
    'plus': '<path d="M5 12h14"/><path d="M12 5v14"/>',
    'minus': '<path d="M5 12h14"/>',
    'x': '<path d="M18 6 6 18"/><path d="m6 6 12 12"/>',
    'up': '<path d="m18 15-6-6-6 6"/>',
    'down': '<path d="m6 9 6 6 6-6"/>',
    'image': '<rect x="3" y="3" width="18" height="18" rx="2"/><circle cx="9" cy="9" r="2"/><path d="m21 15-3.1-3.1a2 2 0 0 0-2.8 0L6 21"/>',
    'download': '<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="m7 10 5 5 5-5"/><path d="M12 15V3"/>',
    'window': '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 9h18"/>',
    'copy': '<rect x="8" y="8" width="14" height="14" rx="2"/><path d="M4 16a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2"/>',
    'home': '<path d="M3 10a2 2 0 0 1 .7-1.5l7-6a2 2 0 0 1 2.6 0l7 6A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/><path d="M15 21v-8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v8"/>',
    'book': '<path d="M4 19.5v-15A2.5 2.5 0 0 1 6.5 2H20v20H6.5a2.5 2.5 0 0 1 0-5H20"/>',
    'check': '<path d="M20 6 9 17l-5-5"/>',
    'min': '<path d="M6 12h12"/>', 'max': '<path d="M6 6h12v12H6z"/>',
}

out = []


def add(s): out.append(s)


def rect(x, y, w, h, fill, rx=0, stroke=None, opacity=None, dash=None):
    s = f' stroke="{stroke}"' if stroke else ''
    o = f' opacity="{opacity}"' if opacity is not None else ''
    d = f' stroke-dasharray="{dash}"' if dash else ''
    add(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{rx}" fill="{fill}"{s}{o}{d}/>')


def text(x, y, s, fill, size=14, weight=400, anchor='start', mono=False, italic=False):
    fam = 'Hack, monospace' if mono else 'Inter, sans-serif'
    it = ' font-style="italic"' if italic else ''
    add(f'<text x="{x}" y="{y}" fill="{fill}" font-family="{fam}" font-size="{size}" font-weight="{weight}" '
        f'text-anchor="{anchor}"{it} xml:space="preserve">{escape(s)}</text>')


def runs(x, y, parts, size=14):
    """Text in several colours on one line: [(text, fill, weight)]."""
    spans = ''.join(f'<tspan fill="{f}" font-weight="{w}">{escape(t)}</tspan>' for t, f, w in parts)
    add(f'<text x="{x}" y="{y}" font-family="Inter, sans-serif" font-size="{size}" xml:space="preserve">{spans}</text>')


def icon(name, x, y, color, size=20):
    add(f'<g transform="translate({x} {y}) scale({size / 24})" fill="none" stroke="{color}" stroke-width="2" '
        f'stroke-linecap="round" stroke-linejoin="round">{ICONS[name]}</g>')


def line(x1, y1, x2, y2, color, width=1):
    add(f'<path d="M{x1} {y1}H{x2}V{y2}" stroke="{color}" stroke-width="{width}" fill="none"/>' if y1 == y2 else
        f'<line x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}" stroke="{color}" stroke-width="{width}"/>')


def shadow(x, y, w, h, rx=12):
    for i, o in ((10, .10), (5, .14), (2, .18)):
        rect(x - i / 2, y + i / 2, w + i, h + i / 2, '#000', rx + i / 2, opacity=o)


def note(x, y, lines, color, size=13, gap=19):
    for i, s in enumerate(lines):
        text(x, y + i * gap, s, color, size)


def callout(n, x, y, p):
    add(f'<circle cx="{x}" cy="{y}" r="11" fill="{p["accent"]}"/>')
    text(x, y + 4.5, str(n), p['on_accent'], 12, 700, 'middle')


# --- The pieces ----------------------------------------------------------------------------------------------------

def window(x, y, w, h, p, title, material=False):
    """A desktop window: the desktop draws the title row; the toolbar continues it (one band, no seam)."""
    shadow(x, y, w, h)
    rect(x, y, w, h, p['surface'], 12, p['border'])
    band = p['subtle']
    if material:   # a glimpse of the wallpaper through the blurred chrome
        add(f'<defs><linearGradient id="mat{x}{y}" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="{p["accent"]}" stop-opacity=".22"/>'
            f'<stop offset=".6" stop-color="{band}" stop-opacity="0"/><stop offset="1" stop-color="#3FB6B2" stop-opacity=".16"/></linearGradient></defs>')
    add(f'<path d="M{x + 1} {y + 13}a12 12 0 0 1 12-12H{x + w - 13}a12 12 0 0 1 12 12V{y + 92}H{x + 1}z" fill="{band}"/>')
    if material:
        add(f'<path d="M{x + 1} {y + 13}a12 12 0 0 1 12-12H{x + w - 13}a12 12 0 0 1 12 12V{y + 92}H{x + 1}z" fill="url(#mat{x}{y})"/>')
    icon('globe', x + 14, y + 11, p['accent'], 16)
    text(x + 38, y + 24, title, p['text'], 13, 600)
    for i, n in enumerate(('min', 'max', 'x')):
        icon(n, x + w - 100 + i * 32, y + 10, p['second'], 16)
    line(x + 1, y + 92, x + w - 1, y + 92, p['border'])


def toolbar(x, y, w, p, host, path, secure=True, loading=False, back=True, fwd=False, star=False, focus=False):
    """y is the window top; the toolbar row sits under the title at y+38."""
    ty = y + 42
    for i, (n, on) in enumerate((('back', back), ('forward', fwd), ('stop' if loading else 'reload', True))):
        bx = x + 10 + i * 40
        if i == 0 and back:
            rect(bx, ty, 36, 36, p['raised'], 8)
        icon(n, bx + 8, ty + 8, p['text'] if on else p['quiet'], 20)
    fx, fw = x + 136, w - 136 - 56
    rect(fx, ty, fw, 36, p['raised'], 8, p['accent'] if focus else p['border'])
    if focus:
        rect(fx - 3, ty - 3, fw + 6, 42, 'none', 10, p['accent'], opacity=.35)
    if host is not None:
        icon('lock' if secure is True else 'file' if secure == 'file' else 'unlock', fx + 12, ty + 10,
             p['ok'] if secure is True else p['second'] if secure == 'file' else p['warn'], 16)
        runs(fx + 36, ty + 23, [(host, p['text'], 600), (path, p['quiet'], 400)])
    if not focus:
        icon('star', fx + fw - 30, ty + 9, p['accent'] if star else p['second'], 18)
        if star:
            add(f'<g transform="translate({fx + fw - 30} {ty + 9}) scale(.75)" fill="{p["accent"]}" stroke="none">'
                f'<path d="m12 3 2.8 5.7 6.2.9-4.5 4.4 1 6.2L12 17.3 6.5 20.2l1-6.2L3 9.6l6.2-.9z"/></g>')
    icon('more', x + w - 44, ty + 8, p['text'], 20)
    if loading:   # a thin progress line along the toolbar's lower edge
        rect(x + 1, y + 90, int((w - 2) * .42), 3, p['accent'], 1)
    return fx, ty, fw


def article(x, y, w, h, p, scale=1.0):
    """A fictional sample page in the site's own (light) style: the engine draws it, the chrome stays Zenith's."""
    rect(x, y, w, h, p['page'])
    add(f'<clipPath id="pg{x}{y}"><rect x="{x}" y="{y}" width="{w}" height="{h}"/></clipPath><g clip-path="url(#pg{x}{y})">')
    rect(x, y, w, 46, '#1F3B36')
    text(x + 32, y + 29, 'Field Notes', '#F4F1E8', 17, 700)
    for i, s in enumerate(('Sky', 'Weather', 'Tides', 'About')):
        text(x + 170 + i * 82, y + 29, s, '#CFE3DC', 13)
    cx = x + 48
    cw = min(w - 96, 760)
    text(cx, y + 96, 'Sky · November', '#3C7A6C', 12, 600)
    text(cx, y + 132, 'Watching a comet from a dark field', p['ink'], 30, 700)
    text(cx, y + 160, 'By the Field Notes editors · 6 min read', p['ink2'], 13)
    para = ['A comet is easiest to find in the hour after dusk, low in the west, a faint smudge with a',
            'tail that points away from the Sun. Binoculars help more than a telescope: the wide field',
            'shows the whole tail at once. Give your eyes twenty minutes to adapt before you look.']
    for i, s in enumerate(para):
        text(cx, y + 200 + i * 24, s, p['ink'], 15)
    # A figure the engine decoded (GIF/BMP support) next to a link list.
    fy = y + 290
    rect(cx, fy, 360, 170, '#0F1A2E', 6)
    add(f'<path d="M{cx + 230} {fy + 60} l-150 70" stroke="#C9D3FF" stroke-width="10" stroke-linecap="round" opacity=".18"/>')
    add(f'<path d="M{cx + 230} {fy + 60} l-120 56" stroke="#E8EEFF" stroke-width="3" stroke-linecap="round" opacity=".7"/>')
    add(f'<circle cx="{cx + 232}" cy="{fy + 59}" r="6" fill="#FFFFFF"/>')
    for sx, sy in ((40, 30), (300, 140), (90, 150), (330, 36), (180, 24), (60, 100), (270, 110)):
        add(f'<circle cx="{cx + sx}" cy="{fy + sy}" r="1.3" fill="#FFFFFF" opacity=".8"/>')
    text(cx, fy + 190, 'Figure 1. The tail points away from the Sun.', p['ink2'], 12, italic=True)
    lx = cx + 392
    text(lx, fy + 16, 'In this guide', p['ink'], 14, 700)
    for i, s in enumerate(('Finding the comet', 'What to bring', 'Photographing the tail', 'Tonight’s sky chart')):
        text(lx, fy + 44 + i * 26, s, p['link'], 14)
        if i == 2:
            add(f'<line x1="{lx}" y1="{fy + 47 + i * 26}" x2="{lx + 154}" y2="{fy + 47 + i * 26}" stroke="{p["link"]}"/>')
    text(lx, fy + 160, 'Download the printable chart (PDF)', p['link'], 14)
    for i, s in enumerate(('Most comets fade within weeks. Check the sky chart for its position each',
                           'night: it drifts a little against the stars.')):
        text(cx, fy + 228 + i * 24, s, p['ink'], 15)
    add('</g>')
    # The page's scrollbar: a thin overlay that thickens on hover, as in the other apps.
    rect(x + w - 8, y + 12, 4, int(h * .32), '#000000', 2, opacity=.28)


def hover_pill(x, y, s, p):
    wdt = 14 + len(s) * 7
    shadow(x, y, wdt, 26, 8)
    rect(x, y, wdt, 26, p['raised'], 8, p['border'])
    text(x + 10, y + 17, s, p['second'], 12)


def menu(x, y, w, items, p):
    hgt = sum(10 if it is None else 34 for it in items) + 12
    shadow(x, y, w, hgt, 10)
    rect(x, y, w, hgt, p['raised'], 10, p['border'])
    cy = y + 6
    for it in items:
        if it is None:
            line(x + 10, cy + 5, x + w - 10, cy + 5, p['border']); cy += 10; continue
        ic, label, key = it[:3]
        hot = len(it) > 3 and it[3]
        if hot:
            rect(x + 6, cy, w - 12, 34, p['tint'], 6)
        if ic == 'zoom':
            text(x + 16, cy + 22, label, p['text'], 13)
            for j, (n, lbl) in enumerate((('minus', None), (None, '100 %'), ('plus', None))):
                bx = x + w - 150 + j * 46
                rect(bx, cy + 3, 42, 28, p['subtle'], 6)
                if n: icon(n, bx + 13, cy + 9, p['text'], 16)
                else: text(bx + 21, cy + 22, lbl, p['text'], 12, 600, 'middle')
        else:
            if ic: icon(ic, x + 16, cy + 9, p['second'], 16)
            text(x + 42, cy + 22, label, p['text'], 13)
            if key: text(x + w - 16, cy + 22, key, p['quiet'], 12, anchor='end')
        cy += 34
    return hgt


def panel(x, y, w, h, p, heading, caption=None):
    rect(x, y, w, h, p['surface'], 12, p['border'])
    text(x + 20, y + 32, heading, p['text'], 15, 600)
    if caption:
        text(x + 20, y + 52, caption, p['second'], 12)


def heading(y, s, sub=None):
    text(48, y, s, DARK['text'], 16, 600)
    if sub:
        text(48, y + 22, sub, DARK['second'], 13)


# --- The sheet -----------------------------------------------------------------------------------------------------

def build():
    d, l = DARK, LIGHT
    add(f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}">')
    rect(0, 0, W, H, d['desk'])
    text(48, 55, 'Zenith / Nebula concept', d['text'], 28, 600)
    text(48, 86, 'Proposal, not running UI · Nebula, the Zenith browser: Zenith chrome around the NetSurf 3.11 engine · accepted neutral surfaces, '
                 '8/12 radii, Inter and Hack · sample pages are fictional', d['second'], 14)

    # 1. Reading a page.
    heading(130, 'Dark · Reading a page',
            'One band of chrome under the title, the page below it untouched. The site styles its page; Zenith styles everything around it.')
    wx, wy, ww, wh = 48, 192, 1344, 760
    window(wx, wy, ww, wh, d, 'Watching a comet from a dark field — Field Notes', material=True)
    toolbar(wx, wy, ww, d, 'fieldnotes.example', '/sky/comet-watching', back=True, star=True)
    article(wx + 1, wy + 93, ww - 2, wh - 94, d)
    hover_pill(wx + 14, wy + wh - 40, 'https://fieldnotes.example/sky/comet-watching#photographing', d)
    for n, (cx, cy) in enumerate(((wx + 70, wy), (wx + 300, wy), (wx + ww - 78, wy),
                                  (wx + ww - 34, wy), (wx + 6, wy + wh - 27)), 1):
        callout(n, cx, cy, d)
    ny = 984
    cols = [
        ('1  Icon buttons', ['Back, Forward and Reload as 36 px icon', 'buttons; Reload turns into Stop while', 'loading. Back and Forward hold a', 'history list on secondary click.']),
        ('2  The address shows the site', ['Host in full text, the path quieter.', 'The lock is the security summary and', 'opens Page info. Focus (Ctrl+L) shows', 'the whole address, selected.']),
        ('3  Bookmark star', ['Filled when the page is bookmarked.', 'Ctrl+D adds one; a click on a filled', 'star offers Rename and Remove.', '']),
        ('4  One menu', ['New window, zoom, find, history,', 'bookmarks, page source and About.', 'No hidden second toolbar.', '']),
        ('5  Hover pill, no status bar', ['A link’s address appears only while', 'the pointer is on it; loading progress', 'is a 3 px line under the toolbar.', 'The page gains 24 px.']),
    ]
    for i, (h1, body) in enumerate(cols):
        x = 48 + i * 272
        text(x, ny, h1, d['text'], 14, 600)
        note(x, ny + 24, body, d['second'])

    # 2. Address field, page info, menu.
    y2 = 1104
    heading(y2, 'Dark · The address field, Page info and the menu')
    py = y2 + 30
    # 2a Address focused with suggestions.
    panel(48, py, 520, 430, d, 'Typing an address', 'Suggestions from bookmarks and history; Enter goes, Esc restores.')
    fx, fy, fw = 68, py + 76, 480
    rect(fx, fy, fw, 36, d['raised'], 8, d['accent']); rect(fx - 3, fy - 3, fw + 6, 42, 'none', 10, d['accent'], opacity=.35)
    icon('search', fx + 12, fy + 10, d['second'], 16)
    text(fx + 36, fy + 23, 'comet', d['text'], 14); rect(fx + 76, fy + 9, 1.5, 18, d['text'])
    sy = fy + 44
    shadow(fx, sy, fw, 272, 10); rect(fx, sy, fw, 272, d['raised'], 10, d['border'])
    sug = [('star', 'Watching a comet from a dark field', 'fieldnotes.example/sky/comet-watching', True),
           ('clock', 'Comet tails explained', 'skyguide.example/basics/tails', False),
           ('clock', 'Tonight’s sky chart', 'fieldnotes.example/sky/chart', False),
           ('globe', 'Go to comet', 'https://comet', False),
           ('file', 'Open a local file…', 'HTML from your Zenith disk', False)]
    for i, (ic, t1, t2, hot) in enumerate(sug):
        ry = sy + 6 + i * 52
        if hot: rect(fx + 6, ry, fw - 12, 48, d['tint'], 6)
        icon(ic, fx + 18, ry + 15, d['accent'] if hot else d['second'], 18)
        text(fx + 48, ry + 21, t1, d['text'], 13, 600 if hot else 400)
        text(fx + 48, ry + 39, t2, d['quiet'], 12)
    text(68, py + 412, 'Without a scheme an address becomes https://. No search engine by default.', d['quiet'], 12)

    # 2b Page info popover.
    px = 592
    panel(px, py, 380, 430, d, 'Page info', 'Opened from the lock. Plain words, no jargon first.')
    cx, cy = px + 20, py + 76
    rect(cx, cy, 340, 330, d['raised'], 10, d['border'])
    icon('lock', cx + 16, cy + 18, d['ok'], 20)
    text(cx + 46, cy + 28, 'Secure connection', d['text'], 14, 600)
    note(cx + 46, cy + 48, ['fieldnotes.example', 'Certificate checked by Zenith'], d['second'], 12, 17)
    line(cx + 12, cy + 96, cx + 328, cy + 96, d['border'])
    rows = [('code', 'Scripts', 'Not supported', d['second']), ('image', 'Images', '4 of 6 loaded', d['warn']),
            ('globe', 'Connections', '6 of 6 used', d['warn']), ('shield', 'Cookies', 'Kept until you close', d['second'])]
    for i, (ic, k, v, c) in enumerate(rows):
        ry = cy + 112 + i * 40
        icon(ic, cx + 16, ry + 2, d['second'], 16)
        text(cx + 46, ry + 15, k, d['text'], 13)
        text(cx + 324, ry + 15, v, c, 13, 600 if c != d['second'] else 400, 'end')
    rect(cx + 16, cy + 278, 148, 36, d['subtle'], 8, d['border']); text(cx + 90, cy + 301, 'Certificate…', d['text'], 13, 600, 'middle')
    rect(cx + 176, cy + 278, 148, 36, d['accent'], 8); text(cx + 250, cy + 301, 'Load again', d['on_accent'], 13, 600, 'middle')

    # 2c Menu.
    mx = 996
    panel(mx, py, 396, 430, d, 'The menu', 'Everything else lives here, each with its key.')
    menu(mx + 20, py + 70, 356, [('window', 'New window', 'Ctrl+N'), ('zoom', 'Zoom', ''), None,
                                  ('search', 'Find on page', 'Ctrl+F'), ('clock', 'History', 'Ctrl+H', True),
                                  ('book', 'Bookmarks', 'Ctrl+B'), None, ('code', 'Page source', 'Ctrl+U'),
                                  ('globe', 'About Nebula', ''), ], d)

    # 3. Light: the start page in a new window.
    y3 = 1600
    heading(y3, 'Light · A new window', 'A start page drawn by the engine from a packaged HTML file, styled with the Zenith theme passed in as CSS.')
    sx, sy, sw, sh = 48, y3 + 46, 1344, 520
    window(sx, sy, sw, sh, l, 'New window')
    toolbar(sx, sy, sw, l, None, '', back=False, focus=True)
    text(sx + 172, sy + 65, 'Search bookmarks and history, or enter an address', l['quiet'], 14)
    rect(sx + 1, sy + 93, sw - 2, sh - 94, l['surface'])
    cxm = sx + sw // 2
    text(cxm, sy + 168, 'Good evening', l['text'], 24, 600, 'middle')
    text(cxm, sy + 194, 'Pick up where you left off, or open a file from your disk.', l['second'], 14, anchor='middle')
    text(sx + 200, sy + 246, 'Bookmarks', l['text'], 15, 600)
    tiles = [('F', 'Field Notes', 'fieldnotes.example', '#1F3B36'), ('S', 'Sky guide', 'skyguide.example', '#3157B7'),
             ('Z', 'Zenith guide', 'Local file', '#7042BE'), ('T', 'Tide tables', 'tides.example', '#197571'),
             ('+', 'Add', 'Ctrl+D on any page', None)]
    for i, (ch, t1, t2, c) in enumerate(tiles):
        tx, ty = sx + 200 + i * 192, sy + 262
        rect(tx, ty, 176, 96, l['raised'], 12, l['border'], dash='4 4' if c is None else None)
        if c:
            rect(tx + 16, ty + 16, 32, 32, c, 8); text(tx + 32, ty + 38, ch, '#FFFFFF', 15, 700, 'middle')
        else:
            icon('plus', tx + 20, ty + 20, l['second'], 24)
        text(tx + 16, ty + 70, t1, l['text'], 13, 600); text(tx + 16, ty + 86, t2, l['quiet'], 12)
    text(sx + 200, sy + 400, 'Recent', l['text'], 15, 600)
    for i, (t1, t2, when) in enumerate((('Watching a comet from a dark field', 'fieldnotes.example', '19:42'),
                                         ('Comet tails explained', 'skyguide.example', '19:30'),
                                         ('guide.html', 'Using the browser', 'Yesterday'))):
        ry = sy + 414 + i * 30
        icon('clock', sx + 200, ry + 4, l['quiet'], 16)
        text(sx + 226, ry + 17, t1, l['text'], 13); text(sx + 520, ry + 17, t2, l['quiet'], 12)
        text(sx + 1144, ry + 17, when, l['quiet'], 12, anchor='end')

    # 4. Honest states.
    y4 = 2200
    heading(y4, 'Dark · Honest states', 'What the engine and kernel cannot do yet is said once, in place, with the one action that helps.')
    sy4 = y4 + 34
    cw = 324

    def strip(x, y, w, p):   # a compact toolbar for the state cards
        rect(x, y, w, 52, p['subtle'], 0)
        for i, n in enumerate(('back', 'forward', 'reload')):
            icon(n, x + 12 + i * 30, y + 16, p['second'], 18)
        rect(x + 104, y + 8, w - 116, 36, p['raised'], 8, p['border'])

    # 4a Connection budget.
    x = 48; panel(x, sy4, cw, 330, d, 'Connection limit reached')
    strip(x + 1, sy4 + 48, cw - 2, d); icon('lock', x + 117, sy4 + 66, d['ok'], 16); text(x + 140, sy4 + 79, 'fieldnotes.example', d['text'], 13, 600)
    rect(x + 16, sy4 + 116, cw - 32, 80, d['raised'], 10, d['warn'])
    icon('image', x + 30, sy4 + 132, d['warn'], 18)
    note(x + 58, sy4 + 146, ['2 images were not loaded.', 'This page used all 6 connections', 'a browser may open.'], d['text'], 12, 17)
    rect(x + 16, sy4 + 208, 140, 32, d['accent'], 8); text(x + 86, sy4 + 229, 'Load again', d['on_accent'], 13, 600, 'middle')
    note(x + 16, sy4 + 268, ['A bar under the toolbar, shown once per', 'page; the page stays readable. Goes away', 'when the kernel budget (0.0.81) grows.'], d['quiet'], 12, 17)
    # 4b Certificate error page.
    x = 48 + (cw + 16); panel(x, sy4, cw, 330, d, 'Certificate not trusted')
    strip(x + 1, sy4 + 48, cw - 2, d); icon('unlock', x + 117, sy4 + 66, d['danger'], 16); text(x + 140, sy4 + 79, 'old-mirror.example', d['text'], 13, 600)
    icon('shield', x + cw // 2 - 14, sy4 + 120, d['danger'], 28)
    text(x + cw // 2, sy4 + 172, 'This site’s identity can’t be checked', d['text'], 13, 600, 'middle')
    text(x + cw // 2, sy4 + 192, 'Its certificate expired on 3 March 2025.', d['second'], 12, anchor='middle')
    rect(x + cw // 2 - 70, sy4 + 210, 140, 32, d['accent'], 8); text(x + cw // 2, sy4 + 231, 'Go back', d['on_accent'], 13, 600, 'middle')
    text(x + cw // 2, sy4 + 262, 'Details', d['accent'], 12, 600, 'middle')
    note(x + 16, sy4 + 290, ['No "continue anyway" in the first stage.'], d['quiet'], 12, 17)
    # 4c Downloads not yet.
    x = 48 + 2 * (cw + 16); panel(x, sy4, cw, 330, d, 'A link to a file')
    strip(x + 1, sy4 + 48, cw - 2, d); icon('lock', x + 117, sy4 + 66, d['ok'], 16); text(x + 140, sy4 + 79, 'fieldnotes.example', d['text'], 13, 600)
    rect(x + 16, sy4 + 116, cw - 32, 108, d['raised'], 10, d['border'])
    icon('download', x + 30, sy4 + 132, d['second'], 18)
    text(x + 58, sy4 + 146, 'sky-chart-november.pdf', d['text'], 13, 600)
    note(x + 58, sy4 + 164, ['PDF · 1.8 MB', 'Nebula can’t show this file, and', 'Zenith can’t save downloads yet.'], d['second'], 12, 17)
    rect(x + 16, sy4 + 236, 150, 32, d['subtle'], 8, d['border']); text(x + 91, sy4 + 257, 'Copy address', d['text'], 13, 600, 'middle')
    note(x + 16, sy4 + 292, ['Becomes "Save to Downloads" with N3.'], d['quiet'], 12, 17)
    # 4d Find bar.
    x = 48 + 3 * (cw + 16); panel(x, sy4, cw, 330, d, 'Find on page')
    rect(x + 16, sy4 + 56, cw - 32, 150, d['page'], 6)
    rect(x + 26, sy4 + 72, 42, 17, '#F2C94C', 3); rect(x + 26, sy4 + 132, 42, 17, '#FBE9A9', 3)
    note(x + 28, sy4 + 84, ['Comet season: look in the hour', 'after dusk, low in the west, for a', 'faint smudge. Binoculars help.', 'Comet tails point away from the', 'Sun and fade within weeks.'], d['ink'], 12, 20)
    rect(x + 16, sy4 + 214, cw - 32, 48, d['subtle'], 8, d['border'])
    rect(x + 24, sy4 + 222, 160, 32, d['raised'], 6, d['accent']); text(x + 34, sy4 + 243, 'comet', d['text'], 13)
    text(x + 196, sy4 + 243, '1 of 7', d['second'], 12)
    icon('up', x + 240, sy4 + 229, d['text'], 18); icon('down', x + 266, sy4 + 229, d['text'], 18); icon('x', x + 288, sy4 + 229, d['second'], 16)
    note(x + 16, sy4 + 290, ['Ctrl+F; slides up over the page bottom.'], d['quiet'], 12, 17)

    # 5. Stages and decisions.
    y5 = sy4 + 380
    heading(y5, 'Stages and open decisions')
    stages = [('B1  Chrome', 'Icon toolbar, host/path address with the lock, hover pill, progress line, material band.'),
              ('B2  States', 'Error pages from packaged HTML, connection-limit bar, file-link card, Page info.'),
              ('B3  Tools', 'Menu, find bar, zoom, page source; link menu: Open, Open in new window, Copy address.'),
              ('B4  Memory', 'Bookmarks and history kept per user; start page and address suggestions use them.'),
              ('B5  Windows', 'New window opens a second browser; tabs wait for a larger kernel connection budget.'),
              ('B6  Downloads', 'Save to Downloads and a downloads list, on top of the N3 download service.')]
    for i, (h1, body) in enumerate(stages):
        text(48, y5 + 40 + i * 26, h1, d['text'], 13, 600)
        text(170, y5 + 40 + i * 26, body, d['second'], 13)
    ox = 900
    text(ox, y5 + 40, 'Open decisions', d['text'], 13, 600)
    note(ox, y5 + 66, ['\u2022 Windows only, or tabs once the budget allows',
                      '\u2022 A default search engine, or addresses only', '\u2022 Keep cookies between sessions, or per window only',
                      '\u2022 Certificate override: never, or behind Details'], d['second'], 13, 26)

    add('</svg>')


def main():
    build()
    target = ROOT / 'docs/design/browser-concept.svg'
    target.write_text('\n'.join(out) + '\n')
    print(target)


if __name__ == '__main__':
    main()
