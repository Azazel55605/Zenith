#!/usr/bin/env python3
"""Check the native frontend's input and raster callbacks against NetSurf 3.11."""
from pathlib import Path
import argparse
import base64
import os
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]


def jpeg_cases(destination):
    images = ROOT / "tests/Zenith.Tests/Fixtures/images"
    baseline = (images / "quads-444.jpg").read_bytes()
    oversized = bytearray(baseline)
    # SOF0 stores the big-endian height and width after its precision field.
    size = baseline.index(b"\xff\xc0") + 5
    oversized[size:size + 4] = bytes.fromhex("1000 1000")
    samples = [("baseline", baseline), ("progressive", (images / "prog-420.jpg").read_bytes()),
               ("gray", (images / "prog-grey.jpg").read_bytes()), ("truncated", baseline[:-2]),
               ("oversized", oversized)]
    with destination.open("w") as out:
        for name, data in samples:
            out.write(name + " data:image/jpeg;base64," + base64.b64encode(data).decode("ascii") + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--core", type=Path, default=ROOT / "obj/netsurf-core")
    parser.add_argument("--sdk", type=Path, default=ROOT / "obj/netsurf-sdk")
    parser.add_argument("--mdn-responsive-page", type=Path, help="also test a local copy of MDN’s learn/media-queries/grid.html")
    parser.add_argument("--simplecss-stylesheet", type=Path, help="also test a local copy of the published Simple.css stylesheet")
    args = parser.parse_args()
    core, sdk = args.core.resolve(), args.sdk.resolve()
    upstream = core / "netsurf-all-3.11/netsurf"
    subprocess.run([sys.executable, "-B", ROOT / "tools/netsurf/test-profile.py"], check=True)
    subprocess.run([sys.executable, ROOT / "tools/netsurf/build-runtime.py", sdk], check=True)
    subprocess.run([sys.executable, ROOT / "tools/build-netsurf-core.py", "--output", core, "--sdk", sdk], check=True)
    with tempfile.TemporaryDirectory(prefix="zenith-netsurf-frontend-") as directory:
        tmp = Path(directory)
        atlas = tmp / "atlas.c"
        subprocess.run([sys.executable, ROOT / "tools/build-ui-fonts.py", atlas, "--mono", "--browser"], check=True)
        # Pull only the production UTF-8 decoder, leaving the engine's window
        # and navigation symbols to the fixture's observation callbacks.
        decoder = ["netsurf-all-3.11__netsurf__utils__utf8.c.o",
                   "netsurf-all-3.11__libparserutils__src__charset__encodings__utf8.c.o"]
        subprocess.run(["ar", "x", core / "libnetsurf.a", *decoder], cwd=tmp, check=True)
        compiler = [
            os.environ.get("CC", "cc"), "-std=gnu11", "-O1", "-Wall", "-Wextra", "-Werror",
            "-fsanitize=undefined", "-fno-sanitize-recover=all", "-DZENITH_BROWSER_VERSION=\"test\"",
            "-ffunction-sections", "-fdata-sections", "-Wl,--gc-sections", "-no-pie",
            "-I", upstream, "-I", upstream / "include", "-I", core / "include", "-I", ROOT / "userland/ui",
            ROOT / "userland/netsurf/plot.c", ROOT / "userland/netsurf/source-view.c",
            ROOT / "userland/netsurf/font.c", ROOT / "userland/ui/ui.c", ROOT / "userland/ui/text.c",
            atlas]
        binary = tmp / "frontend-test"
        subprocess.run([*compiler, ROOT / "tools/fixtures/netsurf/frontend.c",
                        *(tmp / obj for obj in decoder), "-lm", "-o", binary], check=True)
        subprocess.run([binary], check=True)
        typography = tmp / "typography-test"
        subprocess.run([*compiler, ROOT / "tools/fixtures/netsurf/typography.c",
                        *(tmp / obj for obj in decoder), "-lm", "-o", typography], check=True)
        subprocess.run([typography], check=True)
        navigation = tmp / "navigation-test"
        subprocess.run([*compiler, ROOT / "tools/fixtures/netsurf/navigation.c",
                        '-DNEBULA_RESOURCE_DIR="' + str(ROOT / "userland/netsurf/resources") + '/"',
                        ROOT / "userland/netsurf/fetch.c", ROOT / "userland/netsurf/start-page.c", core / "libnetsurf.a",
                        "-lm", "-o", navigation], check=True)
        jpeg_cases(tmp / "jpeg-cases")
        subprocess.run([navigation, core / "resources", core / "resources/Messages", tmp / "jpeg-cases",
                        args.mdn_responsive_page.resolve() if args.mdn_responsive_page else "",
                        args.simplecss_stylesheet.resolve() if args.simplecss_stylesheet else ""], check=True)


if __name__ == "__main__":
    main()
