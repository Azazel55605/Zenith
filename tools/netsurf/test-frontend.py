#!/usr/bin/env python3
"""Check the native frontend's input and raster callbacks against NetSurf 3.11."""
from pathlib import Path
import os
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
CORE = ROOT / "obj/netsurf-core"
UPSTREAM = CORE / "netsurf-all-3.11/netsurf"


def main():
    subprocess.run([sys.executable, "-B", ROOT / "tools/netsurf/test-profile.py"], check=True)
    subprocess.run([sys.executable, ROOT / "tools/netsurf/build-runtime.py"], check=True)
    subprocess.run([sys.executable, ROOT / "tools/build-netsurf-core.py"], check=True)
    with tempfile.TemporaryDirectory(prefix="zenith-netsurf-frontend-") as directory:
        tmp = Path(directory)
        atlas = tmp / "atlas.c"
        subprocess.run([sys.executable, ROOT / "tools/build-ui-fonts.py", atlas, "--mono", "--browser"], check=True)
        # Pull only the production UTF-8 decoder, leaving the engine's window
        # and navigation symbols to the fixture's observation callbacks.
        decoder = ["netsurf-all-3.11__netsurf__utils__utf8.c.o",
                   "netsurf-all-3.11__libparserutils__src__charset__encodings__utf8.c.o"]
        subprocess.run(["ar", "x", CORE / "libnetsurf.a", *decoder], cwd=tmp, check=True)
        compiler = [
            os.environ.get("CC", "cc"), "-std=gnu11", "-O1", "-Wall", "-Wextra", "-Werror",
            "-fsanitize=undefined", "-fno-sanitize-recover=all", "-DZENITH_BROWSER_VERSION=\"test\"",
            "-ffunction-sections", "-fdata-sections", "-Wl,--gc-sections", "-no-pie",
            "-I", UPSTREAM, "-I", UPSTREAM / "include", "-I", CORE / "include", "-I", ROOT / "userland/ui",
            ROOT / "userland/netsurf/plot.c",
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
                        ROOT / "userland/netsurf/fetch.c", ROOT / "userland/netsurf/start-page.c", CORE / "libnetsurf.a",
                        "-lm", "-o", navigation], check=True)
        subprocess.run([navigation, CORE / "resources", CORE / "resources/Messages"], check=True)


if __name__ == "__main__":
    main()
