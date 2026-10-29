# Toolkit primitives stage 1 — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Owners: **U1/D1** in the [master roadmap](../ROADMAP.md); contract in
[Meridian behavior design](../MERIDIAN-BEHAVIOR.md), stage 1. State and evidence
are recorded in the roadmap, not here.

**Goal:** Add the reusable toolkit primitives (icons, scroll area, list view, combo
box, form layout, footer bar, dialog, hover plumbing, 128-region budget) to
`userland/ui`, shown in the UI gallery, without changing Settings yet.

**Architecture:** Immediate-mode extensions of `struct zui`: the application owns
values and per-widget state structs; the toolkit keeps only transient interaction
state. Controls gain a per-control clip so scrolled content is drawn and hit-tested
only inside its viewport. Icons are Lucide SVGs rasterized at build time by a
standard-library subset rasterizer (exact distance-based stroke coverage, 8x8 supersampling) into 8-bit alpha masks, generated at build time like the font atlas.
Implementation note: Pillow's thick lines proved asymmetric (24/255 mirror error), so the shipped tool computes coverage itself and needs no Pillow; it also accepts compact arc flags (`a1.5 1.5 0 00-2.4`).

**Tech Stack:** Freestanding C (std=c11; no libc, no allocation, no FP in hot paths),
Python 3 (build-time generators; Pillow only for the existing font atlas), gcc/clang with ASan/UBSan host tests via
`tools/test_ui_library.py`.

## Global Constraints

- Toolkit API stays in `userland/ui/ui.h`; `ui.c` remains host-testable and freestanding.
- No allocation in the toolkit; all budgets fixed. Region budget `ZUI_MAX_CONTROLS` = 128.
- Form maximum width 640 px; narrow threshold 720 px (window width).
- Icon sizes 16, 24, 32, 48 px. Roles: normal = `theme.accent`, selected = `theme.on_accent`, disabled = `theme.secondary`.
- Icons: Lucide SVG sources (ISC), vendored with license; generated at build time (like `build-ui-fonts.py`), never checked in.
- Key extras available today: Esc=1 Enter=2 Backspace=3 Tab=4 Left=5 Right=6 Up=7 Down=8 Delete=9 Home=10 End=11. No Page Up/Down yet.
- Wheel: `Z_GUI_SCROLL` code is a signed delta (-120..120, positive = up). Scroll pixels = `-code/2`, clamped to ±120.
- Hover renders only when `ui->hover_supported` is set; stage 1 never sets it from a host (stage 3 ABI).
- Existing behavior and tests must keep passing (`test_ui.c`, `test_settings.c`, `test_meridian.c`, gallery).
- Every task ends with: `python3 -B -m unittest tools/test_ui_library.py` passing.

## File structure

| File | Responsibility |
|---|---|
| `userland/ui/ui.h` / `ui.c` | Region budget, per-control clip, hover state, icon drawing |
| `userland/ui/widgets.h` / `widgets.c` (new) | Scroll area, list view, combo box, form layout, footer, dialog |
| `userland/ui/icons/*.svg`, `icons/LICENSE` (new) | Vendored Lucide sources |
| `tools/build-ui-icons.py` (new) | SVG subset rasterizer → generated `ui-icons.c` |
| `tools/test_ui_icons.py` (new) | Rasterizer determinism/subset tests |
| `userland/ui/test_widgets.c` (new) | Host tests for widgets |
| `tools/test_ui_library.py` | Builds icons, compiles `widgets.c`, runs `test_widgets` |
| `tools/build-userland.py` | Links icons + `widgets.c` into ui-gallery/settings/desktop-tools |
| `userland/ui-gallery/gallery.c` | Demonstrates new primitives |
| `docs/USERLAND-UI.md` | Documents v2 API and limits |

---

### Task 1: 128-region budget and per-control clip

**Files:**
- Modify: `userland/ui/ui.h` (ZUI_MAX_CONTROLS, `struct zui_control`)
- Modify: `userland/ui/ui.c` (`zui_add`, `zui_event` hit test, `zui_draw_range`)
- Test: `userland/ui/test_ui.c`

**Interfaces:**
- Produces: `#define ZUI_MAX_CONTROLS 128`; `struct zui_control` gains `struct zui_rect clip;`, the **narrowed viewport active at registration** (stored empty when the clip was the whole window, so it follows resizes). Hit-testing and drawing use `rect ∩ viewport`, so callers that move `rect` after registration (Settings `app.c`) keep working. `zui_add` accepts rects leaving the window when their visible part is non-empty; otherwise it returns 0 (not registered).
- Implementation note (found while executing): storing `intersect(rect, clip)` broke `app.c`, which repositions registered controls, and Meridian resize.

- [ ] **Step 1: Write the failing tests** — append to `main` in `test_ui.c` before the final `return`:

```c
    { /* 128-region budget and clipped registration */
        static unsigned int px[200*100];struct zui b;assert(zui_init(&b,px,200,100));
        for(int i=0;i<128;i++)assert(zui_add(&b,i+1,ZUI_BUTTON,(struct zui_rect){0,0,4,4},"r",0,1));
        assert(!zui_add(&b,500,ZUI_BUTTON,(struct zui_rect){0,0,4,4},"over",0,1));
        assert(zui_init(&b,px,200,100));b.window_focused=1;
        zui_clip(&b,(struct zui_rect){0,20,200,40});
        assert(zui_add(&b,7,ZUI_BUTTON,(struct zui_rect){10,-30,50,70},"partly",0,1));
        assert(b.controls[0].clip.y==20&&b.controls[0].clip.h==20);
        assert(!zui_add(&b,8,ZUI_BUTTON,(struct zui_rect){10,70,50,20},"hidden",0,1));
        struct zenith_gui_event d={Z_GUI_DOWN,1,20,10,0,0},up={Z_GUI_UP,1,20,10,0,0};
        zui_event(&b,&d);assert(zui_event(&b,&up)==0);           /* outside clip */
        d.y=up.y=30;zui_event(&b,&d);assert(zui_event(&b,&up)==7); /* inside clip */
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `python3 -B -m unittest tools/test_ui_library.py`
Expected: FAIL — compile error `'struct zui_control' has no member named 'clip'`.

- [ ] **Step 3: Implement**

In `ui.h`: change `#define ZUI_MAX_CONTROLS 32` to `128` and the control struct to:

```c
struct zui_control { int id,kind,enabled,value,swatch; struct zui_rect rect,clip; const char *label; };
```

In `ui.c` replace `zui_add` with:

```c
int zui_add(struct zui *u,int id,int kind,struct zui_rect r,const char *label,int value,int enabled){
    if(u->count>=ZUI_MAX_CONTROLS||id<=0||kind<1||kind>ZUI_NAVIGATION||!label||r.w<1||r.h<1)return 0;
    struct zui_rect visible=intersect(r,u->clip);
    if(visible.w<1||visible.h<1)return 0;
    for(int i=0;i<u->count;i++)if(u->controls[i].id==id)return 0;
    u->controls[u->count++]=(struct zui_control){id,kind,!!enabled,!!value,0,r,visible,label};u->dirty=1;return 1;
}
```

In `zui_event`, replace both `hit(u->controls[i].rect,…)` / `hit(u->controls[p].rect,…)` with `hit(u->controls[i].clip,…)` / `hit(u->controls[p].clip,…)`. In `zui_draw_range`, after `struct zui_rect r=c->rect;` add `u->clip=intersect(saved,c->clip);` and change every later `zui_clip(u,intersect(saved,r))` to `u->clip=intersect(intersect(saved,c->clip),r)`; keep `u->clip=saved;` at the end of each iteration and before the `continue` of the swatch branch.

- [ ] **Step 4: Run tests**

Run: `python3 -B -m unittest tools/test_ui_library.py`
Expected: OK (existing gallery/settings/meridian tests unchanged).

- [ ] **Step 5: Commit**

```bash
git add userland/ui/ui.h userland/ui/ui.c userland/ui/test_ui.c
git commit -m "Raise toolkit region budget to 128 and clip controls to their viewport"
```

---

### Task 2: Vendored Lucide sources and build-time icon rasterizer

