#!/usr/bin/env python3
"""M3 disposable IPv4 lab: QEMU/slirp, loopback HTTP/DNS/NTP; no external network dependencies."""
import argparse
import json
import os
import subprocess
import importlib.util
from pathlib import Path
import socket
import socketserver
import struct
import threading
import time

spec = importlib.util.spec_from_file_location("headless", Path(__file__).with_name("headless-smoke.py"))
h = importlib.util.module_from_spec(spec); spec.loader.exec_module(h)
PAYLOAD = (b"Zenith network lab\n" * 4096)

class Http(socketserver.BaseRequestHandler):
    def handle(self):
        try:
            self.request.settimeout(5)
            request = b""
            while b"\r\n\r\n" not in request and len(request) < 1024:
                data = self.request.recv(512)
                if not data: return
                request += data
            if not request: return  # nc -z
            path = request.split(b" ")[1]
            if path == b"/retry":
                with self.server.count_lock:
                    self.server.retry_count += 1
                    path = b"/truncated" if self.server.retry_count == 1 else b"/ok"
            if path == b"/oversized":
                self.request.sendall(b"HTTP/1.0 200 OK\r\nContent-Length: 8388609\r\n\r\n"); return
            if path == b"/headers":
                self.request.sendall(b"HTTP/1.0 200 OK\r\nX-Large: " + b"x" * 9000 + b"\r\nContent-Length: 0\r\n\r\n"); return
            if path == b"/encoding":
                self.request.sendall(b"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n"); return
            if path == b"/truncated":
                self.request.sendall(b"HTTP/1.0 200 OK\r\nContent-Length: 100\r\n\r\nshort"); return
            if path in (b"/slow", b"/stall"):
                self.request.sendall(b"HTTP/1.0 200 OK\r\nContent-Length: 100000\r\n\r\nstart")
                self.server.slow.set(); self.server.stop.wait(10); return
            self.request.sendall(b"HTTP/1.0 200 OK\r\nContent-Length: " + str(len(PAYLOAD)).encode() + b"\r\n\r\n")
            for start in range(0, len(PAYLOAD), 1024):
                self.request.sendall(PAYLOAD[start:start + 1024]); time.sleep(0.02)
        except (OSError, IndexError): pass

class Dns(socketserver.BaseRequestHandler):
    def handle(self):
        data, sock = self.request
        if len(data) < 12: return
        result = bytearray(data); result[2:4] = b"\x85\x80"; result[6:8] = b"\x00\x01"
        result.extend(b"\xc0\x0c\x00\x01\x00\x01\x00\x00\x00\x3c\x00\x04\x0a\x00\x02\x02")
        if b"reject" in data:
            result[0] ^= 1; sock.sendto(result, self.client_address); return
        bad = bytearray(result); bad[0] ^= 1; sock.sendto(bad, self.client_address)
        sock.sendto(result, self.client_address)

class Ntp(socketserver.BaseRequestHandler):
    def handle(self):
        data, sock = self.request
        if len(data) != 48: return
        result = bytearray(48); result[0:2] = b"\x24\x02"; result[24:32] = data[40:48]
        # Fixed known UTC permits validation of the software correction, independent of host RTC.
        seconds = 4000000000
        result[40:44] = struct.pack("!I", seconds)
        bad = bytearray(result); bad[24] ^= 1; sock.sendto(bad, self.client_address)
        sock.sendto(result, self.client_address)

class TcpServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True
class UdpServer(socketserver.ThreadingUDPServer):
    daemon_threads = True

