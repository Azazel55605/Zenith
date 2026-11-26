"""A three-color JPEG and a pixel check for the installed NetSurf browser."""
from PIL import Image, ImageChops


NAME = "served-photo.jpg"
COLORS = ((196, 65, 37), (34, 146, 181), (189, 167, 49))
BLOCK = 16


def write_fixture(directory):
    photo = Image.new("RGB", (BLOCK * len(COLORS), BLOCK))
    for i, color in enumerate(COLORS):
        photo.paste(color, (i * BLOCK, 0, (i + 1) * BLOCK, BLOCK))
    photo.save(directory / NAME, format="JPEG", quality=94, subsampling=0)
    return f'<img src="/{NAME}" width="48" height="16" alt="JPEG fixture">'


def rendered(frame):
    # Use interior pixels away from the JPEG block edges. Allow small differences
    # between the host and freestanding decoder's integer YCbCr conversions.
    masks = [band.point([255 if abs(v - expected) <= 3 else 0 for v in range(256)])
             for band, expected in zip(frame.split(), COLORS[0])]
    first = ImageChops.multiply(masks[0], ImageChops.multiply(masks[1], masks[2])).tobytes()
    pos = first.find(b"\xff" * 12)
    while pos >= 0:
        x, y = pos % frame.width, pos // frame.width
        if x + 44 <= frame.width and y + 12 <= frame.height:
            for i, color in enumerate(COLORS):
                box = (x + i * BLOCK + 2, y + 2, x + i * BLOCK + 12, y + 12)
                if any(low < expected - 3 or high > expected + 3
                       for (low, high), expected in zip(frame.crop(box).getextrema(), color)):
                    break
            else:
                return True
        pos = first.find(b"\xff" * 12, pos + 1)
    return False


def check_fixture(directory):
    write_fixture(directory)
    with Image.open(directory / NAME) as photo:
        if photo.format != "JPEG" or not rendered(photo.convert("RGB")):
            raise RuntimeError("The encoded JPEG fixture did not reproduce its three color blocks")
        if rendered(photo.convert("RGB").transpose(Image.Transpose.FLIP_LEFT_RIGHT)):
            raise RuntimeError("The JPEG check accepted reversed color blocks")
