"""Swap occasional adjacent download frames on transfer-bench's private QEMU link."""

import select
import socket
import struct
import tempfile
import threading
import time
from collections import Counter
from pathlib import Path


def data_segment(frame, ports):
    """Return flow and sequence range for an ordinary data segment from a fixture server."""
    if (
        len(frame) < 54
        or frame[12:14] != b"\x08\x00"
        or frame[14] >> 4 != 4
        or frame[23] != 6
        or frame[26:34] != bytes((10, 0, 2, 2, 10, 0, 2, 15))
        or int.from_bytes(frame[20:22], "big") & 0x3FFF
    ):
        return None
    ip_header = (frame[14] & 15) * 4
    tcp = 14 + ip_header
    ip_length = int.from_bytes(frame[16:18], "big")
    if ip_header < 20 or 14 + ip_length > len(frame) or tcp + 20 > 14 + ip_length:
        return None
    sport, dport, seq = struct.unpack_from("!HHI", frame, tcp)
    tcp_header = (frame[tcp + 12] >> 4) * 4
    length = ip_length - ip_header - tcp_header
    if (
        sport not in ports
        or tcp_header < 20
        or length < 512
        or frame[tcp + 13] not in (0x10, 0x18)
    ):
        return None
    return (sport, dport), seq, (seq + length) & 0xFFFFFFFF


class TransferOrder:
    """At most one frame waits for its successor. Never alter frame bytes or create frames."""

    def __init__(self, ports):
        self.ports = set(ports)
        self.swaps = dict.fromkeys(self.ports, 0)
        self.delivered_pairs = Counter()
        self.error = None
        self.stop = threading.Event()
        self.root = tempfile.TemporaryDirectory(prefix="zn-order-")
        self.listeners = []
        for name in ("observed", "delivered"):
            listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            listener.bind(str(Path(self.root.name) / name))
            listener.listen(1)
            listener.settimeout(0.2)
            self.listeners.append(listener)
        self.thread = threading.Thread(target=self._serve, daemon=True)
        self.thread.start()

    def options(self):
        # TX is slirp -> guest. The dump filter added afterwards sees delivery order.
        return [
            "-chardev",
            f"socket,id=observed,path={self.root.name}/observed",
            "-chardev",
            f"socket,id=delivered,path={self.root.name}/delivered",
            "-object",
            "filter-redirector,id=order,netdev=net0,queue=tx,outdev=observed,indev=delivered",
        ]

    def _accept(self, listener):
        while not self.stop.is_set():
            try:
                return listener.accept()[0]
            except TimeoutError:
                continue
        raise InterruptedError()

    def _serve(self):
        try:
            with (
                self._accept(self.listeners[0]) as observed,
                self._accept(self.listeners[1]) as delivered,
            ):
                self._forward(observed, delivered)
        except (OSError, ValueError) as error:
            if not self.stop.is_set():
                self.error = error

    def _forward(self, observed, delivered):
        buffer = bytearray()
        held = None
        deadline = 0.0
        counts = dict.fromkeys(self.ports, 0)
        while not self.stop.is_set():
            if held and time.monotonic() >= deadline:
                delivered.sendall(held[0])
                held = None
            timeout = max(0, deadline - time.monotonic()) if held else 0.25
            if not select.select([observed], [], [], timeout)[0]:
                if held:
                    delivered.sendall(held[0])
                    held = None
                continue
            chunk = observed.recv(65536)
            if not chunk:
                if buffer or held:
                    raise ValueError("incomplete QEMU benchmark stream")
                return
            buffer.extend(chunk)
            while len(buffer) >= 4:
                size = int.from_bytes(buffer[:4], "big")
                if not 14 <= size <= 65536:
                    raise ValueError("invalid QEMU Ethernet frame length")
                if len(buffer) < 4 + size:
                    break
                record = bytes(buffer[: 4 + size])
                del buffer[: 4 + size]
                segment = data_segment(record[4:], self.ports)
                if segment:
                    flow, seq, _ = segment
                    counts[flow[0]] += 1
                if held:
                    previous, (previous_flow, previous_seq, previous_end) = held
                    held = None
                    if (
                        segment
                        and flow == previous_flow
                        and seq == previous_end
                        and time.monotonic() <= deadline
                    ):
                        delivered.sendall(record + previous)
                        self.swaps[flow[0]] += 1
                        self.delivered_pairs[flow, seq, previous_seq] += 1
                        continue
                    delivered.sendall(previous)
                if segment and counts[flow[0]] % 64 == 0:
                    held = record, segment
                    deadline = time.monotonic() + 0.005
                else:
                    delivered.sendall(record)

    def close(self):
        self.stop.set()
        self.thread.join(timeout=2)
        for listener in self.listeners:
            listener.close()
        self.root.cleanup()
        if self.error:
            raise RuntimeError("benchmark packet-order fixture failed") from self.error
        if self.thread.is_alive():
            raise RuntimeError("benchmark packet-order fixture did not stop")