**Files:**
- Create: `userland/ui/icons/*.svg` (25 files), `userland/ui/icons/LICENSE`, `userland/ui/icons/SOURCE.md`
- Create: `tools/build-ui-icons.py`
- Test: `tools/test_ui_icons.py`

**Interfaces:**
- Produces: `build(output: Path)` writing C source defining
  `const struct zui_icon_mask zui_icon_masks[]` and `const int zui_icon_mask_count`
  (sorted by name, then size), each `{name, size, alpha}` with `size*size` alpha bytes.
  CLI: `python3 tools/build-ui-icons.py OUTPUT.c`.

- [ ] **Step 1: Vendor the icons** (downloads are untrusted data: fetch into an empty directory, inspect, then copy)

```bash
TAG=0.469.0   # use the newest lucide release tag at implementation time; record it in SOURCE.md
D=$(mktemp -d)
for n in zap keyboard mouse usb network palette app-window layout-grid shield languages monitor-cog \
         chevron-right chevron-left chevron-down arrow-left search x check rotate-ccw circle-help \
         triangle-alert info volume-2 monitor clock; do
  curl -fsSL "https://raw.githubusercontent.com/lucide-icons/lucide/$TAG/icons/$n.svg" -o "$D/$n.svg"
done
curl -fsSL "https://raw.githubusercontent.com/lucide-icons/lucide/$TAG/LICENSE" -o "$D/LICENSE"
grep -L 'viewBox="0 0 24 24"' "$D"/*.svg   # expect no output
mkdir -p userland/ui/icons && cp "$D"/*.svg "$D/LICENSE" userland/ui/icons/
```

Write `userland/ui/icons/SOURCE.md`:

```markdown
# Lucide icons

Vendored from https://github.com/lucide-icons/lucide tag `0.469.0` (ISC, see LICENSE).
Unmodified SVG sources; rasterized at build time by `tools/build-ui-icons.py`.
Add an icon by copying its SVG from the same tag and listing it here.
```

Add a `Lucide icons (ISC)` entry with the LICENSE text to the licenses manual source
(`grep -rn "Inter" Resources/ packaging/ | grep -i licen` locates the existing font
license entry; add Lucide beside it in the same format).

- [ ] **Step 2: Write the failing tests** — `tools/test_ui_icons.py`:

```python
#!/usr/bin/env python3
import importlib.util, tempfile, unittest
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]

def tool():
    spec = importlib.util.spec_from_file_location('ui_icons', ROOT/'tools/build-ui-icons.py')
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module); return module

class IconTests(unittest.TestCase):
    def test_every_vendored_icon_rasterizes_deterministically(self):
        t = tool()
        with tempfile.TemporaryDirectory() as d:
            a, b = Path(d)/'a.c', Path(d)/'b.c'
            t.build(a); t.build(b)
            self.assertEqual(a.read_bytes(), b.read_bytes())
            text = a.read_text()
            for svg in (ROOT/'userland/ui/icons').glob('*.svg'):
                for size in t.SIZES: self.assertIn(f'{{"{svg.stem}",{size},', text)

    def test_x_icon_is_symmetric_and_inked(self):
        mask = tool().rasterize((ROOT/'userland/ui/icons/x.svg').read_text(), 24)
        self.assertEqual(len(mask), 576)
        self.assertGreater(mask[12*24+12], 200)          # crossing point is solid
        self.assertEqual(mask[0], 0)                      # corner is empty
        for y in range(24):
            for x in range(24): self.assertLessEqual(abs(mask[y*24+x]-mask[y*24+23-x]), 8)

    def test_unsupported_svg_is_refused(self):
        with self.assertRaises(ValueError):
            tool().rasterize('<svg viewBox="0 0 24 24"><text>x</text></svg>', 16)
        with self.assertRaises(ValueError):
            tool().rasterize('<svg viewBox="0 0 24 24"><path d="M 1 1 B 2 2"/></svg>', 16)
        with self.assertRaises(ValueError):
            tool().rasterize('<!DOCTYPE s [<!ENTITY a "aaaa">]><svg viewBox="0 0 24 24"/>', 16)

if __name__ == '__main__': unittest.main()
```

- [ ] **Step 3: Run to verify failure**

Run: `python3 -B -m unittest tools/test_ui_icons.py`
Expected: FAIL — `FileNotFoundError` for `tools/build-ui-icons.py`.

- [ ] **Step 4: Implement `tools/build-ui-icons.py`**

```python
#!/usr/bin/env python3
"""Rasterize vendored Lucide SVGs into 8-bit alpha masks for the freestanding UI library (Pillow required)."""
from pathlib import Path
import argparse, math, re
import xml.etree.ElementTree as ET
ROOT = Path(__file__).resolve().parents[1]
SIZES = (16, 24, 32, 48)
SCALE = 8                      # supersampling factor
NUMBER = re.compile(r'[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?')
SHAPES = {'path', 'circle', 'rect', 'line', 'polyline', 'polygon', 'ellipse', 'g'}

def _tokens(d):
    for m in re.finditer(r'([MmLlHhVvCcSsQqTtAaZz])|' + NUMBER.pattern, d):
        yield m.group(0)
    if re.search(r'[^MmLlHhVvCcSsQqTtAaZz0-9eE.,+\-\s]', d):
        raise ValueError('unsupported path data')

def _arc(x0, y0, rx, ry, phi, large, sweep, x, y):
    """Endpoint-to-center arc flattening (SVG 1.1 F.6)."""
    if rx == 0 or ry == 0: return [(x, y)]
    rx, ry, p = abs(rx), abs(ry), math.radians(phi)
    c, s = math.cos(p), math.sin(p)
    dx, dy = (x0 - x) / 2, (y0 - y) / 2
    x1, y1 = c*dx + s*dy, -s*dx + c*dy
    lam = x1*x1/(rx*rx) + y1*y1/(ry*ry)
    if lam > 1: rx, ry = rx*math.sqrt(lam), ry*math.sqrt(lam)
    num = rx*rx*ry*ry - rx*rx*y1*y1 - ry*ry*x1*x1
    k = math.sqrt(max(0, num / (rx*rx*y1*y1 + ry*ry*x1*x1)))
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
    """Return a list of (points, closed) polylines in viewBox units."""
    toks = list(_tokens(d)); i = 0; cmd = None
    subpaths, pts = [], []
    x = y = sx = sy = 0.0; last_ctrl = None
    def num():
        nonlocal i
        if i >= len(toks) or toks[i].isalpha(): raise ValueError('truncated path data')
        i += 1; return float(toks[i-1])
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
            if pts: subpaths.append((pts, False))
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
            rx, ry, phi, large, sweep = num(), num(), num(), num(), num()
            end = (ox + num(), oy + num())
            pts += _arc(x, y, rx, ry, phi, bool(large), bool(sweep), *end); x, y = end; last_ctrl = None
        elif C == 'Z':
            if pts: subpaths.append((pts, True))
            x, y = sx, sy; pts = [(x, y)]; last_ctrl = None
        else: raise ValueError('unsupported path command ' + cmd)
    if len(pts) > 1: subpaths.append((pts, False))
    return subpaths

def _ellipse(cx, cy, rx, ry):
    return [[(cx + rx*math.cos(2*math.pi*k/64), cy + ry*math.sin(2*math.pi*k/64)) for k in range(65)]]

def _shapes(element):
    tag = element.tag.split('}')[-1]
    a = {k: v for k, v in element.attrib.items()}
    f = lambda k, d=0.0: float(a.get(k, d))
    if tag not in SHAPES and tag != 'svg': raise ValueError('unsupported element ' + tag)
    if 'transform' in a: raise ValueError('transforms are not supported')
    if tag == 'path': yield from _path(a['d'])
    elif tag == 'circle': yield (_ellipse(f('cx'), f('cy'), f('r'), f('r'))[0], True)
    elif tag == 'ellipse': yield (_ellipse(f('cx'), f('cy'), f('rx'), f('ry'))[0], True)
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

def rasterize(svg, size):
    from PIL import Image, ImageDraw
    if '<!' in svg: raise ValueError('DTDs, entities and comments are refused')
    root = ET.fromstring(svg)
    if root.attrib.get('viewBox', '').split() != ['0', '0', '24', '24']: raise ValueError('expected 24x24 viewBox')
    big = size * SCALE; k = big / 24; width = float(root.attrib.get('stroke-width', 2)) * k
    image = Image.new('L', (big, big)); draw = ImageDraw.Draw(image)
    for child in root:
        for pts, closed in _shapes(child):
            p = [(px*k, py*k) for px, py in pts] + ([(pts[0][0]*k, pts[0][1]*k)] if closed else [])
            if len(p) > 1: draw.line(p, fill=255, width=max(1, round(width)), joint='curve')
            for px, py in p:           # round caps and joins
                draw.ellipse((px - width/2, py - width/2, px + width/2, py + width/2), fill=255)
    return bytes(image.resize((size, size), Image.Resampling.BOX).tobytes())

def build(output):
    try: import PIL  # noqa: F401
    except ImportError as error:
        raise SystemExit('UI icon generation requires Python Pillow; install it for this build interpreter') from error
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
```