def main():
    parser = argparse.ArgumentParser(description=__doc__); parser.add_argument("keep", type=Path, nargs="?", default=Path("/tmp/zenith-network"))
    args = parser.parse_args(); args.keep.mkdir(parents=True, exist_ok=True)
    servers = [TcpServer(("127.0.0.1", 0), Http), UdpServer(("127.0.0.1", 0), Dns), UdpServer(("127.0.0.1", 0), Ntp)]
    http, dns, ntp = [s.server_address[1] for s in servers]
    servers[0].slow = threading.Event(); servers[0].stop = threading.Event(); servers[0].count_lock = threading.Lock(); servers[0].retry_count = 0
    for server in servers: threading.Thread(target=server.serve_forever, daemon=True).start()
    checks = 0; m = None
    def check(passed, label):
        nonlocal checks
        checks += 1; print(("ok   " if passed else "FAIL ") + label, flush=True)
        if not passed: raise RuntimeError(label)
    def command(text, marker, timeout=90):
        offset = len(m.log()); m.send(text + "\r"); check(m.wait(marker, offset, timeout), marker); return m.log()[offset:]
    def status(text, name, code=0, timeout=90): return command(text + "; echo " + name + "=$?", name + "=" + str(code), timeout)
    def boot(label, disk=None, network=True):
        machine = h.Machine(args.keep / label, disk, "session=headless" if disk else None,
            network=["-netdev", "user,id=net0", "-device", "e1000e,netdev=net0,id=nic0",
                     "-qmp", f"unix:{args.keep / label / 'qmp.sock'},server=on,wait=off"] if network else [])
        if not machine.wait("headless ready", timeout=600): machine.stop(); raise RuntimeError(label + " boot failed")
        check(True, label + " boot without GPU"); return machine
    def link(up):
        with socket.socket(socket.AF_UNIX) as qmp:
            qmp.settimeout(10); qmp.connect(str(m.directory / "qmp.sock")); reader = qmp.makefile("rb")
            json.loads(reader.readline())
            for id_, command_, arguments in [(1, "qmp_capabilities", {}), (2, "set_link", {"name": "nic0", "up": up})]:
                qmp.sendall((json.dumps({"execute": command_, "arguments": arguments, "id": id_}) + "\n").encode())
                while True:
                    response = json.loads(reader.readline())
                    if response.get("id") == id_: break
                if "error" in response: raise RuntimeError(str(response))
        time.sleep(0.3)
    secret = "M3!" + os.urandom(8).hex()
    def shutdown(label):
        command("poweroff", "all filesystems unmounted"); check(m.process.wait(timeout=60) == 0, label + " clean poweroff")
        disk = m.disk; m.stop(); return disk
    def login():
        offset = len(m.log()); m.send("login root\r"); check(m.wait("Password: ", offset), "hidden root login prompt")
        offset = len(m.log()); m.send(secret + "\r"); check(m.wait("authenticated as root", offset), "root authenticated")
    try:
        m = boot("live")
        check(m.wait("network: online address=10.0.2.15", timeout=60), "DHCP boot address")
        output = status("ip", "M3_STATUS")
        check("gateway=10.0.2.2" in output and "dns=10.0.2.3" in output and "state=online" in output, "DHCP route and DNS")
        status("nslookup localhost", "M3_HOSTS")
        output = status(f"nslookup mirror.home 10.0.2.2 {dns}", "M3_DNS")
        check("mirror.home address=10.0.2.2" in output, "DNS ignores wrong transaction and accepts correlated answer")
        status(f"nslookup reject.home 10.0.2.2 {dns}", "M3_BAD_DNS", 1)
        status("ping 10.0.2.2", "M3_PING")
        status(f"nc -z 10.0.2.2 {http}", "M3_TCP")
        status(f"ntp 10.0.2.2 {ntp}", "M3_NTP")
        output = status("date -u", "M3_DATE")
        check("Sat Oct  3 07:06:" in output, "NTP software UTC updated")  # 4000000000 = 2026-10-03 07:06:40
        url = f"http://10.0.2.2:{http}"
        output = status(f"fetch {url}/ok /tmp/download", "M3_FETCH")
        check("fetch complete bytes=" + str(len(PAYLOAD)) in output and "fetch progress" in output, "streaming payload and progress")
        output = status("wc -c /tmp/download", "M3_SIZE"); check(str(len(PAYLOAD)) in output, "guest downloaded byte count")
        status(f"fetch {url}/ok /tmp/download", "M3_EXISTS", 1)
        status("echo preserved > /tmp/original", "M3_SEED")
        for path in ("oversized", "headers", "encoding", "truncated", "stall"):
            status(f"fetch --replace {url}/{path} /tmp/original", "M3_" + path.upper(), 1)
            output = status("cat /tmp/original", "M3_KEEP_" + path.upper()); check("preserved\n" in output, path + " preserves original")
        output = status(f"fetch {url}/retry /tmp/retried", "M3_RETRY")
        check("fetch retry 2/2" in output and "fetch complete" in output, "one bounded retry succeeds")
        servers[0].slow.clear(); offset = len(m.log()); m.send(f"fetch --replace {url}/slow /tmp/original\r")
        check(servers[0].slow.wait(30), "cancellable transfer reached server"); m.socket.sendall(b"\x03")
        check(m.wait("[console] ", offset, timeout=30), "cancelled transfer returns prompt")
        output = status("echo cancel=$?", "M3_CANCEL_QUERY"); check("cancel=130" in output, "Ctrl+C returns status 130")
        output = status("cat /tmp/original", "M3_CANCEL_KEEP"); check("preserved\n" in output, "cancellation preserves original")
        output = status("ls -a /tmp", "M3_CLEANUP"); check(".fetch-" not in output, "temporary downloads cleaned")
        servers[0].slow.clear(); m.send(f"fetch --replace {url}/slow /tmp/original; echo M3_DISCONNECTED=$?\r")
        check(servers[0].slow.wait(30), "disconnect transfer reached server"); link(False)
        check(m.wait("M3_DISCONNECTED=1", timeout=30), "disconnected transfer fails")
        output = status("ip", "M3_LINK_DOWN"); check("link=down" in output, "hardware link status change")
        output = status("cat /tmp/original", "M3_DISCONNECT_KEEP"); check("preserved\n" in output, "disconnect preserves original")
        link(True); status(f"nc -z 10.0.2.2 {http}", "M3_LINK_RESTORED")
        status("netcfg off", "M3_OFF")
        status(f"fetch {url}/ok /tmp/offline", "M3_OFFLINE", 1)
        status("netcfg static 10.0.2.15 255.255.255.0 10.0.2.2 10.0.2.3", "M3_STATIC")
        status(f"nc -z 10.0.2.2 {http}", "M3_STATIC_TCP")
        status("netcfg static 10.0.2.15 255.0.255.0 10.0.2.2 10.0.2.3", "M3_BAD_CONFIG", 1)
        output = status("cat /etc/zenith/network.conf", "M3_CONFIG_KEPT"); check("MASK=255.255.255.0" in output, "invalid config leaves saved state intact")
        status("netcfg dhcp", "M3_DHCP_REAPPLY")
        status("install sata0 --yes", "M3_INSTALL", timeout=300)
        live_disk = shutdown("live"); m = boot("installed", live_disk)
        check(m.wait("network: online address=10.0.2.15", timeout=60), "installed DHCP boot")
        status("netcfg static 10.0.2.16 255.255.255.0 10.0.2.2 10.0.2.3", "M3_SAVE_STATIC")
        status(f"netcfg ntp 10.0.2.2 {ntp}", "M3_SAVE_NTP")
        installed = shutdown("configured"); m = boot("saved", installed)
        check(m.wait("network: synchronized via 10.0.2.2", timeout=60), "configured boot NTP")
        output = status("ip", "M3_SAVED"); check("mode=static" in output and "address=10.0.2.16" in output, "installed configuration survives reboot")
        status(f"fetch {url}/ok /home/user/network-file", "M3_INSTALLED_FETCH")
        offset = len(m.log()); m.send("passwd root\r"); check(m.wait("New password: ", offset), "hidden root setup prompt")
        offset = len(m.log()); m.send(secret + "\r"); check(m.wait("Repeat password: ", offset), "hidden root confirmation")
        offset = len(m.log()); m.send(secret + "\r"); check(m.wait("password updated for root", offset), "root test credential set")
        status("su user", "M3_SU")
        status("netcfg off", "M3_USER_CONFIG_DENIED", 1)
        status(f"ntp 10.0.2.2 {ntp}", "M3_USER_NTP_DENIED", 1)
        status(f"fetch {url}/ok /etc/forbidden-download", "M3_USER_PATH_DENIED", 1)
        status(f"fetch {url}/ok /home/user/user-download", "M3_USER_FETCH")
        output = status("ip", "M3_AFTER_DENIAL"); check("operation=idle" in output, "denials release service operation")
        login(); saved = shutdown("downloaded"); m = boot("offline", saved, network=False)
        login()
        output = status("ip", "M3_NO_NIC"); check("state=offline" in output and "eth0" not in output, "missing NIC remains usable headless")
        status(f"nc -z 10.0.2.2 {http}", "M3_NO_NIC_ERROR", 1)
        status("wc -c /home/user/network-file", "M3_PERSISTENT_FILE")
        disk = shutdown("offline"); m = None
        check(all(secret not in log.read_text(errors="replace") for log in args.keep.glob("*/serial.log")), "credentials absent from transcripts")
        # Inspect the final ext2 partition independently of the guest VFS.
        volume = args.keep / "checked-root.img"
        with disk.open("rb") as source:
            source.seek(512); header = source.read(512); entries, _, stride = struct.unpack_from("<QII", header, 72)
            source.seek(entries * 512 + stride); entry = source.read(stride); start, end = struct.unpack_from("<QQ", entry, 32)
            if end < start or end - start + 1 > 512 * 1024 * 1024 // 512: raise RuntimeError("invalid root partition")
            source.seek(start * 512)
            with volume.open("wb") as target:
                remaining = (end - start + 1) * 512
                while remaining:
                    data = source.read(min(remaining, 1024 * 1024))
                    if not data: raise RuntimeError("short root partition")
                    target.write(data); remaining -= len(data)
        fsck = subprocess.run(["e2fsck", "-f", "-n", str(volume)], capture_output=True)
        (args.keep / "e2fsck.log").write_bytes(fsck.stdout + fsck.stderr); check(fsck.returncode == 0, "independent e2fsck clean")
        for path in ("network-file", "user-download"):
            content = subprocess.run(["debugfs", "-R", "cat /home/user/" + path, str(volume)], capture_output=True)
            check(content.returncode == 0 and content.stdout == PAYLOAD, "independent payload bytes " + path)
        print(f"RESULT: {checks}/{checks} network checks passed", flush=True)
    finally:
        if m: m.stop()
        servers[0].stop.set()
        for server in servers: server.shutdown(); server.server_close()

if __name__ == "__main__": main()
