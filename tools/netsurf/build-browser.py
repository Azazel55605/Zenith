#!/usr/bin/env python3
"""Build the static Zenith NetSurf executable and its installed data files."""
import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
APP = ROOT / "userland/netsurf"
UI = ROOT / "userland/ui"
CORE = ROOT / "obj/netsurf-core"
SDK = ROOT / "obj/netsurf-sdk"


def copy(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, destination)


def pack_source(destination):
    """Ship the corresponding build inputs together, within the signed per-file limit."""
    inputs = [ROOT / name for name in (
        "tools/build-userland.py", "tools/build-ui-fonts.py", "tools/build-netsurf-core.py",
        "tools/fixtures/netsurf/frontend.c",
        "userland/sunbeam/zenith.h", "userland/net-tools/http_response.h", "Resources/Fonts/Inter-Regular.ttf",
        "Resources/Fonts/Inter-SemiBold.ttf", "Resources/Fonts/Hack-Regular.ttf",
        "Resources/Fonts/LICENSE-Inter.txt", "Resources/Fonts/LICENSE-Hack.txt")]
    for folder in (HERE, APP, UI, ROOT / "userland/include", ROOT / "vendor/netsurf", ROOT / "vendor/netsurf-runtime"):
        inputs.extend(path for path in folder.rglob("*") if path.is_file() and "__pycache__" not in path.parts)
    with tarfile.open(destination, "w:xz", preset=2) as archive:
        for path in sorted(inputs):
            entry = tarfile.TarInfo(path.relative_to(ROOT).as_posix())
            entry.size = path.stat().st_size
            entry.mode = 0o755 if path.suffix == ".py" else 0o644
            with path.open("rb") as source:
                archive.addfile(entry, source)


def build(destination, version):
    cc = os.environ.get("ZENITH_BROWSER_CC", "gcc")
    subprocess.run([sys.executable, HERE / "build-runtime.py"], check=True)
    subprocess.run([sys.executable, ROOT / "tools/build-netsurf-core.py"], check=True)
    sources = CORE / "netsurf-all-3.11"
    upstream = sources / "netsurf"
    target = destination / "usr/share/zenith"
    elf = target / "native/netsurf.elf"
    elf.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="netsurf-link-", dir=CORE) as directory:
        work = Path(directory)
        atlas = work / "atlas.c"
        subprocess.run([sys.executable, ROOT / "tools/build-ui-fonts.py", atlas, "--mono"], check=True)
        flags = ["-std=gnu11", "-Os", "-ffunction-sections", "-fdata-sections", "-fno-pie",
                 "-fno-stack-protector", "-mno-red-zone", "-fcf-protection=none", "-fno-builtin",
                 "-fno-asynchronous-unwind-tables", "-fno-unwind-tables", "-nostdinc",
                 "-isystem", SDK / "include", "-I", CORE / "include", "-I", upstream,
                 "-I", upstream / "include", "-I", ROOT / "userland/include", "-I", UI,
                 "-D_DEFAULT_SOURCE", "-D__zenith__", "-DZUI_WITH_LIBC", '-DZENITH_BROWSER_VERSION="' + version + '"',
                 "-ffile-prefix-map=" + str(ROOT) + "=."]
        objects = []
        for source in [HERE / "runtime/start.s", APP / "libc_zenith.c", APP / "fetch.c",
                       APP / "fetch-transport.c", APP / "frontend.c", APP / "font.c", APP / "plot.c",
                       UI / "ui.c", UI / "widgets.c", UI / "runtime.c", UI / "text.c", atlas]:
            obj = work / (source.stem + ".o")
            subprocess.run([cc, *flags, "-c", source, "-o", obj], check=True)
            objects.append(obj)
        linked = work / "netsurf.elf"
        subprocess.run([cc, "-nostdlib", "-static", "-no-pie", "-Wl,--gc-sections",
                        "-Wl,--build-id=none", "-Wl,-z,max-page-size=4096",
                        "-T", HERE / "runtime/link.ld", *objects, "-Wl,--start-group",
                        CORE / "libnetsurf.a", SDK / "lib/libc.a", "-lgcc", "-Wl,--end-group",
                        "-o", linked], check=True)
        # Keep symbols in the build tree for guest fault addresses. Linux syscall/TLS
        # assembly in the linked image indicates an unported libc entry point.
        shutil.copyfile(linked, CORE / "netsurf.debug.elf")
        assembly = subprocess.check_output(["objdump", "-d", "--no-show-raw-insn", linked], text=True)
        bad = re.search(r"^\s*[0-9a-f]+:\s+(?:syscall\b|sysenter\b|int\s+\$0x80\b|[^\n]*%fs:)", assembly, re.M)
        if bad:
            raise ValueError("Linux-only instruction in Zenith image: " + bad.group().strip())
        subprocess.run(["objcopy", "--strip-all", linked, elf], check=True)
    if elf.stat().st_size > 4 * 1024 * 1024:
        raise ValueError("NetSurf exceeds the signed native program size limit")
    resources = target / "netsurf"
    for asset in (CORE / "resources").iterdir():
        if asset.is_file():
            copy(asset, resources / asset.name)
    for asset in (APP / "resources").iterdir():
        copy(asset, resources / asset.name)
    copy(APP / "netsurf.app", target / "apps/netsurf.app")
    # Nebula's own toolbar and menu icons beside the shared ones (zenith-icons is at the package file limit).
    import importlib.util
    spec = importlib.util.spec_from_file_location("ui_icons", ROOT / "tools/build-ui-icons.py")
    icons = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(icons)
    icon_dir = destination / icons.ICON_DIR
    icon_dir.mkdir(parents=True, exist_ok=True)
    for svg in sorted((APP / "icons").glob("*.svg")):
        (icon_dir / (svg.stem + ".zic")).write_bytes(icons.icon_file(svg.read_text()))
    notices = destination / "usr/share/doc/zenith-netsurf"
    copy(upstream / "COPYING", notices / "COPYING")
    copy(ROOT / "vendor/netsurf-runtime/COPYRIGHT", notices / "COPYRIGHT-musl")
    copy(APP / "icons/LICENSE", notices / "LICENSE-Lucide.txt")
    for face in ("Inter", "Hack"):
        copy(ROOT / ("Resources/Fonts/LICENSE-" + face + ".txt"), notices / ("LICENSE-" + face + ".txt"))
    for name in ("libcss", "libdom", "libhubbub", "libnsbmp", "libnsgif", "libnsutils", "libparserutils", "libwapcaplet"):
        copy(sources / name / "COPYING", notices / ("COPYING-" + name))
    copy(CORE / "zlib-1.4/LICENSE", notices / "LICENSE-zlib")
    for name in ("LICENSE", "TRADEMARK"):
        copy(CORE / "libpng-1.6.59" / name, notices / (name + "-libpng"))
    pack_source(notices / "source.tar.xz")
    print(f"Built NetSurf for Zenith: {elf} ({elf.stat().st_size:,} bytes)")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--version", default="0.1.0")
    args = parser.parse_args()
    build(args.destination.resolve(), args.version)
