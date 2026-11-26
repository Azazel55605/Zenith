#!/usr/bin/env python3
"""Exercise a test-signed Nebula browser, its network resources and its on-disk profile in Zenith."""
import argparse
from contextlib import ExitStack
from functools import partial
import hashlib
import http.server
import importlib.util
import io
import json
import math
from pathlib import Path
from statistics import median
import subprocess
import sys
import threading
import time

from PIL import Image, ImageChops, ImageDraw, ImageFont
import boot_image
import guest_cpu_frame
import guest_netsurf_jpeg
import guest_netsurf_png
from tls_fixture import certificates, context

ROOT = Path(__file__).resolve().parents[1]
GLYPH_SAMPLES = (("?", "3f385f"), ("Ü", "3b4963"), ("ä", "4f445a"), ("©", "3c4e4b"))
PAGE_EDGE = "7c306e"
SOURCE_COMMENT = "<!-- SOURCE-7F18 -->"
RESPONSIVE_COLORS = ("304f93", "913455", "24714d")
FIELD_COLOR = "d5be77"
REM_COLOR = "8d5530"
MEASURE_COLOR = "5b3047"
GAP_COLOR = "487b26"
GRID_COLORS = ("265d78", "72863a", "aa4e28")
CARD_COLORS = ("43555f", "109820", "b05d3f")


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / filename)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def start_server(stack, served, requests, tls=None):
    class Handler(http.server.SimpleHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def do_GET(self):
            requests.append(self.path)
            return super().do_GET()

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), partial(Handler, directory=served))
    if tls:
        server.socket = tls.wrap_socket(server.socket, server_side=True)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    stack.callback(server.server_close)
    stack.callback(server.shutdown)
    return ("https" if tls else "http") + f"://10.0.2.2:{server.server_port}"


def fixture_file(directory, filename, color, title, label, target=None, external=False, extra_html=""):
    css = (f"html{{border-top:3px solid #{PAGE_EDGE}}}"
           "body{background:#fff;color:#14212a;margin:24px}h1{font-size:28px}"
           f".swatch{{display:block;background-color:#{color};color:white;font-size:20px;"
           "font-weight:bold;padding:16px;width:238px;height:50px;text-decoration:none}")
    if external:
        (directory / "theme.css").write_text(css)
        style = '<link rel="stylesheet" href="/theme.css">'
    else:
        style = f"<style>{css}</style>"
    element = f'<a class="swatch" href="{target}">{label}</a>' if target else f'<p class="swatch">{label}</p>'
    comment = SOURCE_COMMENT + "\n" if external else ""
    (directory / filename).write_text(comment + f'<!doctype html><html><head><meta charset="utf-8"><title>{title}</title>{style}</head>'
                                      f'<body><h1>{title}</h1>{element}'
                                      f'{extra_html}</body></html>\n')


def glyph_examples():
    cells = "".join(f'<td style="width:40px;height:32px;background:#{color}">&#{ord(char)};</td>'
                    for char, color in GLYPH_SAMPLES)
    return f'<table style="font:20px monospace;color:white;text-align:center"><tr>{cells}</tr></table>'


def responsive_fixture(served):
    narrow, medium, wide = RESPONSIVE_COLORS
    (served / "responsive.html").write_text(
        '<!doctype html><meta charset="utf-8"><title>Responsive form</title>'
        '<link rel="stylesheet" href="/responsive.css"><form action="/submitted.html" method="get">'
        '<div class="field"><input aria-label="Draft" name="draft" autocomplete="off"></div>'
        '<button type="submit">Submit responsive form</button></form>'
        '<div class="grid">' + ''.join(f'<div style="background:#{color}"></div>' for color in GRID_COLORS) + '</div>'
        '<div class="cards"><div class="first"></div><div class="second"></div><div class="third"></div></div>'
        '<div class="rem"></div><div class="measure"></div><div class="gap-reference"></div>')
    (served / "responsive.css").write_text(
        f'html{{border-top:3px solid #{PAGE_EDGE}}} body{{margin:24px}}'
        f'.field{{width:270px;padding:10px;background:#{FIELD_COLOR}}} input{{width:200px;height:24px}}'
        f'button{{display:block;width:240px;height:60px;color:white;background:#{narrow};'
        'font:bold 18px sans-serif;border:0;margin-top:12px}'
        f'@media (min-width:620px){{button{{width:300px;background:#{medium}}}}}'
        f'@media (min-width:900px){{button{{width:360px;background:#{wide}}}}}'
        '.grid{display:grid;grid-template-columns:1fr 2fr;width:calc(120px + 120px);margin-top:8px}'
        '.grid>div{height:16px}'
        ':root{--card-tracks:repeat(auto-fit,minmax(270px,1fr));--first-tint:red;--brand:120deg 100% 50%;'
        f'--third-tint:#{CARD_COLORS[2]}}}'
        '.cards{display:grid;grid-template-columns:var(--card-tracks);'
        '--first-tint:rgb(207 232 220 / 20%);gap:16px;background:#203040;margin-top:12px}'
        '.cards>div{height:12px}.first{background-color:var(--first-tint)}'
        '.second{background-color:var(--unset-tint,hsl(var(--brand) / 50%))}'
        '.third{background-color:var(--third-tint)}'
        f'html{{font-size:20px}} .rem{{width:2rem;height:6px;background:#{REM_COLOR}}}'
        '@media(min-width:620px){html{font-size:30px}}'
        f'.measure{{width:240px;height:12px;background:#{MEASURE_COLOR}}}'
        f'.gap-reference{{width:16px;height:16px;background:#{GAP_COLOR}}}')
    fixture_file(served, "submitted.html", "975414", "Submitted form", "Draft received")