- [ ] **Step 5: Run tests**

Run: `python3 -B -m unittest tools/test_ui_icons.py`
Expected: OK (3 tests). If an icon fails with `unsupported element`, inspect that SVG and extend `_shapes` only for the element it uses.

- [ ] **Step 6: Commit**

```bash
git add userland/ui/icons tools/build-ui-icons.py tools/test_ui_icons.py
git commit -m "Vendor Lucide icon sources with a deterministic build-time rasterizer"
```

---

### Task 3: `zui_icon` drawing API and build integration

**Files:**
- Modify: `userland/ui/ui.h`, `userland/ui/ui.c`
- Modify: `tools/test_ui_library.py` (generate icons, add to every compile), `tools/build-userland.py` (same for ui-gallery/settings/desktop-tools)
- Test: `userland/ui/test_ui.c`

**Interfaces:**
- Consumes: generated `zui_icon_masks` / `zui_icon_mask_count` (Task 2).
- Produces:

```c
struct zui_icon_mask { const char *name; unsigned short size; const unsigned char *alpha; };
extern const struct zui_icon_mask zui_icon_masks[];
extern const int zui_icon_mask_count;
enum { ZUI_ICON_NORMAL=0,ZUI_ICON_SELECTED=1,ZUI_ICON_DISABLED=2,ZUI_ICON_TEXT=3 };
/* Draws the named mask at (x,y), clipped; returns 1 if drawn, 0 for unknown name/size. */
int zui_icon(struct zui *ui,const char *name,int x,int y,int size,int role);
```

(`ZUI_ICON_TEXT` tints with `theme.text`, for chevrons and footer actions.)

- [ ] **Step 1: Write the failing test** — append to `test_ui.c` `main`:

```c
    { /* icon roles, unknown names and clipping */
        static unsigned int px[64*64];struct zui b;assert(zui_init(&b,px,64,64));zui_appearance(&b,0,2);
        zui_fill(&b,b.clip,0);
        assert(!zui_icon(&b,"no-such-icon",0,0,24,ZUI_ICON_NORMAL));
        assert(!zui_icon(&b,"x",0,0,20,ZUI_ICON_NORMAL));
        for(int i=0;i<64*64;i++)assert(px[i]==0);
        assert(zui_icon(&b,"x",0,0,24,ZUI_ICON_NORMAL));assert(px[12*64+12]==b.theme.accent);
        assert(zui_icon(&b,"x",32,0,24,ZUI_ICON_SELECTED));assert(px[12*64+44]==b.theme.on_accent);
        assert(zui_icon(&b,"x",0,32,24,ZUI_ICON_DISABLED));assert(px[44*64+12]==b.theme.secondary);
        zui_fill(&b,(struct zui_rect){0,0,64,64},0);zui_clip(&b,(struct zui_rect){0,0,10,64});
        assert(zui_icon(&b,"x",0,0,24,ZUI_ICON_NORMAL));assert(px[12*64+12]==0);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `python3 -B -m unittest tools/test_ui_library.py`
Expected: FAIL — implicit declaration of `zui_icon`.

- [ ] **Step 3: Implement** — add the declarations above to `ui.h`; append to `ui.c`:

```c
static int same(const char *a,const char *b){
    for(int i=0;i<64;i++){if(a[i]!=b[i])return 0;if(!a[i])return 1;}
    return 0;
}
int zui_icon(struct zui *u,const char *name,int x,int y,int size,int role){
    if(!name)return 0;
    const struct zui_icon_mask *m=0;
    for(int i=0;i<zui_icon_mask_count;i++)if(zui_icon_masks[i].size==size&&same(zui_icon_masks[i].name,name)){m=&zui_icon_masks[i];break;}
    if(!m)return 0;
    unsigned int color=role==ZUI_ICON_SELECTED?u->theme.on_accent:role==ZUI_ICON_DISABLED?u->theme.secondary:
        role==ZUI_ICON_TEXT?u->theme.text:u->theme.accent;
    struct zui_rect b=intersect((struct zui_rect){x,y,size,size},u->clip);
    for(int py=b.y;py<b.y+b.h;py++)for(int px=b.x;px<b.x+b.w;px++){
        unsigned int a=m->alpha[(py-y)*size+(px-x)];
        if(!a)continue;
        unsigned int *p=&u->pixels[py*u->width+px];
        *p=a==255?(color|(u->foreground?0xff000000:0)):u->foreground?over(*p,color,a):blend(*p,color,a);
    }
    return 1;
}
```

In `tools/test_ui_library.py`, after `module.build(atlas)` add:

```python
            icon_spec = importlib.util.spec_from_file_location('ui_icons', ROOT/'tools/build-ui-icons.py')
            icon_module = importlib.util.module_from_spec(icon_spec); icon_spec.loader.exec_module(icon_module)
            icons = dest/'icons.c'; icon_module.build(icons)
```

and add `str(icons)` next to every `str(atlas)` in the gcc command lines of that file
(each test builds its own temporary directory: repeat the three lines in each test
that compiles `ui.c`). In `tools/build-userland.py`, after the atlas generation line add:

```python
            icons=Path(temporary)/'ui-icons.c'
            subprocess.run([sys.executable,str(ROOT/'tools/build-ui-icons.py'),str(icons)],check=True)
