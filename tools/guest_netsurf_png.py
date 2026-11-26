"""PNG fixtures and independent guest-rendering checks for the browser gate."""
import base64
import io

from PIL import Image, ImageDraw


STRIP_SIZE = 12
STRIP_COLORS = [(40 + i * 13, 191 - i * 7, 68 + i * 9) for i in range(12)]


def external_strip(directory):
    """Use distinct URLs so twelve image loads cannot be served by a single fetch."""
    paths = []
    for i, color in enumerate(STRIP_COLORS):
        name = f"tile-{i:02}.png"
        Image.new("RGB", (STRIP_SIZE, STRIP_SIZE), color).save(directory / name)
        paths.append("/" + name)
    images = "".join(f'<img src="{path}" alt="">' for path in paths)
    return f"<p>{images}</p>", paths


def strip_rendered(frame):
    line = b"".join(bytes(color) * STRIP_SIZE for color in STRIP_COLORS)
    return contains_pixels(frame, [line] * STRIP_SIZE)


def contains_pixels(frame, rows):
    pixels = frame.tobytes()
    stride = frame.width * 3
    first = rows[0]
    start = pixels.find(first)
    while start >= 0:
        if (start % 3 == 0 and start % stride + len(first) <= stride and
                all(pixels.startswith(row, start + y * stride) for y, row in enumerate(rows))):
            return True
        start = pixels.find(first, start + 1)
    return False


def image_html():
    # Keep the alpha-bearing corner deliberately nonwhite in the encoded RGB.
    canvas = Image.new("RGBA", (36, 24), (241, 36, 151, 255))
    draw = ImageDraw.Draw(canvas)
    draw.rectangle((11, 0, 35, 8), fill=(18, 190, 221, 255))
    draw.rectangle((0, 9, 10, 23), fill=(43, 103, 24, 255))
    draw.rectangle((11, 9, 35, 23), fill=(79, 41, 171, 0))
    buffer = io.BytesIO()
    canvas.save(buffer, format="PNG")
    payload = base64.b64encode(buffer.getvalue()).decode("ascii")
    return f'<img src="data:image/png;base64,{payload}" alt="PNG fixture">'


def is_rendered(frame):
    """Require the asymmetric 36x24 original at native size, composited over white."""
    upper = bytes((241, 36, 151)) * 11 + bytes((18, 190, 221)) * 25
    lower = bytes((43, 103, 24)) * 11 + bytes((255, 255, 255)) * 25
    return contains_pixels(frame, [upper] * 9 + [lower] * 15)
