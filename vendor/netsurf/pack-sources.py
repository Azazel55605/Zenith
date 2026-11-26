#!/usr/bin/env python3
"""Reproduce sources.tar.xz from the verified, unmodified release tarballs."""
import argparse
import hashlib
from pathlib import Path, PurePosixPath
import tarfile

LIBS = {"libwapcaplet", "libparserutils", "libhubbub", "libcss", "libdom", "libnsutils", "libnsbmp", "libnsgif"}
NETSURF_HASH = "4dea880ff3c2f698bfd62c982b259340f9abcd7f67e6c8eb2b32c61f71644b7b"
ZLIB_HASH = "02dd159783677a749c04adbdd02b55b8a2bcc7e054f51b76b549199a916ad584"
LIBPNG_HASH = "d80dd2a38a37f803cb9b6ac7b14bd6e74ddc3b654780a8380bdf93523fdb4389"


def selected(parts):
    if len(parts) < 2:
        return False
    if parts[0] == "zlib-1.4":
        return len(parts) == 2 and (parts[-1].endswith((".h", ".c")) or parts[-1] in ("README", "LICENSE"))
    if parts[0] == "libpng-1.6.59":
        leaf = parts[-1]
        return (parts[1:] == ("scripts", "pnglibconf.h.prebuilt") or
                (len(parts) == 2 and (leaf in ("README", "LICENSE", "TRADEMARK") or
                 (leaf.startswith("png") and leaf.endswith((".c", ".h")) and leaf != "pngtest.c"))))
    if len(parts) == 2:
        return parts[1] in ("README.md", "ChangeLog.md")
    component, tail = parts[1], parts[2:]
    if component not in LIBS | {"netsurf"}:
        return False
    if tail[-1].upper().startswith(("LICENSE", "LICENCE", "COPYING", "COPYRIGHT", "AUTHORS")):
        return True
    if len(tail) == 1:
        return tail[0].startswith(("README", "Makefile"))
    if component != "netsurf":
        return tail[0] in ("include", "src", "build") or tail[:2] == ("bindings", "hubbub")
    if "duktape" in tail or tail[0] == "test":
        return False
    if tail in (("resources", "en", "credits.html"), ("resources", "en", "licence.html")):
        return True
    if tail[0] in ("content", "desktop", "include", "utils"):
        return True
    return (tail[0] == "resources" and len(tail) == 2 and
            tail[1] in ("FatMessages", "default.css", "internal.css", "adblock.css", "quirks.css", "SearchEngines")) or tail == ("tools", "split-messages.c")


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("netsurf", type=Path)
    p.add_argument("zlib", type=Path)
    p.add_argument("libpng", type=Path)
    p.add_argument("output", type=Path)
    args = p.parse_args()
    with tarfile.open(args.output, "x:xz", preset=6) as out:
        for path, digest in ((args.netsurf, NETSURF_HASH), (args.zlib, ZLIB_HASH), (args.libpng, LIBPNG_HASH)):
            if hashlib.sha256(path.read_bytes()).hexdigest() != digest:
                raise ValueError(f"Upstream archive digest mismatch: {path}")
            with tarfile.open(path) as src:
                for member in sorted(src.getmembers(), key=lambda m: m.name):
                    if member.isfile() and selected(PurePosixPath(member.name).parts):
                        clean = tarfile.TarInfo(member.name)
                        clean.size = member.size
                        out.addfile(clean, src.extractfile(member))


if __name__ == "__main__":
    main()