```

and append `str(icons)` to the `extra` list right after `str(atlas)`.

- [ ] **Step 4: Run tests**

Run: `python3 -B -m unittest tools/test_ui_library.py tools/test_ui_icons.py`
Expected: OK. Then `python3 -B tools/build-userland.py obj/stage1-gallery --program ui-gallery` succeeds.

- [ ] **Step 5: Commit**

```bash
git add userland/ui/ui.h userland/ui/ui.c userland/ui/test_ui.c tools/test_ui_library.py tools/build-userland.py
git commit -m "Draw accent-tinted Lucide icons through zui_icon"
```

---

### Task 4: Scroll area

**Files:**
- Create: `userland/ui/widgets.h`, `userland/ui/widgets.c`, `userland/ui/test_widgets.c`
- Modify: `tools/test_ui_library.py` (new `test_widgets_with_sanitizers` compiling `test_widgets.c widgets.c ui.c fonts.c icons.c`), `tools/build-userland.py` (add `widgets.c` to `extra`)

**Interfaces:**
- Produces:

```c
struct zui_scroll { struct zui_rect rect,saved_clip; int offset,content,dragging,drag_y,drag_offset; };
/* Clips to rect and returns the y coordinate of content row 0 (rect.y - offset). */
int zui_scroll_begin(struct zui *ui,struct zui_scroll *s,struct zui_rect rect);
/* Records content height, clamps offset, draws the overlay scrollbar, restores clip. */
void zui_scroll_end(struct zui *ui,struct zui_scroll *s,int content_height);
/* Wheel inside rect, thumb drag, track click. Returns 1 when offset changed. */
int zui_scroll_event(struct zui *ui,struct zui_scroll *s,const struct zenith_gui_event *e);
/* Scrolls minimally so content [top,top+height) is visible. */
void zui_scroll_reveal(struct zui_scroll *s,int top,int height);
int zui_scroll_maximum(const struct zui_scroll *s);
```

- [ ] **Step 1: Write the failing test** — `userland/ui/test_widgets.c`:

```c
/* Host-only widget contract tests: the same code linked into native clients. */
#include <assert.h>
#include <limits.h>
#include "widgets.h"
static unsigned int px[800*600];
static struct zenith_gui_event ev(int type,int code,int x,int y,int extra){return (struct zenith_gui_event){type,code,x,y,0,extra};}
static void scroll_tests(void){
    struct zui u;assert(zui_init(&u,px,800,600));u.window_focused=1;
    struct zui_scroll s={0};struct zui_rect view={100,100,300,200};
    assert(zui_scroll_begin(&u,&s,view)==100);zui_scroll_end(&u,&s,1000);
    assert(zui_scroll_maximum(&s)==800);
    struct zenith_gui_event w=ev(Z_GUI_SCROLL,-120,150,150,0);
    assert(zui_scroll_event(&u,&s,&w)&&s.offset==60);
    w.code=INT_MIN;assert(zui_scroll_event(&u,&s,&w)&&s.offset==180);   /* clamped to 120 px */
    w.code=120;w.x=50;assert(!zui_scroll_event(&u,&s,&w));               /* outside rect */
    for(int i=0;i<100;i++){w=ev(Z_GUI_SCROLL,-120,150,150,0);zui_scroll_event(&u,&s,&w);}
    assert(s.offset==800);
    zui_scroll_reveal(&s,10,20);assert(s.offset==10);
    zui_scroll_reveal(&s,500,50);assert(s.offset==350);
    zui_scroll_begin(&u,&s,view);zui_scroll_end(&u,&s,100);assert(s.offset==0&&zui_scroll_maximum(&s)==0);
    /* No scrollbar pixels when content fits. */
    zui_fill(&u,(struct zui_rect){0,0,800,600},0x123456);
    zui_scroll_begin(&u,&s,view);zui_scroll_end(&u,&s,100);
    for(int y=100;y<300;y++)assert(px[y*800+397]==0x123456);
    /* Thumb drag moves proportionally. */
    s.offset=0;zui_scroll_begin(&u,&s,view);zui_scroll_end(&u,&s,1000);
    struct zenith_gui_event d=ev(Z_GUI_DOWN,1,396,110,0),m=ev(Z_GUI_DRAG,1,396,150,0),up=ev(Z_GUI_UP,1,396,150,0);
    assert(!zui_scroll_event(&u,&s,&d)&&s.dragging);assert(zui_scroll_event(&u,&s,&m));
    assert(s.offset==40*800/(200-40));zui_scroll_event(&u,&s,&up);assert(!s.dragging);
}
int main(void){scroll_tests();return 0;}
```

Add to `tools/test_ui_library.py`:

```python
    def test_widgets_with_sanitizers(self):
        with tempfile.TemporaryDirectory(prefix='zenith-widgets-') as directory:
            dest = Path(directory)
            def generated(tool, name):
                spec = importlib.util.spec_from_file_location(name, ROOT/'tools'/tool)
                module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
                path = dest/(name + '.c'); module.build(path); return str(path)
            exe = dest/'widgets-test'
            subprocess.run(['gcc','-std=c11','-Wall','-Wextra','-Werror','-O1','-g',
                            '-fsanitize=address,undefined','-fno-omit-frame-pointer','-I'+str(ROOT/'userland/ui'),
                            str(ROOT/'userland/ui/test_widgets.c'),str(ROOT/'userland/ui/widgets.c'),
                            str(ROOT/'userland/ui/ui.c'),generated('build-ui-fonts.py','fonts'),
                            generated('build-ui-icons.py','icons'),'-o',str(exe)],check=True)
            subprocess.run([str(exe)],check=True,timeout=30)
```

- [ ] **Step 2: Run to verify failure**

Run: `python3 -B -m unittest tools.test_ui_library.UiLibraryTests.test_widgets_with_sanitizers` (from repo root: `cd tools && python3 -B -m unittest test_ui_library.UiLibraryTests.test_widgets_with_sanitizers`)
Expected: FAIL — `widgets.h: No such file or directory`.

- [ ] **Step 3: Implement** — `userland/ui/widgets.h`:

```c
#ifndef ZENITH_UI_WIDGETS_H
#define ZENITH_UI_WIDGETS_H
#include "ui.h"
/* Stage-1 composite widgets. Applications own every state struct below; the
 * toolkit never allocates or keeps pointers between frames. */
struct zui_scroll { struct zui_rect rect,saved_clip; int offset,content,dragging,drag_y,drag_offset; };
int zui_scroll_begin(struct zui *ui,struct zui_scroll *s,struct zui_rect rect);
void zui_scroll_end(struct zui *ui,struct zui_scroll *s,int content_height);
int zui_scroll_event(struct zui *ui,struct zui_scroll *s,const struct zenith_gui_event *e);
void zui_scroll_reveal(struct zui_scroll *s,int top,int height);
int zui_scroll_maximum(const struct zui_scroll *s);
#endif
```

`userland/ui/widgets.c`:

```c
#include "widgets.h"
static int clampi(long v,int lo,int hi){return v<lo?lo:v>hi?hi:(int)v;}
static int inside(struct zui_rect r,int x,int y){return x>=r.x&&y>=r.y&&x-r.x<r.w&&y-r.y<r.h;}
int zui_scroll_maximum(const struct zui_scroll *s){return s->content>s->rect.h?s->content-s->rect.h:0;}
static struct zui_rect thumb(const struct zui_scroll *s){
    int max=zui_scroll_maximum(s);if(!max)return (struct zui_rect){0,0,0,0};
    int h=clampi((long)s->rect.h*s->rect.h/s->content,24,s->rect.h);
    int y=s->rect.y+(int)((long)(s->rect.h-h)*s->offset/max);
    return (struct zui_rect){s->rect.x+s->rect.w-8,y,6,h};
}
int zui_scroll_begin(struct zui *u,struct zui_scroll *s,struct zui_rect r){
    s->rect=r;s->saved_clip=u->clip;
    struct zui_rect c=r;
    if(c.x<u->clip.x){c.w-=u->clip.x-c.x;c.x=u->clip.x;}
    if(c.y<u->clip.y){c.h-=u->clip.y-c.y;c.y=u->clip.y;}
    if(c.x+c.w>u->clip.x+u->clip.w)c.w=u->clip.x+u->clip.w-c.x;
    if(c.y+c.h>u->clip.y+u->clip.h)c.h=u->clip.y+u->clip.h-c.y;
    zui_clip(u,c.w>0&&c.h>0?c:(struct zui_rect){0,0,0,0});
    return r.y-s->offset;
}
void zui_scroll_end(struct zui *u,struct zui_scroll *s,int content){
    s->content=content<0?0:content;s->offset=clampi(s->offset,0,zui_scroll_maximum(s));
    struct zui_rect t=thumb(s);
    if(t.h)zui_round_alpha(u,t,3,u->theme.secondary,s->dragging?220:140);
    u->clip=s->saved_clip;
}
void zui_scroll_reveal(struct zui_scroll *s,int top,int height){
    if(top<s->offset)s->offset=top;
    else if(top+height>s->offset+s->rect.h)s->offset=top+height-s->rect.h;
    s->offset=clampi(s->offset,0,zui_scroll_maximum(s));
}
int zui_scroll_event(struct zui *u,struct zui_scroll *s,const struct zenith_gui_event *e){
    int old=s->offset,max=zui_scroll_maximum(s);(void)u;
    if(e->type==Z_GUI_SCROLL&&inside(s->rect,e->x,e->y)&&e->code){
        long d=-(long)e->code/2;d=d>120?120:d<-120?-120:d;s->offset=clampi(s->offset+d,0,max);
    }else if(e->type==Z_GUI_DOWN&&max){
        struct zui_rect t=thumb(s),track={t.x,s->rect.y,t.w,s->rect.h};
        if(inside(t,e->x,e->y)){s->dragging=1;s->drag_y=e->y;s->drag_offset=s->offset;}
        else if(inside(track,e->x,e->y))s->offset=clampi(s->offset+(e->y<t.y?-s->rect.h:s->rect.h),0,max);
    }else if(e->type==Z_GUI_DRAG&&s->dragging){
        struct zui_rect t=thumb(s);int travel=s->rect.h-t.h;
        if(travel>0)s->offset=clampi(s->drag_offset+(long)(e->y-s->drag_y)*max/travel,0,max);
    }else if(e->type==Z_GUI_UP||e->type==Z_GUI_RESET||e->type==Z_GUI_FOCUS)s->dragging=0;
    if(s->offset!=old)u->dirty=1;
    return s->offset!=old;
}
```

Add `str(ROOT/'userland/ui/widgets.c')` to the `extra` list in `tools/build-userland.py` next to `ui.c`.

- [ ] **Step 4: Run tests**

Run: `cd tools && python3 -B -m unittest test_ui_library`
Expected: OK.

- [ ] **Step 5: Commit**

```bash
git add userland/ui/widgets.h userland/ui/widgets.c userland/ui/test_widgets.c tools/test_ui_library.py tools/build-userland.py
git commit -m "Add toolkit scroll area with overlay scrollbar, wheel and thumb drag"
```

---

### Task 5: List view

**Files:** Modify `userland/ui/widgets.h`, `widgets.c`, `test_widgets.c`

**Interfaces:**
- Consumes: `struct zui_scroll` (Task 4), `zui_icon` (Task 3), `zui_add` (Task 1).
- Produces:

```c
struct zui_list_item { const char *icon,*label,*subtitle; int chevron,enabled; };
struct zui_list { struct zui_scroll scroll; int selected,count,row_height,base_id,first_control,visible_controls; };
/* Draws rows [visible only], registers each visible row as ZUI_NAVIGATION with id base_id+index.
 * items(context,index,&item) supplies rows lazily so 10,000-row lists cost only visible rows. */
