#!/usr/bin/env python3
"""Replay the VMware DHCP offer profile through a real E1000E guest receive path."""
import argparse
import importlib.util
from pathlib import Path
import socket
import struct
import threading
import re
from dhcp_capture import broadcast_requests

spec = importlib.util.spec_from_file_location("headless", Path(__file__).with_name("headless-smoke.py"))
h = importlib.util.module_from_spec(spec); spec.loader.exec_module(h)


def checksum(data):
    if len(data) % 2: data += b"\x00"
    total = sum(struct.unpack("!" + "H" * (len(data) // 2), data))
    while total >> 16: total = (total & 65535) + (total >> 16)
    return (~total) & 65535


def reply(frame, unicast):
    if len(frame) < 285 or frame[12:14] != b"\x08\x00" or frame[23] != 17: return None
    udp = 14 + (frame[14] & 15) * 4
    if frame[udp:udp + 4] != b"\x00\x44\x00\x43": return None
    request = frame[udp + 8:]
    if len(request) < 243 or request[240:242] != b"\x35\x01" or request[242] not in (1, 3): return None
    payload = bytearray(300); payload[:240] = request[:240]; payload[0] = 2
    address, server, gateway = b"\xac\x10\x2b\x8d", b"\xac\x10\x2b\xfe", b"\xac\x10\x2b\x02"
    payload[16:20] = address; payload[20:24] = server
    options = b"\x35\x01" + bytes([2 if request[242] == 1 else 5])
    for kind, value in ((54, server), (51, struct.pack("!I", 1800)), (1, b"\xff\xff\xff\x00"), (3, gateway), (6, gateway)):
        options += bytes([kind, 4]) + value
    options += b"\xff"; payload[240:240 + len(options)] = options
    destination = b"\xff" * 4
    udp_data = bytearray(struct.pack("!HHHH", 67, 68, 308, 0) + payload)
    struct.pack_into("!H", udp_data, 6, checksum(server + destination + b"\x00\x11\x01\x34" + udp_data) or 65535)
    ip = bytearray(struct.pack("!BBHHHBBH4s4s", 0x45, 0x10, 328, 0, 0, 16, 17, 0, server, destination))
    struct.pack_into("!H", ip, 10, checksum(ip))
    return (frame[6:12] if unicast else b"\xff" * 6) + b"\x00\x50\x56\xf0\xf1\xc4\x08\x00" + ip + udp_data


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("keep", type=Path); args = parser.parse_args()
    checks = 0
    def check(passed, label):
        nonlocal checks
        checks += 1; print(("ok   " if passed else "FAIL ") + label, flush=True)
        if not passed: raise RuntimeError(label)
    for unicast in (True, False):
        label = "unicast" if unicast else "broadcast"; directory = args.keep / label
        stop = threading.Event(); errors = []; replies = []; connection = []; machine = None
        listener = socket.socket(); listener.bind(("127.0.0.1", 0)); listener.listen(1); listener.settimeout(1)
        def serve():
            try:
                while not stop.is_set():
                    try: client, _ = listener.accept(); break
                    except socket.timeout: continue
                else: return
                connection.append(client); client.settimeout(1); pending = b""
                with client:
                    while not stop.is_set():
                        try: data = client.recv(65536)
                        except socket.timeout: continue
                        if not data: break
                        pending += data
                        while len(pending) >= 4:
                            length = struct.unpack_from("!I", pending)[0]
                            if length > 65535: raise ValueError("oversized Ethernet frame")
                            if len(pending) < length + 4: break
                            frame = pending[4:4 + length]; pending = pending[4 + length:]
                            response = reply(frame, unicast)
                            if response is not None:
                                replies.append(response)
                                client.sendall(struct.pack("!I", len(response)) + response)
            except OSError as error:
                if not stop.is_set(): errors.append(str(error))
            except Exception as error: errors.append(str(error))
        thread = threading.Thread(target=serve, daemon=True); thread.start()
        try:
            machine = h.Machine(directory, network=["-netdev", f"socket,id=net0,connect=127.0.0.1:{listener.getsockname()[1]}",
                "-device", "e1000e,netdev=net0", "-object", f"filter-dump,id=wire,netdev=net0,file={directory / 'network.pcap'}"])
            check(machine.wait("headless ready", timeout=600), label + " guest boot")
            check(machine.wait("network: online address=172.16.43.141", timeout=60), label + " VMware-profile DHCP lease")
            offset = len(machine.log()); machine.send("ip; echo DHCP_RECEIVE_STATUS=$?\r")
            check(machine.wait("DHCP_RECEIVE_STATUS=0", offset), label + " status command")
            output = h.efi.smoke.COSMOS_NOISE.sub("", machine.log()[offset:])
            check("receive-filter-initially-valid=" in output, label + " driver adaptation ran before networking")
            interrupts = re.search(r"nic-irqs=(\d+) rx-irqs=(\d+)", output)
            check(interrupts is not None and int(interrupts[1]) > 0 and int(interrupts[2]) > 0,
                  label + " receive IRQ diagnostics observe real driver delivery")
            check("dhcp-replies=2 rejected=0" in output, label + " Offer and ACK accepted")
            check("gateway=172.16.43.2" in output and "dns=172.16.43.2" in output, label + " VMware route and DNS")
            check(len(replies) == 2 and not errors, label + " fixture exchanged exactly Offer and ACK")
            machine.send("poweroff\r"); check(machine.wait("all filesystems unmounted"), label + " clean shutdown")
            check(machine.process.wait(timeout=60) == 0, label + " poweroff exit")
            check(broadcast_requests(directory / "network.pcap") == [1, 3], label + " outgoing broadcast Discover and Request")
        finally:
            stop.set()
            if machine: machine.stop()
            for client in connection:
                try: client.shutdown(socket.SHUT_RDWR)
                except OSError: pass
            listener.close(); thread.join(timeout=5)
    print(f"RESULT: {checks}/{checks} DHCP receive checks passed", flush=True)


if __name__ == "__main__": main()
