import struct
import tempfile
import unittest
from pathlib import Path
from dhcp_capture import broadcast_requests


def capture(destination=b"\xff" * 6, endian="<", types=(1, 3)):
    mac = b"\x00\x0c\x29\x55\xc5\x54"
    result = struct.pack(endian + "IHHIIII", 0xa1b2c3d4, 2, 4, 0, 0, 65535, 1)
    for message in types:
        frame = bytearray(342)
        frame[:14] = destination + mac + b"\x08\x00"
        frame[14] = 0x45
        frame[23] = 17
        frame[30:34] = b"\xff" * 4
        frame[34:38] = b"\x00\x44\x00\x43"
        frame[42:45] = b"\x01\x01\x06"
        frame[70:76] = mac
        frame[278:285] = b"\x63\x82\x53\x63\x35\x01" + bytes([message])
        result += struct.pack(endian + "IIII", 0, 0, len(frame), len(frame)) + frame
    return result


class DhcpCaptureTests(unittest.TestCase):
    def read(self, data):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "wire.pcap"
            path.write_bytes(data)
            return broadcast_requests(path)

    def test_broadcast_exchange_in_both_pcap_byte_orders(self):
        for endian in ("<", ">"):
            self.assertEqual(self.read(capture(endian=endian)), [1, 3])

    def test_rejects_observed_vmware_zero_destination(self):
        with self.assertRaisesRegex(ValueError, "both IPv4 and Ethernet"):
            self.read(capture(destination=bytes(6)))

    def test_requires_request_after_discover(self):
        with self.assertRaisesRegex(ValueError, "both DHCP"):
            self.read(capture(types=(1,)))

    def test_rejects_truncated_capture(self):
        with self.assertRaisesRegex(ValueError, "truncated"):
            self.read(capture()[:-1])

    def test_rejects_source_mac_mismatch(self):
        data = bytearray(capture())
        data[24 + 16 + 70] ^= 1
        with self.assertRaisesRegex(ValueError, "source MAC"):
            self.read(data)


if __name__ == "__main__":
    unittest.main()