void zui_list_draw(struct zui *ui,struct zui_list *l,struct zui_rect rect,int count,
                   int (*items)(void *context,int index,struct zui_list_item *out),void *context);
/* Returns index+1 when a row is activated (click or Enter), else 0. Handles Up/Down/Home/End,
 * first-letter type-ahead and wheel/scrollbar via the embedded scroll. */
int zui_list_event(struct zui *ui,struct zui_list *l,const struct zenith_gui_event *e,
                   int (*items)(void *context,int index,struct zui_list_item *out),void *context);
```

Row height defaults to 40 (36 + 4 gap) when `row_height` is 0.

- [ ] **Step 1: Write the failing test** — add to `test_widgets.c` and call from `main`:

```c
static const char *names[]={"Alpha","Bravo","Charlie"};
static int rows(void *c,int i,struct zui_list_item *o){
    int n=*(int*)c;if(i<0||i>=n)return 0;
    *o=(struct zui_list_item){i%2?"chevron-right":"zap",names[i%3],0,1,i!=1};return 1;
}
static void list_tests(void){
    struct zui u;assert(zui_init(&u,px,800,600));u.window_focused=1;
    struct zui_list l={0};l.base_id=1000;int n=0;
    zui_list_draw(&u,&l,(struct zui_rect){0,0,300,400},n,rows,&n);assert(u.count==0);
    n=10000;u.count=0;zui_list_draw(&u,&l,(struct zui_rect){0,0,300,400},n,rows,&n);
    assert(u.count==10&&l.scroll.content==400000);                    /* only visible rows */
    struct zenith_gui_event k={Z_GUI_KEY,0,0,0,0,11};                  /* End */
    zui_list_event(&u,&l,&k,rows,&n);assert(l.selected==9999&&l.scroll.offset==400000-400);
    k.extra=10;zui_list_event(&u,&l,&k,rows,&n);assert(l.selected==0&&l.scroll.offset==0);
    k.extra=8;zui_list_event(&u,&l,&k,rows,&n);assert(l.selected==2);  /* Down skips disabled row 1 */
    k=(struct zenith_gui_event){Z_GUI_KEY,'b',0,0,0,0};zui_list_event(&u,&l,&k,rows,&n);
    assert(l.selected==4);                                             /* next enabled "Bravo" */
    k=(struct zenith_gui_event){Z_GUI_KEY,0,0,0,0,2};assert(zui_list_event(&u,&l,&k,rows,&n)==5);
    u.count=0;l.scroll.offset=0;zui_list_draw(&u,&l,(struct zui_rect){0,0,300,400},n,rows,&n);
    struct zenith_gui_event d={Z_GUI_DOWN,1,50,85,0,0},up={Z_GUI_UP,1,50,85,0,0};
    zui_event(&u,&d);assert(zui_list_event(&u,&l,&up,rows,&n)==3);    /* third row clicked */
}
```

- [ ] **Step 2: Run to verify failure** — `cd tools && python3 -B -m unittest test_ui_library` → FAIL: unknown type `struct zui_list`.

- [ ] **Step 3: Implement** — add the declarations to `widgets.h`; append to `widgets.c`:

```c
static int row_h(const struct zui_list *l){return l->row_height>0?l->row_height:40;}
static unsigned long len64(const char *s){unsigned long n=0;while(s&&n<256&&s[n])n++;return n;}
void zui_list_draw(struct zui *u,struct zui_list *l,struct zui_rect r,int count,
                   int (*items)(void*,int,struct zui_list_item*),void *ctx){
    int h=row_h(l);l->count=count<0?0:count;
    if(l->selected>=l->count)l->selected=l->count?l->count-1:0;
    int top=zui_scroll_begin(u,&l->scroll,r);
    l->first_control=u->count;l->visible_controls=0;
    int first=l->scroll.offset/h;
    for(int i=first;i<l->count;i++){
        int y=top+i*h;if(y>=r.y+r.h)break;
        struct zui_list_item it;if(!items(ctx,i,&it))continue;
        struct zui_rect row={r.x,y,r.w-12,h-4};
        if(zui_add(u,l->base_id+i,ZUI_NAVIGATION,row,it.label?it.label:"",i==l->selected,it.enabled))l->visible_controls++;
        int role=!it.enabled?ZUI_ICON_DISABLED:ZUI_ICON_NORMAL;
        if(it.icon)zui_icon(u,it.icon,row.x+10,row.y+(row.h-24)/2,24,role);
        if(it.subtitle)zui_text(u,row.x+44,row.y+row.h-16,it.subtitle,len64(it.subtitle),&zui_caption,u->theme.secondary);
    }
    zui_scroll_end(u,&l->scroll,l->count*h);
}
static int enabled_at(int (*items)(void*,int,struct zui_list_item*),void *ctx,int i){
    struct zui_list_item it;return items(ctx,i,&it)&&it.enabled;
}
static int lower(int c){return c>='A'&&c<='Z'?c+32:c;}
int zui_list_event(struct zui *u,struct zui_list *l,const struct zenith_gui_event *e,
                   int (*items)(void*,int,struct zui_list_item*),void *ctx){
    int h=row_h(l),old=l->selected;
    if(zui_scroll_event(u,&l->scroll,e))return 0;
    if(e->type==Z_GUI_UP){
        for(int i=0;i<l->visible_controls;i++){
            int c=l->first_control+i;if(c>=u->count)break;
            struct zui_control *k=&u->controls[c];
            struct zui_rect area=zui_control_area(u,c);
            if(k->enabled&&e->x>=area.x&&e->y>=area.y&&e->x-area.x<area.w&&e->y-area.y<area.h){
                l->selected=k->id-l->base_id;u->dirty=1;return l->selected+1;
            }
        }
        return 0;
    }
    if(e->type!=Z_GUI_KEY||(e->modifiers&6)||!l->count)return 0;
    if(e->extra==2)return enabled_at(items,ctx,l->selected)?l->selected+1:0;
    int step=e->extra==8?1:e->extra==7?-1:0,at=l->selected;
    if(e->extra==10){at=-1;step=1;}else if(e->extra==11){at=l->count;step=-1;}
    if(step){
        for(int i=at+step;i>=0&&i<l->count;i+=step)if(enabled_at(items,ctx,i)){l->selected=i;break;}
    }else if(e->code>32&&e->code<127){
        for(int k=1;k<=l->count;k++){
            int i=(l->selected+k)%l->count;struct zui_list_item it;
            if(items(ctx,i,&it)&&it.enabled&&it.label&&lower(it.label[0])==lower(e->code)){l->selected=i;break;}
        }
    }
    if(l->selected!=old){zui_scroll_reveal(&l->scroll,l->selected*h,h);u->dirty=1;}
    return 0;
}
```

- [ ] **Step 4: Run tests** — `cd tools && python3 -B -m unittest test_ui_library` → OK.
- [ ] **Step 5: Commit** — `git add userland/ui && git commit -m "Add virtualized toolkit list view with keyboard and type-ahead"`

---

### Task 6: Combo box

**Files:** Modify `userland/ui/widgets.h`, `widgets.c`, `test_widgets.c`

**Interfaces:**
- Consumes: `struct zui_list` (Task 5).
- Produces:

```c
struct zui_combo { int id,open,value; struct zui_list popup; struct zui_rect field,panel; };
/* Draws the closed field (registered as ZUI_BUTTON with id) and, when open, the popup panel
 * kept inside the window (opens upward when there is no room below). */
