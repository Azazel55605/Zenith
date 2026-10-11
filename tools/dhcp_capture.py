"""Check guest DHCP broadcasts in a classic Ethernet PCAP from QEMU filter-dump."""
import struct


def broadcast_requests(path):
    data = path.read_bytes()
    if len(data) < 24 or data[:4] not in (b"\xd4\xc3\xb2\xa1", b"\xa1\xb2\xc3\xd4"):
        raise ValueError("expected classic PCAP")
    endian = "<" if data[:4] == b"\xd4\xc3\xb2\xa1" else ">"
    if struct.unpack_from(endian + "I", data, 20)[0] != 1:
        raise ValueError("expected Ethernet capture")
    messages = []
    position = 24
    while position < len(data):
        if len(data) - position < 16:
            raise ValueError("truncated PCAP record")
        captured, original = struct.unpack_from(endian + "II", data, position + 8)
        position += 16
        if captured != original or captured > len(data) - position:
            raise ValueError("truncated captured frame")
        frame = data[position:position + captured]
        position += captured
        if len(frame) < 42 or frame[12:14] != b"\x08\x00" or frame[23] != 17:
            continue
        header = (frame[14] & 15) * 4
        udp = 14 + header
        if header < 20 or len(frame) < udp + 8:
            raise ValueError("invalid IPv4 header")
        if frame[udp:udp + 4] != b"\x00\x44\x00\x43":
            continue
        if frame[30:34] != b"\xff" * 4 or frame[:6] != b"\xff" * 6:
            raise ValueError("DHCP request must broadcast at both IPv4 and Ethernet layers")
        payload = frame[udp + 8:]
        if len(payload) < 243 or payload[0:3] != b"\x01\x01\x06" or payload[28:34] != frame[6:12]:
            raise ValueError("invalid BOOTP request or source MAC")
        if payload[236:242] != b"\x63\x82\x53\x63\x35\x01" or payload[242] not in (1, 3):
            raise ValueError("expected DHCP Discover or Request")
        messages.append(payload[242])
    if not {1, 3}.issubset(messages):
        raise ValueError("capture must contain both DHCP Discover and Request")
    return messages
