#!/usr/bin/env python3
"""X5: install, verify and execute a large native image on the 512 MiB guest."""
import functools
import hashlib
import http.server
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import threading

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
spec = importlib.util.spec_from_file_location("headless", ROOT / "tools/headless-smoke.py")
headless = importlib.util.module_from_spec(spec)
spec.loader.exec_module(headless)

PROGRAM = r"""
#include "zenith.h"

#ifdef LARGE
/* Put file-backed bytes on disk and a large zero-filled area in the ELF.
 * Volatile reads keep the checks on real mapped pages. */
__asm__(".pushsection .rodata\n.global payload\npayload:\n.fill 2097152,1,90\n.popsection");
extern const volatile unsigned char payload[];
static volatile unsigned char arena[34 * 1024 * 1024];
#endif

int main(int argc, char **argv)
{
    if (argc != 2) return 2;
#ifdef LARGE
    if (payload[0] != 90 || payload[2097151] != 90) return 3;
    for (unsigned int i = 0; i < sizeof(arena); i += 4096) {
        if (arena[i] != 0) return 4;
        arena[i] = 13;
    }
    if (arena[sizeof(arena) - 4096] != 13) return 5;
#endif
    static char buffer[4096];
    unsigned long length = 0;
    while (argv[1][length]) length++;
    long fd = zenith_call(2, (unsigned long)argv[1], length, Z_OPEN_READ);
    if (fd < 0) return 6;
    /* 18 MiB of real file I/O and 4608 reads, above both baseline budgets. */
    for (int i = 0; i < 4608; ++i) {
        long bytes = zenith_call(0, fd, (unsigned long)buffer, sizeof(buffer));
#ifndef LARGE
        if (i == 256) return bytes == -28 ? 0 : 7;
#endif
        if (bytes != sizeof(buffer) || buffer[0] != 90 || buffer[4095] != 90) return 8;
    }
    return zenith_call(3, fd, 0, 0) != 0;
}
"""


def main():
    keep = Path(sys.argv[1]).resolve()
    keep.mkdir(parents=True, exist_ok=True)
    machine = None
    served = keep / "served"
    served.mkdir()
    tool = ROOT / "tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll"
    subprocess.run(["dotnet", "build", str(tool.parents[3]), "-c", "Release", "--nologo"],
                   check=True, stdout=subprocess.DEVNULL)

    def package(*args):
        return subprocess.check_output(["dotnet", str(tool), *map(str, args)], text=True)

    source = keep / "probe.c"
    source.write_text(PROGRAM)
    startup = ROOT / "userland/sunbeam"
    package("keygen", keep / "keys")
    key = (keep / "keys/public.pem").read_bytes()
    (served / "key.pem").write_bytes(key)
    archives = keep / "archives"
    for name, large in (("x5-legacy", False), ("x5-large", True)):
        payload = keep / name
        elf = payload / f"usr/share/zenith/native/{name}.elf"
        elf.parent.mkdir(parents=True)
        subprocess.run([
            "gcc", "-Os", "-static", "-nostdlib", "-no-pie", "-fno-builtin", "-fno-stack-protector",
            "-mno-red-zone", "-Wl,-T," + str(startup / "link.ld"), "-Wl,--build-id=none",
            "-I" + str(startup), *(["-DLARGE"] if large else []), str(startup / "start.s"),
            str(source), "-o", str(elf),
        ], check=True)
        assert (elf.stat().st_size > 256 * 1024) == large
        bundle = keep / (name + "-bundle")
        package("build-native", name, "1.0", payload, bundle, "--repo", "extra")
        assert ("requires\tzenith-native-large-abi\t1.0\n" in (bundle / "manifest.tsv").read_text()) == large
        archive = archives / "extra" / name / "1.0" / (name + ".zpk")
        archive.parent.mkdir(parents=True)
        package("seal", bundle, keep / "keys/private.pem", archive)
        os.link(archive, served / (name + ".zpk"))

    for version, entries in (("", 1), ("-v3", 1), ("-v4", 2)):
        result = package("index-seal" + version, "extra", "1", "1700000000", "1700003600",
                         archives, keep / "keys/private.pem", keep / ("index" + version + ".zri"))
        assert result.rstrip().endswith("entries=" + str(entries)), result
    (served / "input").write_bytes(b"Z" * (18 * 1024 * 1024))

    class Handler(http.server.SimpleHTTPRequestHandler):
        def log_message(self, *_args):
            pass

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(Handler, directory=served))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    url = f"http://10.0.2.2:{server.server_port}"
    network = ["-netdev", "user,id=net0", "-device", "e1000e,netdev=net0"]

    def status(command, label, code=0, timeout=180):
        offset = len(machine.log())
        machine.send(f"{command}; echo X5_{label}=$? | logger\r")
        assert machine.wait(f"user: X5_{label}={code}", offset, timeout), (label, machine.serial)
        print("PASS X5_" + label, flush=True)

    def shutdown():
        offset = len(machine.log())
        machine.send("poweroff\r")
        assert machine.wait("all filesystems unmounted", offset, 60)
        assert machine.process.wait(timeout=30) == 0
        machine.stop()

    try:
        machine = headless.Machine(keep / "live", network=network)
        assert machine.wait("headless ready")
        status("session set-default headless", "SESSION")
        status("install sata0 --yes", "INSTALL", timeout=300)
        shutdown()
        machine = headless.Machine(keep / "installed", machine.disk, network=network)
        assert machine.wait("headless ready")
        status(f"fetch {url}/key.pem /root/key.pem", "KEY")
        status("pkg trust /root/key.pem " + hashlib.sha256(key).hexdigest(), "TRUST")
        for name in ("x5-legacy", "x5-large"):
            status(f"fetch {url}/{name}.zpk /root/{name}.zpk", "FETCH_" + name)
        status("pkg install /root/x5-legacy.zpk /root/x5-large.zpk", "PACKAGE")
        status(f"fetch --kernel {url}/input /root/input", "INPUT")
        status("native run x5-legacy /root/input", "LEGACY_BUDGET")
        status("native run x5-large /root/input", "LARGE_IMAGE_AND_READ")
        status("native run x5-large /root/input", "LARGE_IMAGE_REPEAT")
        status("pkg verify", "VERIFY")
        shutdown()
    finally:
        if machine is not None:
            machine.stop()
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    main()