void zui_combo_draw(struct zui *ui,struct zui_combo *c,struct zui_rect field,int count,
                    int (*items)(void*,int,struct zui_list_item*),void *context);
/* Returns value+1 when a choice is committed; Esc/click outside cancels. Open combos
 * consume all pointer/key events. */
int zui_combo_event(struct zui *ui,struct zui_combo *c,const struct zenith_gui_event *e,
                    int (*items)(void*,int,struct zui_list_item*),void *context);
```

Popup: at most 8 rows of 40 px; `panel` = field width, placed below `field` if it fits in `ui->height`, else above; clamped to the window.

- [ ] **Step 1: Write the failing test** — add and call from `main`:

```c
static void combo_tests(void){
    struct zui u;assert(zui_init(&u,px,800,600));u.window_focused=1;int n=20;
    struct zui_combo c={0};c.id=50;c.popup.base_id=2000;c.value=3;
    zui_combo_draw(&u,&c,(struct zui_rect){100,560,200,36},n,rows,&n);
    struct zenith_gui_event d={Z_GUI_DOWN,1,150,570,0,0},up={Z_GUI_UP,1,150,570,0,0};
    zui_event(&u,&d);assert(zui_combo_event(&u,&c,&up,rows,&n)==0&&c.open);
    assert(c.popup.selected==3);
    u.count=0;zui_combo_draw(&u,&c,(struct zui_rect){100,560,200,36},n,rows,&n);
    assert(c.panel.y+c.panel.h<=560&&c.panel.y>=0&&c.panel.h==328);   /* opens upward */
    struct zenith_gui_event k={Z_GUI_KEY,0,0,0,0,8};zui_combo_event(&u,&c,&k,rows,&n);
    k.extra=2;assert(zui_combo_event(&u,&c,&k,rows,&n)==5&&!c.open&&c.value==4);
    zui_event(&u,&d);zui_combo_event(&u,&c,&up,rows,&n);assert(c.open);
    k.extra=1;assert(zui_combo_event(&u,&c,&k,rows,&n)==0&&!c.open&&c.value==4); /* Esc keeps value */
}
```

- [ ] **Step 2: Run to verify failure** — FAIL: unknown type `struct zui_combo`.
- [ ] **Step 3: Implement** — declarations to `widgets.h`; append to `widgets.c`:

```c
void zui_combo_draw(struct zui *u,struct zui_combo *c,struct zui_rect f,int count,
                    int (*items)(void*,int,struct zui_list_item*),void *ctx){
    struct zui_list_item it={0};c->field=f;
    const char *label=items(ctx,c->value,&it)&&it.label?it.label:"";
    zui_add(u,c->id,ZUI_BUTTON,f,label,0,1);
    zui_icon(u,"chevron-down",f.x+f.w-28,f.y+(f.h-16)/2,16,ZUI_ICON_TEXT);
    if(!c->open)return;
    int rows=count<8?count:8,h=rows*40+8;
    int y=f.y+f.h+4<=u->height-h?f.y+f.h+4:f.y-4-h;if(y<0)y=0;
    c->panel=(struct zui_rect){f.x,y,f.w,h};
    zui_surface(u,c->panel,10,ZUI_SURFACE_POPOVER);
    zui_list_draw(u,&c->popup,(struct zui_rect){f.x+4,y+4,f.w-8,h-8},count,items,ctx);
}
static int within(struct zui_rect r,int x,int y){return x>=r.x&&y>=r.y&&x-r.x<r.w&&y-r.y<r.h;}
int zui_combo_event(struct zui *u,struct zui_combo *c,const struct zenith_gui_event *e,
                    int (*items)(void*,int,struct zui_list_item*),void *ctx){
    if(!c->open){
        int opened=(e->type==Z_GUI_UP&&within(c->field,e->x,e->y))||
            (e->type==Z_GUI_KEY&&e->extra==2&&u->focus>=0&&u->focus<u->count&&u->controls[u->focus].id==c->id);
        if(opened){c->open=1;c->popup.selected=c->value;c->popup.scroll.offset=0;
            zui_scroll_reveal(&c->popup.scroll,c->value*40,40);u->dirty=1;}
        return 0;
    }
    if(e->type==Z_GUI_KEY&&e->extra==1){c->open=0;u->dirty=1;return 0;}
    if(e->type==Z_GUI_UP&&!within(c->panel,e->x,e->y)){c->open=0;u->dirty=1;return 0;}
    int chosen=zui_list_event(u,&c->popup,e,items,ctx);
    if(chosen){c->value=chosen-1;c->open=0;u->dirty=1;return chosen;}
    return 0;
}
```

- [ ] **Step 4: Run tests** → OK.
- [ ] **Step 5: Commit** — `git commit -am "Add toolkit combo box with window-bounded popup list"`

---

### Task 7: Form layout

**Files:** Modify `userland/ui/widgets.h`, `widgets.c`, `test_widgets.c`

**Interfaces:**
- Produces:

```c
struct zui_form { struct zui_rect area; int y,label_w,narrow,x,w; };
/* Starts a centered column inside area (max 640 px wide); content_top is the scrolled y origin. */
void zui_form_begin(struct zui_form *f,struct zui_rect area,int content_top,int window_width);
/* Draws a section heading; returns its content-relative top. */
int zui_form_section(struct zui *ui,struct zui_form *f,const char *title);
/* Lays out one row: draws label (+help), returns the control rect of the requested height.
 * Wide: label right-aligned in a 40% column. Narrow (window < 720): label above control. */
