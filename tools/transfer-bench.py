#!/usr/bin/env python3
"""Time burst downloads to RAM ext2 and a disposable installed ext2 root over HTTP and optionally verified TLS."""
import argparse
import functools
import hashlib
import http.server
import importlib.util
import json
from pathlib import Path
import subprocess
import struct
import threading
import time
from tls_fixture import certificates, context

spec = importlib.util.spec_from_file_location("headless", Path(__file__).with_name("headless-smoke.py"))
h = importlib.util.module_from_spec(spec); spec.loader.exec_module(h)
def check_windows(path):
    data = path.read_bytes()
    if data[:4] != b"\xd4\xc3\xb2\xa1" or struct.unpack_from("<I", data, 20)[0] != 1:
        raise RuntimeError("expected Ethernet PCAP")
    position = 24; payloads = 0; windows = []
    last_ack = {}; pure_acks = 0; backward_acks = 0
    while position < len(data):
        _, _, captured, original = struct.unpack_from("<IIII", data, position); position += 16
        frame = data[position:position + captured]; position += captured
        if len(frame) != captured or captured != original: raise RuntimeError("truncated wire capture")
        if len(frame) < 54 or frame[12:14] != b"\x08\x00" or frame[23] != 6 or frame[26:30] != bytes([10, 0, 2, 15]): continue
        ip = (frame[14] & 15) * 4; tcp = 14 + ip
        window = int.from_bytes(frame[tcp + 14:tcp + 16], "big"); windows.append(window)
        if window > 32768: raise RuntimeError("guest advertises more than its local receive window")
        length = int.from_bytes(frame[16:18], "big") - ip - (frame[tcp + 12] >> 4) * 4
        if length > 0: payloads += 1
        flow = frame[26:34] + frame[tcp:tcp + 4]
        flags = frame[tcp + 13]
        if flags & 2:
            last_ack.pop(flow, None)
        if flags == 0x10 and length == 0:
            ack = int.from_bytes(frame[tcp + 8:tcp + 12], "big")
            previous = last_ack.get(flow)
            if previous is not None and ((ack - previous) & 0xffffffff) > 0x7fffffff:
                backward_acks += 1
            last_ack[flow] = ack
            pure_acks += 1
    if payloads == 0: raise RuntimeError("no guest TCP payload in capture")
    return dict(payload_packets=payloads, maximum_window=max(windows),
                pure_acks=pure_acks, backward_acks=backward_acks)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("keep", type=Path)
    parser.add_argument("--mib", type=int, default=4)
    parser.add_argument("--disk-mib", type=int, help="optional larger installed-root payload (1..32 MiB)")
    parser.add_argument("--https", action="store_true", help="also fetch the same bytes over verified TLS 1.2")
    parser.add_argument("--runs", type=int, default=1, choices=range(1, 11))
    parser.add_argument("--iso", type=Path, help="boot an explicit kernel ISO for a before/after comparison")
    args = parser.parse_args()
    if not 1 <= args.mib <= 8: raise ValueError("benchmark payload must be 1..8 MiB")
    disk_mib = args.disk_mib if args.disk_mib is not None else args.mib
    if not 1 <= disk_mib <= 32: raise ValueError("disk payload must be 1..32 MiB")
    args.keep.mkdir(parents=True, exist_ok=True)
    payload = bytes(range(256)) * (args.mib * 4096)
    served = args.keep / "served"; served.mkdir(exist_ok=True); (served / "payload").write_bytes(payload)
    disk_payload = bytes(range(256)) * (disk_mib * 4096)
    (served / "disk-payload").write_bytes(disk_payload)
    class Handler(http.server.SimpleHTTPRequestHandler):
        def log_message(self, *args): pass
    handler = functools.partial(Handler, directory=str(served))
    servers = {"http": http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)}
    if args.https:
        ca, fingerprint = certificates(args.keep.resolve() / "certificates")
        (served / "ca.pem").write_bytes(ca)
        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
        server.socket = context(args.keep / "certificates").wrap_socket(server.socket, server_side=True)
        servers["https"] = server
    for server in servers.values():
        threading.Thread(target=server.serve_forever, daemon=True).start()
    machine = None; results = []
    def command(value, marker, timeout=180):
        offset = len(machine.log())
        machine.send(value + "; echo " + marker + "=$?")
        start = time.monotonic()
        machine.send("\r")
        if not machine.wait(marker + "=0", offset, timeout=timeout):
            raise RuntimeError("benchmark command failed: " + value + "\n" + machine.log()[offset:][-3000:])
        return time.monotonic() - start, h.efi.smoke.COSMOS_NOISE.sub("", machine.log()[offset:])
    network = ["-netdev", "user,id=net0", "-device", "e1000e,netdev=net0"]
    def measure(target, directory, source, size):
        if args.https:
            command(f"tls bootstrap http://10.0.2.2:{servers['http'].server_port}/ca.pem {fingerprint}", "BENCH_CA")
        for scheme, server in servers.items():
            path = f"{directory}/{scheme}-download"
            url = f"{scheme}://10.0.2.2:{server.server_port}/{source}"
            for run in range(1, args.runs + 1):
                label = f"BENCH_{target.upper()}_{scheme.upper()}_{run}"
                elapsed, output = command(f"fetch --kernel --replace {url} {path}", label, timeout=600)
                results.append(dict(target=target, scheme=scheme, run=run, seconds=elapsed, bytes=size,
                                    bytes_per_second=size / elapsed, mbit_per_second=size * 8e-6 / elapsed))
                print(results[-1], flush=True)
                (args.keep / (label.lower() + ".log")).write_text(output)
            if not directory.startswith("/root"):
                command("rm " + path, "BENCH_CLEAN")
    try:
        machine = h.Machine(args.keep / "live", iso=args.iso, network=network + ["-object", f"filter-dump,id=wire,netdev=net0,file={args.keep / 'live.pcap'}"])
        if not machine.wait("network: online address=10.0.2.15", timeout=120): raise RuntimeError("live network unavailable")
        measure("ram", "/tmp", "payload", len(payload))
        command("install sata0 --yes", "BENCH_INSTALL", timeout=300)
        machine.send("poweroff\r")
        if not machine.wait("all filesystems unmounted") or machine.process.wait(timeout=60) != 0: raise RuntimeError("live shutdown failed")
        installed = machine.disk; machine.stop()
        machine = h.Machine(args.keep / "installed", disk=installed, network=network + ["-object", f"filter-dump,id=wire,netdev=net0,file={args.keep / 'installed.pcap'}"])
        if not machine.wait("network: online address=10.0.2.15", timeout=120): raise RuntimeError("installed network unavailable")
        measure("disk", "/root", "disk-payload", len(disk_payload))
        machine.send("poweroff\r")
        if not machine.wait("all filesystems unmounted") or machine.process.wait(timeout=60) != 0: raise RuntimeError("disk shutdown failed")
        import boot_image
        roots = [p for p in boot_image.partitions(machine.disk) if p[0] != boot_image.ESP]
        if len(roots) != 1: raise RuntimeError("ambiguous scratch root")
        volume = args.keep / "checked-root.ext2"
        with machine.disk.open("rb") as source, volume.open("wb") as destination:
            source.seek(roots[0][2]*512); remaining = (roots[0][3]-roots[0][2]+1)*512
            while remaining:
                block = source.read(min(1048576, remaining))
                if not block: raise RuntimeError("short scratch root")
                destination.write(block); remaining -= len(block)
        audit = subprocess.run(["e2fsck", "-f", "-n", str(volume)], capture_output=True)
        (args.keep / "e2fsck.log").write_bytes(audit.stdout + audit.stderr)
        if audit.returncode != 0: raise RuntimeError("benchmark root is inconsistent")
        for scheme in servers:
            data = subprocess.run(["debugfs", "-R", f"cat /root/{scheme}-download", str(volume)], capture_output=True, check=True).stdout
            if data != disk_payload: raise RuntimeError(scheme + " downloaded bytes differ")
        wire = {mode: check_windows(args.keep / (mode + ".pcap")) for mode in ("live", "installed")}
        (args.keep / "results.json").write_text(json.dumps(dict(payload_sha256=hashlib.sha256(payload).hexdigest(),
            disk_payload_sha256=hashlib.sha256(disk_payload).hexdigest(), wire=wire, results=results), indent=2) + "\n")
        print("PASS: downloaded payload exact; independently checked ext2 clean", flush=True)
    finally:
        if machine: machine.stop()
        for server in servers.values():
            server.shutdown(); server.server_close()


if __name__ == "__main__": main()
