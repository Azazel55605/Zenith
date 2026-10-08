#!/usr/bin/env python3
"""Host regressions for smoke-test serial marker matching (no QEMU required)."""
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import Mock

spec = importlib.util.spec_from_file_location("smoke_test", Path(__file__).with_name("smoke-test.py"))
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


class SerialMatchingTests(unittest.TestCase):
    def matches(self, raw, marker):
        machine = smoke.Machine.__new__(smoke.Machine)
        machine.serial = Mock()
        machine.serial.exists.return_value = True
        machine.serial.read_text.return_value = raw
        machine.process = Mock()
        machine.process.poll.return_value = 0
        return machine.wait_for(marker, 1)

    def test_cosmos_tag_before_intact_marker(self):
        # Captured from the first M2 smoke run: stripping tags loses this marker.
        raw = "[zenith] [   40.[Kernel] Run() returned\n[Kernel] Call350] user: proc-write=1\n"
        self.assertTrue(self.matches(raw, "user: proc-write=1"))

    def test_cosmos_tag_inside_marker(self):
        raw = "user: pro[Kernel] Run() returned\nc-write=1\n"
        self.assertTrue(self.matches(raw, "user: proc-write=1"))

    def test_different_status_does_not_match(self):
        self.assertFalse(self.matches("user: proc-write=0\n", "user: proc-write=1"))


if __name__ == "__main__":
    unittest.main()