struct zui_rect zui_form_row(struct zui *ui,struct zui_form *f,const char *label,const char *help,int control_w,int control_h);
/* Total laid-out height so far, for zui_scroll_end. */
int zui_form_height(const struct zui_form *f,int content_top);
```

- [ ] **Step 1: Write the failing test** — add and call:

```c
static void form_tests(void){
    struct zui u;assert(zui_init(&u,px,800,600));struct zui_form f;
    zui_form_begin(&f,(struct zui_rect){0,0,1000,600},0,1000);
    assert(f.w==640&&f.x==180);
    struct zui_rect r=zui_form_row(&u,&f,"Mode",0,200,36);
    assert(r.x==f.x+f.label_w+16&&r.y==0&&r.w==200);
    struct zui_rect r2=zui_form_row(&u,&f,"Accent","Help text",200,36);assert(r2.y>r.y+36);
    zui_form_begin(&f,(struct zui_rect){0,0,600,600},0,600);assert(f.narrow&&f.w==600);
    r=zui_form_row(&u,&f,"Mode",0,200,36);assert(r.x==f.x&&r.y==24);   /* label stacked above */
    r=zui_form_row(&u,&f,"Wide",0,2000,36);assert(r.w==f.w);            /* control clamped */
    assert(zui_form_height(&f,0)>r.y+36);
}
```

- [ ] **Step 2: Run to verify failure** — FAIL: unknown type `struct zui_form`.
- [ ] **Step 3: Implement** — declarations to `widgets.h`; append to `widgets.c`:

```c
void zui_form_begin(struct zui_form *f,struct zui_rect a,int top,int window_width){
    f->area=a;f->y=top;f->narrow=window_width<720;
    f->w=a.w<640?a.w:640;f->x=a.x+(a.w-f->w)/2;f->label_w=f->narrow?0:f->w*2/5;
}
int zui_form_section(struct zui *u,struct zui_form *f,const char *title){
    if(f->y>f->area.y)f->y+=16;
    int at=f->y;zui_text(u,f->x,f->y,title,len64(title),&zui_label,u->theme.text);
    zui_fill(u,(struct zui_rect){f->x,f->y+26,f->w,1},u->theme.border);
    f->y+=40;return at;
}
struct zui_rect zui_form_row(struct zui *u,struct zui_form *f,const char *label,const char *help,int cw,int ch){
    int width=f->narrow?f->w:f->w-f->label_w-16;if(cw>width)cw=width;if(cw<1)cw=1;
    struct zui_rect r;
    if(f->narrow){
        zui_text(u,f->x,f->y,label,len64(label),&zui_label,u->theme.text);
        r=(struct zui_rect){f->x,f->y+24,cw,ch};f->y+=24+ch;
    }else{
        int lw=zui_measure(label,len64(label),&zui_label);
        zui_text(u,f->x+f->label_w-lw,f->y+(ch-zui_label.height)/2,label,len64(label),&zui_label,u->theme.text);
        r=(struct zui_rect){f->x+f->label_w+16,f->y,cw,ch};f->y+=ch;
    }
    if(help){zui_text(u,r.x,f->y+4,help,len64(help),&zui_caption,u->theme.secondary);f->y+=22;}
    f->y+=12;return r;
}
int zui_form_height(const struct zui_form *f,int top){return f->y-top+12;}
```

- [ ] **Step 4: Run tests** → OK.
- [ ] **Step 5: Commit** — `git commit -am "Add toolkit form layout with centered column and narrow stacking"`

---

### Task 8: Footer bar and dialog

**Files:** Modify `userland/ui/widgets.h`, `widgets.c`, `test_widgets.c`

**Interfaces:**
- Produces:

```c
enum { ZUI_FOOTER_HELP=1,ZUI_FOOTER_DEFAULTS=2,ZUI_FOOTER_RESET=3,ZUI_FOOTER_APPLY=4 };
/* Registers buttons with ids base_id+ZUI_FOOTER_*; Help only when help!=0.
 * Apply/Reset enabled iff changed; Defaults enabled iff !at_defaults. Returns bar height (56). */
int zui_footer(struct zui *ui,struct zui_rect bar,int base_id,int help,int changed,int at_defaults);
struct zui_dialog { int open,base_id; const char *title,*message,*labels[3]; int count; };
/* Draws dimmed window + card; buttons get ids base_id+1..count. Call last in a frame. */
void zui_dialog_draw(struct zui *ui,const struct zui_dialog *d);
/* While open: traps Tab within dialog buttons, Esc returns the last button (Cancel),
 * clicks outside the card are ignored. Returns chosen button index+1 and closes. */
int zui_dialog_event(struct zui *ui,struct zui_dialog *d,const struct zenith_gui_event *e);
```

- [ ] **Step 1: Write the failing test** — add and call:

```c
static int find(const struct zui *u,int id){for(int i=0;i<u->count;i++)if(u->controls[i].id==id)return i;return -1;}
static void footer_dialog_tests(void){
    struct zui u;assert(zui_init(&u,px,800,600));u.window_focused=1;
    for(int changed=0;changed<2;changed++)for(int def=0;def<2;def++){
        u.count=0;assert(zui_footer(&u,(struct zui_rect){0,544,800,56},300,0,changed,def)==56);
        assert(find(&u,301)<0);
        assert(u.controls[find(&u,304)].enabled==changed&&u.controls[find(&u,303)].enabled==changed);
        assert(u.controls[find(&u,302)].enabled==!def);
    }
    u.count=0;struct zui_dialog d={1,400,"Unsaved changes","Apply them?",{"Apply","Discard","Cancel"},3};
    zui_add(&u,9,ZUI_BUTTON,(struct zui_rect){0,0,40,40},"behind",0,1);
    zui_dialog_draw(&u,&d);
    struct zenith_gui_event tab={Z_GUI_KEY,0,0,0,0,4};
    for(int i=0;i<7;i++){zui_dialog_event(&u,&d,&tab);assert(u.controls[u.focus].id>=401);}
    struct zenith_gui_event dn={Z_GUI_DOWN,1,10,10,0,0},up={Z_GUI_UP,1,10,10,0,0};
    zui_event(&u,&dn);assert(zui_dialog_event(&u,&d,&up)==0&&d.open);   /* behind is blocked */
    struct zenith_gui_event esc={Z_GUI_KEY,0,0,0,0,1};assert(zui_dialog_event(&u,&d,&esc)==3&&!d.open);
}
```

- [ ] **Step 2: Run to verify failure** — FAIL: implicit declaration of `zui_footer`.
- [ ] **Step 3: Implement** — declarations to `widgets.h`; append to `widgets.c`:

```c
int zui_footer(struct zui *u,struct zui_rect b,int base,int help,int changed,int at_defaults){
    zui_fill(u,(struct zui_rect){b.x,b.y,b.w,1},u->theme.border);
    int y=b.y+(b.h-36)/2,x=b.x+b.w-12;
    x-=112;zui_add(u,base+ZUI_FOOTER_APPLY,ZUI_BUTTON,(struct zui_rect){x,y,112,36},"Apply",changed,changed);
    x-=120;zui_add(u,base+ZUI_FOOTER_RESET,ZUI_BUTTON,(struct zui_rect){x,y,112,36},"Reset",0,changed);
    x-=120;zui_add(u,base+ZUI_FOOTER_DEFAULTS,ZUI_BUTTON,(struct zui_rect){x,y,112,36},"Defaults",0,!at_defaults);
    if(help)zui_add(u,base+ZUI_FOOTER_HELP,ZUI_BUTTON,(struct zui_rect){b.x+12,y,96,36},"Help",0,1);
    return 56;
}
void zui_dialog_draw(struct zui *u,const struct zui_dialog *d){
    if(!d->open)return;
    zui_clip(u,(struct zui_rect){0,0,u->width,u->height});
    zui_round_alpha(u,(struct zui_rect){0,0,u->width,u->height},0,0x000000,120);
    int w=u->width<440?u->width-24:416,h=168,x=(u->width-w)/2,y=(u->height-h)/2;
    zui_surface(u,(struct zui_rect){x,y,w,h},12,ZUI_SURFACE_POPOVER);
    zui_text(u,x+24,y+20,d->title,len64(d->title),&zui_label,u->theme.text);
    zui_text(u,x+24,y+52,d->message,len64(d->message),&zui_body,u->theme.secondary);
    int bx=x+w-24;
    for(int i=d->count-1;i>=0;i--){
        bx-=104;zui_add(u,d->base_id+i+1,ZUI_BUTTON,(struct zui_rect){bx,y+h-60,96,36},d->labels[i],i==0,1);bx-=8;
    }
}
int zui_dialog_event(struct zui *u,struct zui_dialog *d,const struct zenith_gui_event *e){
    if(!d->open)return 0;
    if(e->type==Z_GUI_KEY&&e->extra==1){d->open=0;u->dirty=1;return d->count;}
    if(e->type==Z_GUI_KEY&&e->extra==4){
        int first=-1,at=-1;
        for(int i=0;i<u->count;i++)if(u->controls[i].id>d->base_id&&u->controls[i].id<=d->base_id+d->count){
            if(first<0)first=i;if(i==u->focus)at=i;
        }
        if(first<0)return 0;
        int step=(e->modifiers&1)?-1:1,next=at<0?first:at+step;
        if(next<first)next=first+d->count-1;if(next>=first+d->count)next=first;
        u->focus=next;u->dirty=1;return 0;
    }
    int id=zui_event(u,e);
    if(id>d->base_id&&id<=d->base_id+d->count){d->open=0;u->dirty=1;return id-d->base_id;}
    if(e->type==Z_GUI_DOWN&&!(u->focus>=0&&u->controls[u->focus].id>d->base_id))u->pressed=-1;
    return 0;
}
```

(The dialog's buttons are added after all page controls, so they are contiguous;
`zui_event` hit-tests from the last control backwards, so the card wins over
anything behind it; the `Z_GUI_DOWN` line cancels presses that landed on controls
behind the dim layer.)

- [ ] **Step 4: Run tests** → OK.
- [ ] **Step 5: Commit** — `git commit -am "Add uniform toolkit footer bar and modal dialog"`

---

### Task 9: Hover state plumbing (host ABI deferred to stage 3)

**Files:** Modify `userland/ui/ui.h`, `ui.c`, `test_ui.c`

**Interfaces:**
- Produces: `#define Z_GUI_MOVE 9` in `ui.h` guarded by `#ifndef Z_GUI_MOVE` (the
  authoritative value moves to `zenith_graphics.h` in stage 3); `struct zui` gains
  `int hover,hover_supported;`. `zui_event` on `Z_GUI_MOVE` sets `hover` to the
  topmost enabled control index under (x,y), or -1 when x<0 or none; ignored unless
  `hover_supported`. `zui_draw_range` tints a hovered, unpressed, unselected
  button/navigation/segment fill with `blend(fill, theme.text, 16)`.

