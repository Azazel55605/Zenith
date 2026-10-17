#!/usr/bin/env python3
import contextlib
import io
from pathlib import Path
import unittest
from guest_pipeline import check


class Machine:
    def __init__(self, text, completed=True):
        self.text = ''; self.result = text; self.completed = completed; self.sent = []; self.waits = []
        self.process = self; self.serial = Path('/fixture/serial.log')
    def log(self): return self.text
    def send(self, text): self.sent.append(text); self.text = self.result
    def wait(self, marker, offset=0, timeout=120):
        self.waits.append((marker, timeout))
        return self.completed if len(self.waits) == 1 else marker in self.text
    def poll(self): return None


class PipelineChecks(unittest.TestCase):
    def run_check(self, machine):
        with contextlib.redirect_stdout(io.StringIO()) as output:
            try: check(machine, 'producer | counter | logger', '163840', 'N_BYTES')
            finally: self.output = output.getvalue()

    def test_exact_output_and_completion_are_both_required(self):
        m = Machine('[zenith] [ 1.0] user: 163840\n[zenith] [ 1.1] user: N_BYTES=0\n')
        self.run_check(m)
        self.assertEqual(1, len(m.sent)); self.assertIn('; echo N_BYTES=$? | logger\r', m.sent[0])
        self.assertIn('pipeline exited 0', self.output)

    def test_wrong_count_is_not_a_substring_success(self):
        m = Machine('user: 1638400\nuser: N_BYTES=0\n')
        with self.assertRaises(RuntimeError): self.run_check(m)
        self.assertIn('1638400', self.output); self.assertEqual(1, len(m.sent))

    def test_expected_output_cannot_mask_failed_completion(self):
        m = Machine('user: 163840\nuser: N_BYTES=1\n', completed=False)
        with self.assertRaises(RuntimeError): self.run_check(m)
        self.assertIn('N_BYTES=1', self.output); self.assertEqual(1, len(m.sent))

    def test_stall_diagnostics_do_not_dump_guest_text_or_repeat_operation(self):
        m = Machine('PRIVATE_INPUT\n[LowLevelMonitor] Acquire BEGIN\n', completed=False)
        with self.assertRaises(RuntimeError): self.run_check(m)
        self.assertNotIn('PRIVATE_INPUT', self.output)
        self.assertIn('/fixture/serial.log', self.output); self.assertEqual(1, len(m.sent))
        self.assertEqual(120, m.waits[0][1])


if __name__ == '__main__': unittest.main()