def check_responsive(mouse, monitor, inputs, login, click, keep, origin, requests):
    inputs.monitor_text(monitor, "sendkey ctrl-l 50")
    login.monitor_type(monitor, origin + "/responsive.html\n")

    def observe(name, layout):
        color = RESPONSIVE_COLORS[layout]
        submit = rendered_swatch(mouse, keep, name, color)
        with Image.open(keep / (name + ".png")) as frame:
            edge = color_mask(frame, bytes.fromhex(PAGE_EDGE)).getbbox()
            field = color_mask(frame, bytes.fromhex(FIELD_COLOR)).getbbox()
            button = color_mask(frame, bytes.fromhex(color)).getbbox()
            cards = [color_mask(frame, bytes.fromhex(c)).getbbox() for c in CARD_COLORS]
            if not all(cards):
                raise RuntimeError(f"Missing responsive Grid cards or alpha blend on {name}: {cards}")
            previous_row = color_mask(frame, bytes.fromhex(GRID_COLORS[2])).getbbox()
            measure = color_mask(frame, bytes.fromhex(MEASURE_COLOR)).getbbox()
            gap = color_mask(frame, bytes.fromhex(GAP_COLOR)).getbbox()
            # The clean Zenith profile uses 90 DPI, giving 225 device pixels for
            # 240 CSS px. Compare spaces against independent, plainly sized boxes.
            if (not measure or measure[2] - measure[0] != 225 or not gap or
                    (gap[2] - gap[0], gap[3] - gap[1]) != (15, 15)):
                raise RuntimeError(f"Missing 90-DPI CSS length references on {name}: {measure}, {gap}")
            if not previous_row or cards[0][1] - previous_row[3] != measure[3] - measure[1]:
                raise RuntimeError(f"Adjacent Grid containers lost their 12px margin on {name}: {previous_row}, {cards[0]}")
            for i, card in enumerate(cards):
                columns = layout + 1
                col = i % columns
                if col:
                    if card[0] != cards[i - 1][2] + gap[2] - gap[0] or card[1] != cards[i - 1][1]:
                        raise RuntimeError(f"Unexpected Grid column geometry on {name}: {cards}")
                elif i and (card[0] != cards[0][0] or card[1] != cards[i-columns][3] + gap[3] - gap[1]):
                    raise RuntimeError(f"Unexpected Grid row geometry on {name}: {cards}")
        if not edge or not field or not button:
            raise RuntimeError(f"Missing page edge or responsive form on {name}")
        return submit, edge, field, button[2] - button[0]

    _, original, field, narrow = observe("form-rail", 0)
    if requests.count("/responsive.html") != 1 or requests.count("/responsive.css") != 1:
        raise RuntimeError("Expected a single initial request for the form and its stylesheet")
    before = requests.copy()
    click(field[0] + 40, (field[1] + field[3]) // 2)
    login.monitor_type(monitor, "preserved")
    inputs.monitor_text(monitor, "sendkey ctrl-shift-s 50")
    _, hidden, _, middle = observe("form-hidden", 1)

    # Find overlays the bottom of the page. Observe it before changing zoom,
    # then require both the new media colour and the scaled root-relative width.
    with Image.open(keep / "form-hidden.png") as baseline:
        overlay_area = (hidden[0] + 8, hidden[1] + 360, hidden[2] - 8, hidden[1] + 398)
        baseline_strip = baseline.crop(overlay_area)
    def find_visible(frame):
        changed = ImageChops.difference(frame.crop(overlay_area), baseline_strip).convert("L").histogram()
        return sum(changed[12:]) > baseline_strip.width * baseline_strip.height // 3
    def rem_width(frame):
        marker = color_mask(frame, bytes.fromhex(REM_COLOR)).getbbox()
        return marker[2] - marker[0] if marker else 0
    # A rem follows the browser's configured DPI. Use the actual narrow and
    # wide root sizes; the unit test also pins results at exactly 96 DPI.
    with Image.open(keep / "form-rail.png") as frame:
        narrow_rem = rem_width(frame)
    with Image.open(keep / "form-hidden.png") as frame:
        wide_rem = rem_width(frame)
    if narrow_rem <= 0 or abs(wide_rem - 1.5 * narrow_rem) > 1.5:
        raise RuntimeError(f"2rem did not track the 20px and 30px root styles: {narrow_rem}, {wide_rem}")
    inputs.monitor_text(monitor, "sendkey ctrl-f 50")
    rendered_swatch(mouse, keep, "form-find", RESPONSIVE_COLORS[1],
                    image_check=lambda frame: find_visible(frame) and rem_width(frame) == wide_rem)
    inputs.monitor_text(monitor, "sendkey ctrl-equal 50")
    rendered_swatch(mouse, keep, "form-find-zoom", RESPONSIVE_COLORS[0],
                    image_check=lambda frame: find_visible(frame) and abs(rem_width(frame) - 1.1 * narrow_rem) <= 1)
    inputs.monitor_text(monitor, "sendkey esc 50")
    inputs.monitor_text(monitor, "sendkey ctrl-0 50")
    rendered_swatch(mouse, keep, "form-find-reset", RESPONSIVE_COLORS[1],
                    image_check=lambda frame: not find_visible(frame) and rem_width(frame) == wide_rem)
    print("PASS open Find preserves the viewport, zoom updates live media and 2rem geometry, reset restores the page", flush=True)

    inputs.monitor_text(monitor, "sendkey ctrl-shift-s 50")
    _, shown, _, restored_width = observe("form-shown", 0)
    if hidden[2] - hidden[0] <= original[2] - original[0] or shown != original or restored_width != narrow:
        raise RuntimeError("The sidebar did not change and restore the page inset")

    def maximize_button(edge):
        # Default Zenith chrome: the page ends 12 px inside the right edge
        # and begins 64 px below the client top. Maximize is at (-58,-22).
        click(edge[2] + 12 - 58, edge[1] - 64 - 22)
    maximize_button(shown)
    _, maximized, _, largest = observe("form-maximized", 2)
    maximize_button(maximized)
    submit, restored, _, last_width = observe("form-restored", 0)
    if not (narrow < middle < largest) or restored != original or last_width != narrow:
        raise RuntimeError("Responsive control geometry did not follow both viewport changes")
    with Image.open(keep / "form-restored.png") as frame:
        tiles = [color_mask(frame, bytes.fromhex(color)).getbbox() for color in GRID_COLORS]
        measure = color_mask(frame, bytes.fromhex(MEASURE_COLOR)).getbbox()
        if not all(tiles):
            raise RuntimeError("Missing painted CSS Grid tiles")
        a, b, c = tiles
        if not (b[2] - a[0] == measure[2] - measure[0] and a[1] == b[1] and a[2] == b[0] and c[0] == a[0] and c[1] == a[3]
                and c[2] == a[2] and abs((b[2] - b[0]) - 2 * (a[2] - a[0])) <= 2):
            raise RuntimeError(f"CSS Grid width did not resolve to 240 px with 1fr / 2fr and an automatic second row: {tiles}")
    print("PASS calculated 240px Grid, fractions, gaps and 1/2/3 auto-fit cards with inherited tracks, scoped colors and alpha", flush=True)
    if requests != before:
        raise RuntimeError("Resizing or toggling the sidebar made a new HTTP request")
    click(*submit)
    rendered_swatch(mouse, keep, "form-submitted", "975414")
    submission = [path for path in requests[len(before):] if path != "/favicon.ico"]
    if submission != ["/submitted.html?draft=preserved"]:
        raise RuntimeError("Submitting the form did not return the original unsaved input")
    print("PASS responsive sidebar and maximize/restore updated CSS without requests and preserved the form draft",
          flush=True)


def color_mask(frame, rgb):
    masks = [band.point([255 if i == target else 0 for i in range(256)])
             for band, target in zip(frame.split(), rgb)]
    return ImageChops.multiply(ImageChops.multiply(masks[0], masks[1]), masks[2])


def source_comment_mask():
    font = ImageFont.truetype(ROOT / "Resources/Fonts/Hack-Regular.ttf", 14, layout_engine=ImageFont.Layout.BASIC)
    ascent, descent = font.getmetrics()
    advance = math.ceil(font.getlength("M"))
    mask = Image.new("L", (len(SOURCE_COMMENT) * advance, ascent + descent))
    draw = ImageDraw.Draw(mask)
    for i, char in enumerate(SOURCE_COMMENT):
        draw.text((i * advance + 1, ascent), char, font=font, anchor="ls", fill=255)
    return mask


def source_comment_visible(frame, origin, mask):
    x, y = origin
    sample = frame.crop((x, y, x + mask.width, y + mask.height)).convert("L")
    bg = sample.getpixel((0, 0))
    contrast = ImageChops.difference(sample, Image.new("L", sample.size, bg))
    peak = contrast.getextrema()[1]
    if peak < 45:
        return False
    expected = mask.point(lambda alpha: 255 if alpha >= 128 else 0)
    actual = contrast.point(lambda value: 255 if value * 2 >= peak else 0)
    difference = ImageChops.difference(expected, actual).histogram()[255]
    return difference <= expected.histogram()[255] // 16


def read_source(mouse, monitor, inputs, keep, page):
    edge = color_mask(page, bytes.fromhex(PAGE_EDGE)).getbbox()
    if not edge:
        raise RuntimeError("Could not locate the HTTP document before opening Page source")
    x, y = edge[:2]
    # First line of raw HTML is a comment. It is invisible on the rendered page.
    origin, mask = (x + 12, y + 40), source_comment_mask()
    if source_comment_visible(page, origin, mask):
        raise RuntimeError("Source marker unexpectedly visible on the HTML page")
    inputs.monitor_text(monitor, "sendkey ctrl-u 50")
    deadline = time.monotonic() + 30
    path = keep / "page-source.ppm"
    while True:
        frame = Image.open(io.BytesIO(mouse.capture(path))).convert("RGB")
        if source_comment_visible(frame, origin, mask):
            frame.save(keep / "page-source.png")
            break
        if time.monotonic() >= deadline:
            raise RuntimeError(f"Missing literal HTML comment in Page source; screen retained at {path}")
        time.sleep(.12)
    inputs.monitor_text(monitor, "sendkey esc 50")


def glyphs_rendered(frame):
    # Compare the ink shapes without counting different placements of the
    # same question-mark fallback as distinct glyphs.
    shapes = set()
    for _, color in GLYPH_SAMPLES:
        box = color_mask(frame, bytes.fromhex(color)).getbbox()
        if not box:
            return False
        ink = frame.crop(box).convert("L").point(lambda value: 255 if value > 180 else 0)
        bounds = ink.getbbox()
        if not bounds or bounds[2] - bounds[0] > 15:
            return False
        glyph = ink.crop(bounds)
        shapes.add((glyph.size, glyph.tobytes()))
    return len(shapes) == len(GLYPH_SAMPLES)


def check_toolbar_icons(frame, page_left, page_top):
    icons = load("ns_ui_icons", "tools/build-ui-icons.py")
    for name, dx in (("arrow-left", 18), ("arrow-right", 58), ("lock", 142)):
        mask = icons.rasterize((ROOT / "userland/ui/icons" / (name + ".svg")).read_text(), 16)
        def matches(x, y):
            pixels = frame.crop((x, y, x + 16, y + 16)).convert("L").tobytes()
            base = median(v for v, alpha in zip(pixels, mask) if alpha == 0)
            ink = [abs(v - base) for v, alpha in zip(pixels, mask) if alpha >= 192]
            blank = [abs(v - base) for v, alpha in zip(pixels, mask) if alpha == 0]
            return min(ink) > 12 and median(ink) > max(blank)
        if not any(matches(page_left + dx + x, page_top - 34 + y)
                   for x in (-1, 0, 1) for y in (-1, 0, 1)):
            raise RuntimeError("Missing rendered standard toolbar icon: " + name)
    print("PASS Back, Forward and verified HTTPS lock matched their standard toolbar glyphs")


def menu_first_row(mouse, keep, name, before, anchor):
    """Observe the menu's filled first row and contrasting glyphs before activating it."""
    x, y = anchor
    box = (x + 12, y + 8, x + 155, y + 36)
    original = before.crop(box)
    deadline = time.monotonic() + 15
    while True:
        frame = Image.open(io.BytesIO(mouse.capture(keep / (name + ".ppm")))).convert("RGB")
        row = frame.crop(box)
        diff = ImageChops.difference(original, row).convert("L").histogram()
        colors = row.getcolors(row.width * row.height)
        uniform, bg = max(colors)
        if sum(diff[12:]) > row.width * row.height // 2 and uniform > row.width * row.height // 2:
            ink = sum(n for n, color in colors
                      if max(abs(a - b) for a, b in zip(color, bg)) > 90)
            if ink > 20:
                frame.save(keep / (name + ".png"))
                return x + 75, y + 22
        if time.monotonic() >= deadline:
            raise RuntimeError(f"Menu first row did not appear: {name}")
        time.sleep(.12)


def start_rule(frame, center, width):
    """Find the start page's 3 CSS px accent rule. Its raster height follows DPI."""
    for y in range(1, frame.height - 3):
        color = frame.getpixel((center, y))
        if frame.getpixel((center, y - 1)) == color or frame.getpixel((center, y + 1)) != color:
            continue
        thickness = 3 if frame.getpixel((center, y + 2)) == color else 2
        if frame.getpixel((center, y + thickness)) == color:
            continue
        row = frame.crop((0, y, frame.width, y + 1)).tobytes()
        run = bytes(color) * (width - 68)
        pos = row.find(run)
        if pos >= 0 and pos % 3 == 0 and not row.startswith(run + bytes(color) * 10, pos):
            return pos // 3, y + thickness
    return None


def first_start_tile(mouse, keep, name, center, width):
    """Find the first full-width link tile in the themed start-page content."""
    path = keep / (name + ".ppm")
    deadline = time.monotonic() + 30
    while True:
        frame = Image.open(io.BytesIO(mouse.capture(path))).convert("RGB")
        rule = start_rule(frame, center, width)
        run, start, last_fill = 0, 0, None
        left, top = rule if rule else (0, frame.height)
        for y in range(top, min(frame.height - 16, top + 400)):
            bg = frame.getpixel((left - 10, y))
            fill = frame.getpixel((left + width - 112, y))
            if fill != bg and frame.getpixel((left + width - 160, y)) == fill:
                if run == 0 or fill != last_fill:
                    run, start = 0, y
                run += 1
                last_fill = fill
                if run >= 22:
                    box = (left + 6, start, left + 150, y + 1)
                    sample = frame.crop(box)
                    ink = sum(n for n, color in sample.getcolors(sample.width * sample.height)
                              if max(abs(a - b) for a, b in zip(color, fill)) > 40)
                    if ink > 20:
                        frame.save(keep / (name + ".png"))
                        return left + 50, start + 15
            else:
                run = 0
        if time.monotonic() >= deadline:
            raise RuntimeError(f"Missing rendered start-page bookmark/history tile at {path}")
        time.sleep(.12)


def toggle_bookmark(mouse, monitor, inputs, keep, previous_frame, edge):
    """Wait for the star highlight to change before closing or navigating the window."""
    right, top = edge[2], edge[1]
    box = (right - 85, top - 40, right - 58, top - 13)
    before = Image.open(keep / (previous_frame + ".png")).convert("RGB").crop(box)
    inputs.monitor_text(monitor, "sendkey ctrl-d 50")
    deadline = time.monotonic() + 15
    while True:
        frame = Image.open(io.BytesIO(mouse.capture(keep / ("star-" + previous_frame + ".ppm")))).convert("RGB")
        if sum(ImageChops.difference(before, frame.crop(box)).convert("L").histogram()[5:]) > 160:
            return
        if time.monotonic() >= deadline:
            raise RuntimeError("Bookmark star did not change")
        time.sleep(.12)


def audit_profile(volume, url):
    def records(file):
        result = subprocess.run(["debugfs", "-R", "cat /root/" + file, volume],
                                capture_output=True, check=True, text=True)
        rows = result.stdout.splitlines()
        if not rows or rows[0] != "NEBULA-PROFILE-1":
            raise RuntimeError("Missing saved browser fixture profile " + file)
        return [row.split("\t") for row in rows[1:]]

    saved, removed = records("browser-saved"), records("browser-removed")
    bookmark = ["b", url, "Verified HTTPS"]
    history = ["h", url, "Verified HTTPS"]
    if saved.count(bookmark) != 1 or bookmark in removed or history not in saved or history not in removed:
        raise RuntimeError("Bookmark was not saved and removed independently of browsing history")


def audit_browser_payload(volume, payload):
    files = sorted(path for path in payload.rglob("*") if path.is_file())
    for path in files:
        guest_path = "/" + path.relative_to(payload).as_posix()
        result = subprocess.run(["debugfs", "-R", "cat " + guest_path, volume], capture_output=True, check=True)
        if result.stdout != path.read_bytes():
            raise RuntimeError("Installed browser payload differs at " + guest_path)
    print(f"PASS all {len(files)} signed payload files matched the installed ext2 volume byte-for-byte")


def rendered_swatch(mouse, keep, name, color, image_check=None):
    deadline = time.monotonic() + 60
    rgb = bytes.fromhex(color)
    path = keep / (name + ".ppm")
    while True:
        frame = Image.open(io.BytesIO(mouse.capture(path))).convert("RGB")
        mask = color_mask(frame, rgb)
        bounds = mask.getbbox()
        if bounds and (image_check is None or image_check(frame)):
            left, top, right, bottom = bounds
            if right-left >= 180 and bottom-top >= 40 and mask.histogram()[255] > 8000:
                # Text must be drawn *inside* the author's colored CSS box.
                # Choose a glyph on the link itself; block padding surrounds it.
                ink = frame.crop((left+3, top+3, right-3, bottom-3))
                white = [b.point([255 if i >= 235 else 0 for i in range(256)]) for b in ink.split()]
                white = ImageChops.multiply(ImageChops.multiply(white[0], white[1]), white[2])
                row_counts = [white.crop((0, y, white.width, y+1)).histogram()[255] for y in range(white.height)]
                if max(row_counts) > 20:
                    iy = row_counts.index(max(row_counts))
                    ix = [i for i in range(white.width) if white.getpixel((i, iy))]
                    print("PASS browser drew", name, "page and text" + (" with verified image pixels" if image_check else ""), flush=True)
                    frame.save(keep / (name + ".png"))
                    return left + 3 + ix[len(ix)//2], top + 3 + iy
        if time.monotonic() >= deadline:
            expected = "page, text and expected image pixels" if image_check else "page and text"
            raise RuntimeError(f"Missing {name} {expected}; screen retained at {path}")
        time.sleep(.12)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("keep", type=Path, help="new directory for screenshots, logs and fixture copies")
    parser.add_argument("--host", action="store_true", help="check the JPEG fixture and run the fetcher contract against the built core")
    parser.add_argument("--disk", type=Path, help="installed disposable Zenith disk with recovery Terminal")
    parser.add_argument("--elf", type=Path, help="exact ELF from this disk's build")
    parser.add_argument("--payload", type=Path, help="existing build-userland payload to package")
    parser.add_argument("--version", default="0.1.0")
    args = parser.parse_args()
    keep = args.keep.resolve()
    if args.host:
        core = ROOT / "obj/netsurf-core"
        source = core / "netsurf-all-3.11/netsurf"
        if not (core / "libnetsurf.a").is_file():
            parser.error("build the netsurf payload first to provide its upstream core")
        keep.mkdir(parents=True, exist_ok=False)
        guest_netsurf_jpeg.check_fixture(keep)
        print("PASS host JPEG fixture, decoded color blocks and reversed-block negative control", flush=True)
        exe = keep / "fetch-contract"
        subprocess.run(["cc", "-std=c11", "-D_DEFAULT_SOURCE", "-g", "-fsanitize=address,undefined",
                        "-Wl,--gc-sections", "-no-pie", "-Wall", "-Wextra", "-Werror", "-I", core / "include",
                        "-I", source, "-I", source / "include", ROOT / "tools/fixtures/netsurf-fetch.c",
                        ROOT / "userland/netsurf/fetch.c", core / "libnetsurf.a", "-o", exe], check=True)
        subprocess.run([exe], check=True)
        return
    if not args.disk or not args.elf:
        parser.error("guest validation requires --disk and its matching --elf")
    if len(str(keep / "desktop/serial.sock").encode()) > 100:
        parser.error("evidence path is too long for the QEMU monitor")
    keep.mkdir(parents=True, exist_ok=False)
    boot_image.verify(args.disk, args.elf)
    h = load("ns_vm", "tools/headless-smoke.py")
    login = load("ns_login", "tools/login-session-smoke.py")
    inputs = load("ns_input", "deploy/distributor/validation-input.py")
    terminal = load("ns_terminal", "deploy/distributor/validation-terminal.py")
    load("ns_accel", "deploy/distributor/validation-accelerator.py").install(h)
    inputs.install(h.Machine)

    payload = args.payload if args.payload else keep / "payload"
    if args.payload is None:
        subprocess.run([sys.executable, "-B", ROOT / "tools/build-userland.py", payload,
                        "--program", "netsurf", "--version", args.version], check=True)
    subprocess.run(["dotnet", "build", ROOT / "tools/Zenith.PackageTool", "-c", "Release", "--nologo"], check=True)
    tool = ROOT / "tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll"
    served = keep / "served"
    served.mkdir()

    def package(*args):
        subprocess.run(["dotnet", tool, *args], check=True)

    package("keygen", keep / "keys")
    key = (keep / "keys/public.pem").read_bytes()
    (served / "app-key").write_bytes(key)
    subprocess.run([sys.executable, "-B", ROOT / "tools/build-ui-icons.py", "--package", keep / "icons"], check=True)
    package("build", "zenith-icons", "0.1.0", keep / "icons", keep / "icons-bundle")
    package("seal", keep / "icons-bundle", keep / "keys/private.pem", served / "icons.zpk")
    package("build-native", "zenith-netsurf", args.version, payload, keep / "bundle", "--repo", "extra")
    package("seal", keep / "bundle", keep / "keys/private.pem", served / "browser.zpk")
    ca, fingerprint = certificates(keep / "tls")
    (served / "ns-ca.pem").write_bytes(ca)

    net = ["-netdev", "user,id=net0", "-device", "e1000e,netdev=net0"]
    images = load("ns_audit", "tools/desktop-launch-replay.py")
    monitor = keep / "monitor.sock"
    http_requests, https_requests = [], []
    with ExitStack() as resources:
        origin = start_server(resources, served, http_requests)
        secure = start_server(resources, served, https_requests, context(keep / "tls"))
        fixture_file(served, "local.html", "9635b2", "Local document", "Open HTTP page", origin + "/first.html",
                     extra_html=guest_netsurf_png.image_html())
        strip, asset_paths = guest_netsurf_png.external_strip(served)
        photo = guest_netsurf_jpeg.write_fixture(served)
        asset_paths.append("/" + guest_netsurf_jpeg.NAME)
        fixture_file(served, "first.html", "167b57", "HTTP styles", "Open secure page", secure + "/secure.html",
                     external=True, extra_html=strip + photo)
        fixture_file(served, "secure.html", "a74726", "Verified HTTPS", "Secure page loaded",
                     extra_html=glyph_examples())
        responsive_fixture(served)
        (served / "b").write_text("native run netsurf file:///root/ns-local.html | logger; echo NS_BROWSER_EXIT=$? | logger\n")
        profile_revision = 'for rev in /root/.nebula/r*; do latest=$rev; done\n'
        (served / "c").write_text(profile_revision + 'cat "$latest/data" > /root/browser-saved\n'
                                  'native run netsurf | logger; echo NS_REOPEN_EXIT=$? | logger\n')
        (served / "q").write_text(profile_revision + 'cat "$latest/data" > /root/browser-removed\npoweroff\n')
        setup = h.Machine(keep / "setup", disk=args.disk, argument="session=headless", network=net)
        try:
            if not setup.wait("headless ready") or not setup.wait("network: online address=10.0.2.15"):
                raise RuntimeError("Installed guest did not start its headless network session")
            def command(text, name):
                offset = len(setup.log())
                setup.send(text + "; echo " + name + "=$? | logger\r")
                if not setup.wait("user: " + name + "=", offset, timeout=150):
                    raise RuntimeError("Missing guest status for " + name)
                if not setup.wait("user: " + name + "=0", offset, timeout=1):
                    raise RuntimeError(f"{name} failed; see {setup.serial}")
            for remote, local in [("app-key", "ns-key"), ("ns-ca.pem", "ns-ca.pem"), ("local.html", "ns-local.html"),
                                  ("b", "b"), ("c", "c"), ("q", "q"), ("icons.zpk", "ns-icons.zpk"),
                                  ("browser.zpk", "ns-browser.zpk")]:
                command(f"recovery fetch --kernel --replace {origin}/{remote} /root/{local}", "NS_GET_" + remote.split(".")[0].replace("-", "_"))
            command("pkg trust /root/ns-key " + hashlib.sha256(key).hexdigest(), "NS_TRUST")
            command("tls trust /root/ns-ca.pem " + fingerprint, "NS_TLS")
            command("pkg install /root/ns-icons.zpk", "NS_ICONS")
            command("pkg install /root/ns-browser.zpk", "NS_INSTALL")
            setup.send("poweroff\r")
            if not setup.wait("all filesystems unmounted") or setup.process.wait(timeout=45):
                raise RuntimeError("Setup did not shut down cleanly")
        finally:
            setup.stop()
        machine = h.Machine(keep / "desktop", disk=setup.disk, argument="session=de",
                            network=net + ["-device", "virtio-gpu-pci", "-device", "virtio-keyboard-pci"], monitor=monitor)
        try:
            if not machine.wait("desktop ready (installed)"):
                raise RuntimeError("Graphical session failed to start")
            mouse = inputs.RenderedMouse(monitor, inputs.monitor_text)
            try:
                guest_cpu_frame.prompt(mouse, terminal, timeout=4)
            except RuntimeError:
                inputs.monitor_text(monitor, "sendkey ctrl-alt-shift-t 50")
                guest_cpu_frame.prompt(mouse, terminal)
            login.monitor_type(monitor, "sh /root/b\n")
            def click(x, y, button=1):
                (cx, cy), _ = mouse.position()
                mouse.command(f"mouse_move {x-cx} {y-cy}")
                mouse.command(f"mouse_button {button}")
                mouse.command("mouse_button 0")
            position = rendered_swatch(mouse, keep, "local", "9635b2", image_check=guest_netsurf_png.is_rendered)
            click(*position)
            def images_painted(frame):
                return guest_netsurf_png.strip_rendered(frame) and guest_netsurf_jpeg.rendered(frame)
            rendered_swatch(mouse, keep, "http", "167b57", image_check=images_painted)
            missing = set(asset_paths).difference(http_requests)
            if missing:
                raise RuntimeError("HTTP image requests missing: " + ", ".join(sorted(missing)))
            print(f"PASS all {len(asset_paths)} independent HTTP images fetched and painted in one browser process", flush=True)
            before_source = (http_requests.copy(), https_requests.copy())
            with Image.open(keep / "http.png") as page:
                read_source(mouse, monitor, inputs, keep, page)
            position = rendered_swatch(mouse, keep, "source-return", "167b57", image_check=images_painted)
            if (http_requests, https_requests) != before_source:
                raise RuntimeError("Viewing or closing page source requested another resource")
            print("PASS Ctrl+U showed the literal HTML comment and Escape restored the images without a request", flush=True)
            click(*position)
            rendered_swatch(mouse, keep, "https", "a74726", image_check=glyphs_rendered)
            print("PASS HTTPS document rendered distinct ?, Ü, ä, and © glyphs", flush=True)
            secure_frame = Image.open(keep / "https.png").convert("RGB")
            edge = color_mask(secure_frame, bytes.fromhex(PAGE_EDGE)).getbbox()
            if not edge or edge[2] - edge[0] < 400:
                raise RuntimeError("Cannot locate the document and its navigation toolbar")
            left, top = edge[:2]
            check_toolbar_icons(secure_frame, left, top)
            click(left + 26, top - 26, button=2)
            click(*menu_first_row(mouse, keep, "back-history", secure_frame, (left + 8, top - 4)))
            link = rendered_swatch(mouse, keep, "back-to-http", "167b57", image_check=guest_netsurf_png.strip_rendered)
            http_frame = Image.open(keep / "back-to-http.png").convert("RGB")
            click(*link, button=2)
            click(*menu_first_row(mouse, keep, "link-menu", http_frame, link))
            rendered_swatch(mouse, keep, "context-open-https", "a74726", image_check=glyphs_rendered)
            print("PASS Back history and Open link menus completed a second navigation", flush=True)
            if "/first.html" not in http_requests or "/theme.css" not in http_requests or "/secure.html" not in https_requests:
                raise RuntimeError("Page, stylesheet or TLS request missing from fixture server")
            toggle_bookmark(mouse, monitor, inputs, keep, "context-open-https", edge)
            inputs.monitor_text(monitor, "sendkey ctrl-q 30")
            if not machine.wait("user: NS_BROWSER_EXIT=0", timeout=30):
                raise RuntimeError("Browser did not close cleanly")
            guest_cpu_frame.prompt(mouse, terminal)
            relaunched = len(machine.log())
            login.monitor_type(monitor, "sh /root/c\n")
            if not machine.wait("NETSURF_DOCUMENT_PRESENTED", relaunched, 30):
                raise RuntimeError("Reopened browser did not present its start page")
            width = edge[2] - left
            center = left + width // 2
            bookmark = first_start_tile(mouse, keep, "restart-bookmark", center, width)
            click(*bookmark)
            rendered_swatch(mouse, keep, "bookmark-open", "a74726", image_check=glyphs_rendered)
            reopened = Image.open(keep / "bookmark-open.png").convert("RGB")
            reopened_edge = color_mask(reopened, bytes.fromhex(PAGE_EDGE)).getbbox()
            toggle_bookmark(mouse, monitor, inputs, keep, "bookmark-open", reopened_edge)
            inputs.monitor_text(monitor, "sendkey alt-home 50")
            recent = first_start_tile(mouse, keep, "removed-bookmark", center, width)
            if recent[1] <= bookmark[1] + 20:
                raise RuntimeError("The saved tile still occupies the Bookmarks section")
            inputs.monitor_text(monitor, "sendkey ctrl-l 50")
            login.monitor_type(monitor, "verified")
            inputs.monitor_text(monitor, "sendkey down 30")
            inputs.monitor_text(monitor, "sendkey ret 30")
            rendered_swatch(mouse, keep, "title-suggestion", "a74726", image_check=glyphs_rendered)
            print("PASS local history title suggestion opened the visited HTTPS page", flush=True)
            check_responsive(mouse, monitor, inputs, login, click, keep, origin, http_requests)
            inputs.monitor_text(monitor, "sendkey ctrl-q 30")
            if not machine.wait("user: NS_REOPEN_EXIT=0", timeout=30):
                raise RuntimeError("Reopened browser did not close cleanly")
            guest_cpu_frame.prompt(mouse, terminal)
            login.monitor_type(monitor, "sh /root/q\n")
            if not machine.wait("all filesystems unmounted") or machine.process.wait(timeout=45):
                raise RuntimeError("Desktop did not shut down cleanly")
        except BaseException:
            try:
                inputs.RenderedMouse(monitor, inputs.monitor_text).capture(keep / "failure.ppm")
            except (OSError, RuntimeError, ValueError):
                pass
            raise
        finally:
            (keep / "requests.json").write_text(json.dumps({"http": http_requests, "https": https_requests}, indent=2))
            machine.stop()
        images.audit(machine.disk, args.elf, keep)
        audit_browser_payload(keep / "root.ext2", payload)
        audit_profile(keep / "root.ext2", secure + "/secure.html")
        print("PASS bookmark saved, reopened from the start page after restart, and removed independently of history")
    print("PASS signed Nebula installation, PNG and JPEG, page source, local/HTTP/verified HTTPS navigation and independent ext2 audit")


if __name__ == "__main__":
    main()
