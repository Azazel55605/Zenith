#!/usr/bin/env python3
"""Writes docs/design/browser-tabs-concept.svg: tabs for Nebula in the layout every Zenith app uses.

The tabs are the sidebar's navigation rows, and the browser sits in the rounded content inset beside them, as in
Orbit, Beacon and Meridian (zui_workspace_begin: a 12 px margin, the inset 24 px right of the sidebar, radius 12).
The toolbar from the first concept moves inside the inset; the page fills the rest of it. It reuses the drawing
helpers of tools/design/browser-concept.py. A proposal, not running UI. The sample pages are fictional."""
import importlib.util
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('browser_concept', ROOT / 'tools/design/browser-concept.py')
bc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bc)
rect, text, runs, icon, line, shadow, note, callout = bc.rect, bc.text, bc.runs, bc.icon, bc.line, bc.shadow, bc.note, bc.callout
DARK, LIGHT, add = bc.DARK, bc.LIGHT, bc.add
bc.ICONS.update({
    'panel': '<rect x="3" y="3" width="18" height="18" rx="2"/><path d="M9 3v18"/>',
    'moon': '<path d="M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9Z"/>',
    'undo': '<path d="M9 14 4 9l5-5"/><path d="M4 9h10.5a5.5 5.5 0 0 1 0 11H11"/>',
    'grip': '<circle cx="9" cy="6" r="1"/><circle cx="9" cy="12" r="1"/><circle cx="9" cy="18" r="1"/><circle cx="15" cy="6" r="1"/><circle cx="15" cy="12" r="1"/><circle cx="15" cy="18" r="1"/>',
})
W, H = 1440, 2520
SITES = [  # letter, colour, title, host
    ('F', '#1F3B36', 'Watching a comet from a dark field', 'fieldnotes.example'),
    ('S', '#3157B7', 'Comet tails explained', 'skyguide.example'),
    ('T', '#197571', 'Tide tables for the north coast', 'tides.example'),
    ('W', '#886010', 'Weather this weekend', 'weather.example'),
    ('Z', '#7042BE', 'Using Nebula', 'Built-in guide'),
]


def tile(x, y, letter, colour, size=20):
    rect(x, y, size, size, colour, 5)
    text(x + size / 2, y + size * .72, letter, '#FFFFFF', int(size * .6), 700, 'middle')


def spinner(x, y, p, size=20):
    """Loading: an accent arc in place of the site tile."""
    r = size / 2 - 2
    cx, cy = x + size / 2, y + size / 2
    add(f'<circle cx="{cx}" cy="{cy}" r="{r}" fill="none" stroke="{p["border"]}" stroke-width="2.5"/>')
    add(f'<path d="M{cx} {cy - r}A{r} {r} 0 0 1 {cx + r} {cy}" fill="none" stroke="{p["accent"]}" stroke-width="2.5" stroke-linecap="round"/>')


