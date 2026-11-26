#!/usr/bin/env python3
"""Build the browser's single-threaded x86-64 C library from pinned musl sources."""
import argparse
import fcntl
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
SOURCE = ROOT / "vendor/netsurf-runtime/musl-1.2.6.tar.gz"
SHA256 = "d585fd3b613c66151fc3249e8ed44f77020cb5e6c1e635a616d3f9f82460512a"


def build(destination):
    destination = destination.resolve()
    destination.mkdir(parents=True, exist_ok=True)
    if hashlib.sha256(SOURCE.read_bytes()).hexdigest() != SHA256:
        raise ValueError("musl source digest mismatch")
    compiler = os.environ.get("ZENITH_BROWSER_CC", "gcc")
    signature = hashlib.sha256(SHA256.encode())
    signature.update(subprocess.check_output([compiler, "--version"]))
    for path in [Path(__file__), *sorted((HERE / "runtime").glob("*"))]:
        signature.update(path.read_bytes())
    stamp = destination / ".musl-build"
    with (destination / ".musl-lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        if stamp.is_file() and stamp.read_text() == signature.hexdigest() and (destination / "lib/libc.a").is_file():
            return
        with tempfile.TemporaryDirectory(prefix="musl-", dir=destination.parent) as temporary:
            with tarfile.open(SOURCE) as archive:
                archive.extractall(temporary, filter="data")
            source = Path(temporary) / "musl-1.2.6"
            for name in ("syscall_arch.h", "pthread_arch.h"):
                shutil.copyfile(HERE / "runtime" / name, source / "arch/x86_64" / name)
            shutil.copyfile(HERE / "runtime/zenith_start.c", source / "src/env/zenith_start.c")
            environment = dict(os.environ, CC=compiler, AR="ar", RANLIB="ranlib", CFLAGS="-Os -mno-red-zone -ffunction-sections -fdata-sections -fno-pie -fno-stack-protector")
            subprocess.run(["./configure", "--target=x86_64-linux-musl", "--disable-shared", "--disable-wrapper",
                            "--with-malloc=oldmalloc", "--prefix="+str(destination)],
                           cwd=source, env=environment, check=True)
            subprocess.run(["make", "install-headers"], cwd=source, env=environment, check=True)
            subprocess.run(["make", "-j"+str(min(os.cpu_count() or 2, 12)), "lib/libc.a"],
                           cwd=source, env=environment, check=True)
            (destination / "lib").mkdir(exist_ok=True)
            lib = destination / "lib/libc.a.tmp"
            shutil.copyfile(source / "lib/libc.a", lib)
            lib.replace(destination / "lib/libc.a")
            stamp.write_text(signature.hexdigest())


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("destination", nargs="?", type=Path, default=ROOT / "obj/netsurf-sdk")
    build(p.parse_args().destination)