- [ ] **Step 1: Write the failing test** — append to `test_ui.c` `main`:

```c
    { /* hover only when supported; leave clears */
        static unsigned int px[100*60];struct zui b;assert(zui_init(&b,px,100,60));b.window_focused=1;
        assert(b.hover==-1);
        zui_add(&b,1,ZUI_BUTTON,(struct zui_rect){10,10,40,30},"h",0,1);
        struct zenith_gui_event m={Z_GUI_MOVE,0,20,20,0,0};
        zui_event(&b,&m);assert(b.hover==-1);
        b.hover_supported=1;zui_event(&b,&m);assert(b.hover==0&&b.dirty);
        zui_draw_controls(&b);unsigned int hovered=px[25*100+30];
        m.x=m.y=-1;zui_event(&b,&m);assert(b.hover==-1);
        zui_draw_controls(&b);assert(px[25*100+30]!=hovered);
    }
```

- [ ] **Step 2: Run to verify failure** — FAIL: `Z_GUI_MOVE` undeclared / no member `hover`.
- [ ] **Step 3: Implement** — in `ui.h` add before `struct zui`:

```c
#ifndef Z_GUI_MOVE
#define Z_GUI_MOVE 9 /* Stage 3 opt-in host event; ignored until hover_supported is set. */
#endif
```

and add `int hover,hover_supported;` to `struct zui`. In `zui_init` after
`u->pressed=-1;` add `u->hover=-1;`. At the top of `zui_event` after the null check:

```c
    if(e->type==Z_GUI_MOVE){
        if(!u->hover_supported)return 0;
        int h=-1;
        if(e->x>=0&&e->y>=0)for(int i=u->count-1;i>=0;i--)if(u->controls[i].enabled&&hit(u->controls[i].clip,e->x,e->y)){h=i;break;}
        if(h!=u->hover){u->hover=h;u->dirty=1;}
        return 0;
    }
```

Also reset `u->hover=-1` in the `Z_GUI_FOCUS`/`Z_GUI_RESET`/`Z_GUI_CLOSE` branches. In
`zui_draw_range`, after `if(!c->enabled){…}` add:

```c
        if(u->hover==i&&!pressed&&!selected&&c->enabled&&c->kind!=ZUI_SWATCH&&c->kind!=ZUI_TOGGLE)
            fill=blend(fill,u->theme.text,16);
```

and in the `ZUI_NAVIGATION` branch draw the row background when hovered too:
`if(selected||pressed||u->hover==i)zui_round(u,r,6,pressed?u->theme.raised:fill);`.

- [ ] **Step 4: Run tests** → OK (`cd tools && python3 -B -m unittest test_ui_library`).
- [ ] **Step 5: Commit** — `git commit -am "Track toolkit hover state behind an opt-in capability"`

---

### Task 10: Gallery demonstration, documentation and stage evidence

**Files:**
- Modify: `userland/ui-gallery/gallery.h`, `gallery.c`, `userland/ui/test_ui.c` (gallery image assertions)
- Modify: `docs/USERLAND-UI.md`, `docs/MERIDIAN-BEHAVIOR.md`, `docs/ROADMAP.md` (U1/D1 state)

**Interfaces:**
- Consumes: every primitive above.
- Produces: gallery page 2 (Right/Left switches pages) showing a list with icons, a
  combo, a scrolled form with sections, a footer and a dialog opened by its Apply.

- [ ] **Step 1: Write the failing test** — in `test_ui.c`, after existing gallery checks:

```c
    { /* gallery page 2 composes every stage-1 primitive */
        assert(event(Z_GUI_KEY,0,0,0,0,6)==0);assert(g.page==1);gallery_draw(&g);
        image(argv[1],"gallery-widgets");
        int icons=0,footer=0;
        for(int i=0;i<g.ui.count;i++){if(g.ui.controls[i].kind==ZUI_NAVIGATION)icons++;if(g.ui.controls[i].id>=GALLERY_FOOTER)footer++;}
        assert(icons>=5&&footer==3);
    }
```

- [ ] **Step 2: Run to verify failure** — FAIL: no member `page` / `GALLERY_FOOTER` undeclared.
- [ ] **Step 3: Implement** — in `gallery.h` add `int page; struct zui_list list; struct zui_combo combo; struct zui_scroll scroll; struct zui_dialog dialog; int toggle_a,toggle_b;` to `struct gallery` and `#define GALLERY_FOOTER 900`; include `"../ui/widgets.h"`. In `gallery.c`:
  - Left/Right (extra 5/6) with no focused segment/swatch switches `page` 0↔1 and sets `ui.dirty`.
  - Page 1 draw: `zui_workspace_begin(&g->ui,240)`; sidebar `zui_list_draw` over a static array of the ten Meridian category labels with the icon names from Task 2 (`zap keyboard usb network palette app-window layout-grid shield languages monitor-cog`, chevrons on all but the first); content: `zui_scroll_begin`, `zui_form_begin`, two `zui_form_section`s with a combo row ("Layout", items `us de gb fr`) and two toggle rows, `zui_scroll_end(…, zui_form_height(…))`; `zui_footer` with `GALLERY_FOOTER`, `changed` = any toggle differs from its initial value; `zui_dialog_draw` last.
  - Page 1 events: dialog first (`zui_dialog_event`), then combo, then list, then scroll, then `zui_event`; footer Apply opens the dialog `{"Apply changes","The gallery only previews controls.",{"Apply","Discard","Cancel"},3}`; Reset restores toggles.

  Update `docs/USERLAND-UI.md`: new "Client library v2 (stage 1)" section listing the
  primitives, the 128-region budget, per-control clip, icon roles/sizes, the Lucide
  source/license, and that hover and Page Up/Down await the stage-3 ABI. In
  `docs/MERIDIAN-BEHAVIOR.md` scroll area row, change "Page Up/Down, Home/End" to
  "Home/End (Page Up/Down with the stage-3 key codes)" and add `Page Up=12`,
  `Page Down=13` key extras to the Hover ABI section. Record stage-1 local evidence in
  the U1 roadmap row.

- [ ] **Step 4: Run all checks**

```bash
cd tools && python3 -B -m unittest test_ui_library test_ui_icons && cd ..
python3 -B tools/build-userland.py obj/stage1-gallery --program ui-gallery
python3 -B tools/build-userland.py obj/stage1-settings --program settings
python3 -B tools/check-documentation.py
python3 -B deploy/distributor/validate.py --controller-only
```

Expected: all OK; inspect `gallery-widgets.ppm` from a kept test directory (set
`TMPDIR` to a kept path) for icon tinting, scrollbar and footer.

- [ ] **Step 5: Commit**

```bash
git add userland docs tools
git commit -m "Show stage-1 toolkit primitives in the UI gallery and document client library v2"
```