def tab_row(x, y, w, p, site, state='normal', hover=False):
    """A tab is a sidebar navigation row: the site's tile, the page title, its host underneath."""
    letter, colour, title, host = site
    if state == 'active':
        rect(x, y, w, 46, p['tint'], 6)
        rect(x + 1, y + 13, 3, 20, p['accent'], 1)
    elif hover:
        rect(x, y, w, 46, p['raised'], 6)
    if state == 'loading':
        spinner(x + 12, y + 13, p)
    elif state == 'sleeping':
        add('<g opacity=".45">'); tile(x + 12, y + 13, letter, colour); add('</g>')
    else:
        tile(x + 12, y + 13, letter, colour)
    room = w - 44 - (28 if hover or state == 'active' else 8)
    t = title if len(title) * 7 < room else title[:max(4, room // 7 - 1)] + '…'
    dim = state == 'sleeping'
    text(x + 42, y + 20, t, p['second'] if dim else p['text'], 13, 600 if state == 'active' else 400)
    sub = {'loading': 'Loading…', 'sleeping': 'Sleeping · ' + host, 'error': 'Could not load'}.get(state, host)
    text(x + 42, y + 37, sub, p['danger'] if state == 'error' else p['quiet'], 12)
    if hover or state == 'active':
        if hover:
            rect(x + w - 30, y + 11, 24, 24, p['border'], 6, opacity=.5)
        icon('x', x + w - 26, y + 15, p['second'], 16)


def sidebar(x, y, w, h, p, rows, hover=None, count=None):
    # The New tab row, then the tabs, then the places that open the start page's sections.
    rect(x, y, w, 36, p['raised'], 8, p['border'])
    icon('plus', x + 12, y + 10, p['text'], 16)
    text(x + 38, y + 23, 'New tab', p['text'], 13, 600)
    text(x + w - 12, y + 23, 'Ctrl+T', p['quiet'], 12, anchor='end')
    text(x + 4, y + 66, f'Tabs · {count or len(rows)}', p['quiet'], 12, 600)
    ry = y + 76
    for i, (site, state) in enumerate(rows):
        tab_row(x, ry, w, p, site, state, hover == i)
        ry += 50
    by = y + h - 2 * 38
    line(x + 4, by - 10, x + w - 4, by - 10, p['border'])
    for i, (ic, label, key) in enumerate((('star', 'Bookmarks', 'Ctrl+B'), ('clock', 'History', 'Ctrl+H'))):
        icon(ic, x + 12, by + i * 38 + 10, p['second'], 16)
        text(x + 38, by + i * 38 + 23, label, p['text'], 13)
        text(x + w - 12, by + i * 38 + 23, key, p['quiet'], 12, anchor='end')


def inset_browser(x, y, w, h, p, host, path, secure=True, star=False, loading=False, page=True):
    """The content inset: radius 12 on the surface colour, the toolbar row at its top, the page filling the rest
    with its lower corners rounded by the inset."""
    rect(x, y, w, h, p['surface'], 12, p['border'])
    ty = y + 8
    for i, (n, on) in enumerate((('back', True), ('forward', False), ('stop' if loading else 'reload', True))):
        icon(n, x + 18 + i * 40, ty + 8, p['text'] if on else p['quiet'], 20)
    fx, fw = x + 134, w - 134 - 56
    rect(fx, ty, fw, 36, p['raised'], 8, p['border'])
    icon('lock' if secure else 'unlock', fx + 12, ty + 10, p['ok'] if secure else p['second'], 16)
    runs(fx + 36, ty + 23, [(host, p['text'], 600), (path, p['quiet'], 400)])
    icon('star', fx + fw - 30, ty + 9, p['accent'] if star else p['second'], 18)
    if star:
        add(f'<g transform="translate({fx + fw - 30} {ty + 9}) scale(.75)" fill="{p["accent"]}" stroke="none">'
            f'<path d="m12 3 2.8 5.7 6.2.9-4.5 4.4 1 6.2L12 17.3 6.5 20.2l1-6.2L3 9.6l6.2-.9z"/></g>')
    icon('more', x + w - 44, ty + 8, p['text'], 20)
    py = y + 52
    line(x + 1, py, x + w - 1, py, p['border'])
    if loading:
        rect(x + 1, py - 2, int((w - 2) * .38), 3, p['accent'], 1)
    if page:
        # The page keeps its own colours; the inset's lower corners clip it.
        cid = f'in{x}{y}'
        add(f'<clipPath id="{cid}"><path d="M{x + 1} {py + 1}H{x + w - 1}V{y + h - 13}a12 12 0 0 1-12 12H{x + 13}a12 12 0 0 1-12-12z"/></clipPath>')
        add(f'<g clip-path="url(#{cid})">')
        bc.article(x + 1, py + 1, w - 2, h - 54, p)
        add('</g>')


def window(x, y, w, h, p, title, side=240, rail=False):
    """Title row (drawn by the desktop) continuing as one base material through the sidebar margin."""
    shadow(x, y, w, h)
    rect(x, y, w, h, p['subtle'], 12, p['border'])
    add(f'<defs><linearGradient id="m{x}{y}" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="{p["accent"]}" stop-opacity=".16"/>'
        f'<stop offset=".5" stop-color="{p["subtle"]}" stop-opacity="0"/><stop offset="1" stop-color="#3FB6B2" stop-opacity=".10"/></linearGradient></defs>')
    rect(x + 1, y + 1, w - 2, h - 2, f'url(#m{x}{y})', 11)
    icon('globe', x + 14, y + 11, p['accent'], 16)
    text(x + 38, y + 24, title, p['text'], 13, 600)
    for i, n in enumerate(('min', 'max', 'x')):
        icon(n, x + w - 100 + i * 32, y + 10, p['second'], 16)
    top = y + 40
    return top


def build():
    d, l = DARK, LIGHT
    add(f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}">')
    rect(0, 0, W, H, d['desk'])
    text(48, 55, 'Zenith / Nebula tabs concept', d['text'], 28, 600)
    text(48, 86, 'Proposal, not running UI · tabs as sidebar rows, the browser in the content inset, as in Orbit, Beacon and '
                 'Meridian · sample pages are fictional', d['second'], 14)

    # 1. The main view.
    bc.heading(130, 'Dark · Five tabs', 'The sidebar lists the tabs like any app’s pages; the browser fills the rounded inset beside it.')
    wx, wy, ww, wh = 48, 180, 1344, 780
    top = window(wx, wy, ww, wh, d, 'Watching a comet from a dark field — Nebula')
    sx, sw = wx + 12, 240
    rows = [(SITES[0], 'active'), (SITES[1], 'normal'), (SITES[2], 'loading'), (SITES[3], 'sleeping'), (SITES[4], 'normal')]
    sidebar(sx, top + 12, sw, wh - (top - wy) - 24, d, rows, hover=1)
    ix = sx + sw + 24
    inset_browser(ix, top + 12, wx + ww - 12 - ix, wh - (top - wy) - 24, d, 'fieldnotes.example', '/sky/comet-watching', star=True)
    for n, (cx, cy) in enumerate(((sx + 20, top + 2), (wx - 16, top + 88 + 73), (wx - 16, top + 88 + 123),
                                  (wx - 16, top + 88 + 173), (ix + 20, top + 2), (sx + 20, wy + wh - 92)), 1):
        callout(n, cx, cy, d)
    ny = 990
    cols = [
        ('1  New tab', ['Opens the start page with the address', 'field ready to type (Ctrl+T). Middle-', 'click or Ctrl+click on a link opens it', 'in a new tab behind this one.']),
        ('2  A tab is a row', ['Site tile, title, host. The close mark', 'shows on the active row and under the', 'pointer; middle-click closes too.', 'Drag a row to reorder.']),
        ('3  Loading', ['An accent arc replaces the tile while', 'the tab loads, also in the background.', 'The active tab loads first when tabs', 'share the one live connection.']),
        ('4  Sleeping', ['A tab unused for a while, or past the', 'memory limit, lets go of its page and', 'keeps address, scroll and history.', 'Selecting it loads it again.']),
        ('5  The inset', ['The toolbar from the first concept,', 'now inside the content inset; the page', 'fills the rest, its lower corners', 'rounded by the inset.']),
        ('6  Places', ['Bookmarks and History open those', 'sections of the start page in the', 'current tab (B4 owns their content).', '']),
    ]
    for i, (h1, body) in enumerate(cols):
        x = 48 + i * 226
        text(x, ny, h1, d['text'], 14, 600)
        note(x, ny + 24, body, d['second'], 12, 18)

    # 2. Row states and the narrow window.
    y2 = 1120
    bc.heading(y2, 'Dark · Row states and a narrow window')
    py = y2 + 30
    bc.panel(48, py, 380, 470, d, 'Tab rows', 'Same 46 px row, one meaning per state.')
    states = [('active', 'Active', None), ('normal', 'Under the pointer', 1), ('loading', 'Loading', None),
              ('sleeping', 'Sleeping', None), ('error', 'Failed', None)]
    for i, (st, label, hv) in enumerate(states):
        ry = py + 76 + i * 76
        text(68, ry, label, d['quiet'], 12, 600)
        tab_row(68, ry + 10, 340, d, SITES[i % 4], st if st != 'normal' else 'normal', hover=hv is not None)
    # Narrow: the sidebar becomes a rail of tiles.
    nx = 452
    bc.panel(nx, py, 440, 470, d, 'A narrow window', 'Under 820 px the sidebar folds to a rail of tiles; Ctrl+Shift+S hides it.')
    rx, ry0 = nx + 20, py + 72
    rect(rx, ry0, 400, 380, d['subtle'], 12, d['border'])
    rect(rx + 8, ry0 + 8, 40, 36, d['raised'], 8, d['border']); icon('plus', rx + 20, ry0 + 18, d['text'], 16)
    for i, (site, st) in enumerate(rows):
        ty = ry0 + 56 + i * 44
        if st == 'active':
            rect(rx + 6, ty - 4, 44, 36, d['tint'], 6); rect(rx + 6, ty + 4, 3, 20, d['accent'], 1)
        if st == 'loading':
            spinner(rx + 18, ty + 4, d)
        elif st == 'sleeping':
            add('<g opacity=".45">'); tile(rx + 18, ty + 4, site[0], site[1]); add('</g>')
        else:
            tile(rx + 18, ty + 4, site[0], site[1])
    rect(rx + 64, ry0 + 8, 328, 364, d['surface'], 12, d['border'])
    icon('back', rx + 76, ry0 + 18, d['text'], 18); icon('reload', rx + 106, ry0 + 18, d['text'], 18)
    rect(rx + 134, ry0 + 12, 220, 30, d['raised'], 8, d['border'])
    icon('lock', rx + 142, ry0 + 19, d['ok'], 14); text(rx + 162, ry0 + 32, 'fieldnotes.example', d['text'], 12, 600)
    icon('more', rx + 362, ry0 + 18, d['text'], 18)
    rect(rx + 65, ry0 + 50, 326, 321, d['page'])
    rect(rx + 65, ry0 + 50, 326, 30, '#1F3B36'); text(rx + 80, ry0 + 70, 'Field Notes', '#F4F1E8', 12, 700)
    text(rx + 80, ry0 + 108, 'Watching a comet', d['ink'], 18, 700)
    for k in range(6):
        rect(rx + 80, ry0 + 124 + k * 18, 280 - (k % 3) * 40, 7, '#D9D6CF', 3)
    hint = ['The rail keeps every tab one click away;', 'its tooltip names the page. The panel', 'button in the menu hides the sidebar', 'entirely (Ctrl+Shift+S) for reading.']
    # 2c. Tab menu and closed tabs.
    mx = 916
    bc.panel(mx, py, 476, 470, d, 'Right-click a tab', 'Everything a tab can do, with its key.')
    tab_row(mx + 20, py + 72, 436, d, SITES[1], 'normal', hover=True)
    bc.menu(mx + 150, py + 128, 286, [('plus', 'New tab below', 'Ctrl+T'), ('copy', 'Duplicate', ''), ('reload', 'Reload', 'Ctrl+R'),
                                      ('moon', 'Put to sleep', ''), None, ('x', 'Close', 'Ctrl+W', True),
                                      ('x', 'Close other tabs', ''), None, ('undo', 'Reopen closed tab', 'Ctrl+Shift+T')], d)
    note(mx + 20, py + 450, ['Rail mode uses the same menu on a tile.'], d['quiet'], 12)

    # 3. Light.
    y3 = 1650
    bc.heading(y3, 'Light · A new tab', 'The start page from B4 opens in the inset; typing in the address replaces it with the page.')
    wx3, wy3, wh3 = 48, y3 + 46, 560
    top = window(wx3, wy3, 1344, wh3, l, 'New tab — Nebula')
    rows3 = [(SITES[0], 'normal'), (SITES[1], 'normal'), (('+', l['accent'], 'New tab', 'Start page'), 'active')]
    sidebar(wx3 + 12, top + 12, 240, wh3 - (top - wy3) - 24, l, rows3)
    ix = wx3 + 12 + 240 + 24
    iw = wx3 + 1344 - 12 - ix
    ih = wh3 - (top - wy3) - 24
    inset_browser(ix, top + 12, iw, ih, l, '', '', page=False)
    rect(ix + 135, top + 21, iw - 190, 34, l['raised'], 8, l['accent'])
    text(ix + 170, top + 43, 'Search bookmarks and history, or enter an address', l['quiet'], 14)
    icon('search', ix + 147, top + 30, l['second'], 16)
    cx = ix + iw // 2
    text(cx, top + 140, 'Good evening', l['text'], 24, 600, 'middle')
    text(cx, top + 166, 'Your bookmarks and recent pages; Ctrl+L to type an address.', l['second'], 14, anchor='middle')
    for i, (ch, t1, t2, c) in enumerate((('F', 'Field Notes', 'fieldnotes.example', '#1F3B36'), ('S', 'Sky guide', 'skyguide.example', '#3157B7'),
                                         ('T', 'Tide tables', 'tides.example', '#197571'), ('W', 'Weather', 'weather.example', '#886010'))):
        tx = cx - 2 * 176 - 24 + i * 188
        rect(tx, top + 200, 176, 96, l['raised'], 12, l['border'])
        tile(tx + 16, top + 216, ch, c, 32)
        text(tx + 16, top + 270, t1, l['text'], 13, 600); text(tx + 16, top + 286, t2, l['quiet'], 12)
    text(cx - 376, top + 340, 'Recently closed', l['text'], 14, 600)
    for i, (t1, t2) in enumerate((('Comet tails explained', 'skyguide.example'), ('Weather this weekend', 'weather.example'))):
        ry = top + 356 + i * 30
        icon('undo', cx - 376, ry + 2, l['quiet'], 16)
        text(cx - 350, ry + 15, t1, l['text'], 13); text(cx - 80, ry + 15, t2, l['quiet'], 12)

    # 4. Keys and stages.
    y4 = 2280
    bc.heading(y4, 'Keys, limits and stages')
    keys = [('Ctrl+T', 'New tab'), ('Ctrl+W', 'Close tab (the last one closes the window)'), ('Ctrl+Tab / Ctrl+Shift+Tab', 'Next / previous tab'),
            ('Ctrl+1 … Ctrl+8, Ctrl+9', 'That tab, the last tab'), ('Ctrl+Shift+T', 'Reopen the last closed tab (8 kept)'),
            ('Middle-click, Ctrl+click', 'Open a link in a new tab behind'), ('Ctrl+Shift+S', 'Hide or show the sidebar')]
    for i, (k, v) in enumerate(keys):
        text(48, y4 + 40 + i * 26, k, d['text'], 13, 600, mono=False)
        text(260, y4 + 40 + i * 26, v, d['second'], 13)
    ox = 760
    stages = [('T0', 'Window and tab state split; the browser moves into the inset'), ('T1', 'Sidebar tabs: new, close, switch, keys, per-tab state'),
              ('T2', 'Open in a new tab: middle-click, Ctrl+click, link menu, target'), ('T3', 'Reorder, duplicate, reopen closed, the rail'),
              ('T4', 'Sleeping tabs, the tab limit, active-first loading, session restore')]
    for i, (k, v) in enumerate(stages):
        text(ox, y4 + 40 + i * 26, k, d['text'], 13, 600)
        text(ox + 40, y4 + 40 + i * 26, v, d['second'], 13)
    note(ox, y4 + 40 + 5 * 26 + 10, ['Up to 16 tabs; past 8, the least recently used sleep. One live connection',
                                     'per process (browser network ABI 1.0) is shared: the active tab goes first.'], d['quiet'], 12, 18)
    add('</svg>')


def main():
    build()
    target = ROOT / 'docs/design/browser-tabs-concept.svg'
    target.write_text('\n'.join(bc.out) + '\n')
    print(target)


if __name__ == '__main__':
    main()
