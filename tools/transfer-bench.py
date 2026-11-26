#!/usr/bin/env python3
"""Time burst downloads to RAM ext2 and a disposable installed ext2 root over HTTP and optionally verified TLS."""
import argparse
import functools
import hashlib
import http.server
import importlib.util
import json
from pathlib import Path
import re
from collections import Counter
import subprocess
import struct
import threading
import time
from tls_fixture import certificates, context

spec = importlib.util.spec_from_file_location("headless", Path(__file__).with_name("headless-smoke.py"))
h = importlib.util.module_from_spec(spec); spec.loader.exec_module(h)
def captured_frames(path):
    with path.open("rb") as pcap:
        header = pcap.read(24)
        if len(header) != 24 or header[:4] != b"\xd4\xc3\xb2\xa1" or struct.unpack_from("<I", header, 20)[0] != 1:
            raise RuntimeError("expected Ethernet PCAP")
        while record := pcap.read(16):
            if len(record) != 16: raise RuntimeError("short PCAP record")
            _, _, captured, original = struct.unpack("<IIII", record)
            frame = pcap.read(captured)
            if len(frame) != captured or captured != original: raise RuntimeError("truncated wire capture")
            yield frame


def check_windows(path, expected_pairs=None, incoming=None):
    payloads = 0; windows = []
    last_ack = {}; pure_acks = 0; backward_acks = 0
    if expected_pairs:
        from transfer_order import data_segment
        pending = expected_pairs.copy()
        ports = {flow[0] for flow, _, _ in pending}
        prior = None
        # Compare the complete Ethernet records, including retransmissions and TLS payloads.
        delivery = Counter(hashlib.sha256(frame).digest() for frame in captured_frames(incoming)
                           if frame[26:34] == bytes((10, 0, 2, 2, 10, 0, 2, 15)))
    for frame in captured_frames(path):
        if expected_pairs and frame[26:30] == bytes((10, 0, 2, 2)):
            if frame[30:34] == bytes((10, 0, 2, 15)):
                digest = hashlib.sha256(frame).digest()
                if delivery[digest] <= 0: raise RuntimeError("delivered frame differs from relay input")
                delivery[digest] -= 1
            segment = data_segment(frame, ports)
            if prior and segment and prior[0] == segment[0] and prior[1] == segment[2]:
                pair = segment[0], prior[1], segment[1]
                if pending[pair]:
                    pending[pair] -= 1
            prior = segment
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
    if expected_pairs:
        if sum(pending.values()): raise RuntimeError("fixture swap missing from the downstream QEMU capture")
        if sum(delivery.values()): raise RuntimeError("download frame missing from the downstream QEMU capture")
    result = dict(payload_packets=payloads, maximum_window=max(windows),
                  pure_acks=pure_acks, backward_acks=backward_acks)
    if expected_pairs:
        result["verified_swapped_pairs"] = sum(expected_pairs.values())
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("keep", type=Path)
    parser.add_argument("--mib", type=int, default=4)
    parser.add_argument("--disk-mib", type=int, help="optional larger installed-root payload (1..32 MiB)")
    parser.add_argument("--https", action="store_true", help="also fetch the same bytes over verified TLS 1.2")
    parser.add_argument("--profile", action="store_true", help="record receive, wait, TLS, disk and NIC timing from an instrumented kernel")
    parser.add_argument("--reorder", action="store_true", help="swap occasional contiguous download segments on the private QEMU link; enables profiling")
    parser.add_argument("--runs", type=int, default=1, choices=range(1, 11))
    parser.add_argument("--iso", type=Path, help="boot an explicit kernel ISO for a before/after comparison")
    args = parser.parse_args()
    if args.reorder: args.profile = True
    if not 1 <= args.mib <= 8: raise ValueError("benchmark payload must be 1..8 MiB")
    disk_mib = args.disk_mib if args.disk_mib is not None else args.mib
    if not 1 <= disk_mib <= 32: raise ValueError("disk payload must be 1..32 MiB")
    args.keep.mkdir(parents=True, exist_ok=True)
    def fixture_bytes(mib, seed):
        # The impairment fixture needs distinguishable byte ranges across equal-size segments.
        if args.reorder: return hashlib.shake_256(seed).digest(mib << 20)
        return bytes(range(256)) * (mib * 4096)
    payload = fixture_bytes(args.mib, b"Zenith live transfer order")
    disk_payload = fixture_bytes(disk_mib, b"Zenith installed transfer order")
    served = args.keep / "served"; served.mkdir(exist_ok=True); (served / "payload").write_bytes(payload)
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
    machine = None; order = None; results = []; delivered_pairs = {}; retried = []
    def command(value, marker, timeout=180):
        offset = len(machine.log())
        machine.send(value + "; echo " + marker + "=$?")
        start = time.monotonic()
        machine.send("\r")
        if not machine.wait(marker + "=0", offset, timeout=timeout):
            raise RuntimeError("benchmark command failed: " + value + "\n" + machine.log()[offset:][-3000:])
        return time.monotonic() - start, h.efi.smoke.COSMOS_NOISE.sub("", machine.log()[offset:])
    network = ["-netdev", "user,id=net0", "-device", "e1000e,netdev=net0"]
    def boot(mode, **kwargs):
        nonlocal order
        options = network.copy()
        if args.reorder:
            from transfer_order import TransferOrder
            order = TransferOrder(server.server_port for server in servers.values())
            delivered_pairs[mode] = order.delivered_pairs
            options += ["-object", f"filter-dump,id=before,netdev=net0,queue=tx,file={args.keep / (mode + '-before.pcap')}"]
            options += order.options()
        options += ["-object", f"filter-dump,id=wire,netdev=net0,file={args.keep / (mode + '.pcap')}"]
        return h.Machine(args.keep / mode, network=options, **kwargs)

    def measure(target, directory, source, size):
        if args.profile:
            command("export ZENITH_TRANSFER_PROFILE=1", "BENCH_PROFILE")
        if args.https:
            command(f"tls bootstrap http://10.0.2.2:{servers['http'].server_port}/ca.pem {fingerprint}", "BENCH_CA")
        for scheme, server in servers.items():
            path = f"{directory}/{scheme}-download"
            url = f"{scheme}://10.0.2.2:{server.server_port}/{source}"
            for run in range(1, args.runs + 1):
                label = f"BENCH_{target.upper()}_{scheme.upper()}_{run}"
                fetch = "recovery fetch" if args.profile else "fetch"
                initial_swaps = order.swaps[server.server_port] if order else 0
                elapsed, output = command(f"{fetch} --kernel --replace {url} {path}", label, timeout=600)
                (args.keep / (label.lower() + ".log")).write_text(output)
                result = dict(target=target, scheme=scheme, run=run, seconds=elapsed, bytes=size,
                              bytes_per_second=size / elapsed, mbit_per_second=size * 8e-6 / elapsed)
                if args.profile:
                    record = re.search(r'fetch timing ((?:[a-z-]+=[0-9]+ )*waits=[0-9]+)(?=[\r\n])', output)
                    if record is None:
                        raise RuntimeError(f"missing complete transfer profile for {label}")
                    metrics = {k: int(v) for k, v in (pair.split("=") for pair in record[1].split())}
                    required = {"bytes", "attempt", "total-ms", "setup-ms", "receive-ms", "wait-ms", "tls-record-ms",
                                "write-ms", "commit-ms", "polls-ms", "polls", "frames", "reads", "waits",
                                "tcp-reject-ahead", "tcp-reject-behind", "tcp-reject-overlap", "tcp-reject-window",
                                "tcp-reject-ambiguous"}
                    if not required.issubset(metrics) or metrics["bytes"] != size or metrics["total-ms"] <= 0:
                        raise RuntimeError(f"invalid transfer profile for {label}")
                    # Kernels predating the link counters can still serve as the comparison baseline.
                    if "wait-timeouts" in metrics and metrics["wait-timeouts"] > metrics["waits"]:
                        raise RuntimeError(f"more timed-out waits than waits for {label}")
                    result["profile"] = metrics
                if order:
                    swaps = order.swaps[server.server_port] - initial_swaps
                    if swaps < 1:
                        raise RuntimeError(f"no adjacent fixture segments swapped during {label}")
                    if metrics["attempt"] != 1:
                        retried.append(label)
                    if ("tcp-buffered-segments" in metrics or "tcp-recovered-bytes" in metrics) and (
                            metrics.get("tcp-buffered-segments", 0) < 1 or metrics.get("tcp-recovered-bytes", 0) < 1):
                        raise RuntimeError(f"guest did not recover any reordered bytes during {label}")
                    result["swapped_pairs"] = swaps
                results.append(result)
                print(result, flush=True)
            if not directory.startswith("/root"):
                if order:
                    # The installer copies /root, allowing an independent audit of the RAM download too.
                    command(f"cp {path} /root/ram-{scheme}-download", "BENCH_SAVE")
                command("rm " + path, "BENCH_CLEAN")
    try:
        machine = boot("live", iso=args.iso)
        if not machine.wait("network: online address=10.0.2.15", timeout=120): raise RuntimeError("live network unavailable")
        measure("ram", "/tmp", "payload", len(payload))
        command("install sata0 --yes", "BENCH_INSTALL", timeout=300)
        machine.send("poweroff\r")
        if not machine.wait("all filesystems unmounted") or machine.process.wait(timeout=60) != 0: raise RuntimeError("live shutdown failed")
        installed = machine.disk; machine.stop()
        if order:
            order.close(); order = None
        machine = boot("installed", disk=installed)
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
            if args.reorder:
                ram = subprocess.run(["debugfs", "-R", f"cat /root/ram-{scheme}-download", str(volume)], capture_output=True, check=True).stdout
                if ram != payload: raise RuntimeError(scheme + " live RAM downloaded bytes differ")
        wire = {mode: check_windows(args.keep / (mode + ".pcap"), delivered_pairs.get(mode),
                                    args.keep / (mode + "-before.pcap"))
                for mode in ("live", "installed")}
        (args.keep / "results.json").write_text(json.dumps(dict(payload_sha256=hashlib.sha256(payload).hexdigest(),
            disk_payload_sha256=hashlib.sha256(disk_payload).hexdigest(), wire=wire, results=results), indent=2) + "\n")
        if retried:
            raise RuntimeError("reordered transfers required a retry: " + ", ".join(retried))
        print("PASS: downloaded payload exact; independently checked ext2 clean", flush=True)
    finally:
        if machine: machine.stop()
        if order: order.close()
        for server in servers.values():
            server.shutdown(); server.server_close()


if __name__ == "__main__": main()
