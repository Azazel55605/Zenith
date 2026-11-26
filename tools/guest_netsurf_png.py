"""A small inline PNG and an independent pixel check for the browser guest gate."""
import base64
import io

from PIL import Image, ImageDraw


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
    rgb = frame.convert("RGB")
    data = rgb.tobytes()
    stride = rgb.width * 3
    upper = bytes((241, 36, 151)) * 11 + bytes((18, 190, 221)) * 25
    lower = bytes((43, 103, 24)) * 11 + bytes((255, 255, 255)) * 25
    start = data.find(upper)
    while start >= 0:
        if (start % 3 == 0 and start % stride + len(upper) <= stride and
                all(data[start + y*stride:start + y*stride + len(upper)] == (upper if y < 9 else lower)
                    for y in range(24))):
            return True
        start = data.find(upper, start + 1)
    return False
