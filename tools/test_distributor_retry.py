"""Retry helper preserves pinned records and serializes with the real controller lock."""
import fcntl
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("retry", Path(__file__).resolve().parents[1]/"deploy/distributor/retry.py")
r = importlib.util.module_from_spec(spec); spec.loader.exec_module(r)


class RetryTests(unittest.TestCase):
    def test_queue_preserves_records_and_refuses_duplicate(self):
        for status in ("failed", "interrupted", "building"):
            with tempfile.TemporaryDirectory() as temp:
                state = Path(temp); records = state/"releases.json"
                records.write_text(json.dumps({"kernel/v0.0.7": dict(status=status, commit="a"*40)}))
                before = records.read_bytes(); r.queue_retry(state, "kernel/v0.0.7")
                self.assertEqual(before, records.read_bytes())
                self.assertEqual({"tag":"kernel/v0.0.7"}, json.loads((state/"retry-request.json").read_text()))
                self.assertEqual(0o640, (state/"retry-request.json").stat().st_mode & 0o777)
                with self.assertRaises(ValueError): r.queue_retry(state, "kernel/v0.0.7")

    def test_refuses_active_complete_unknown_pending_and_invalid(self):
        with tempfile.TemporaryDirectory() as temp:
            state = Path(temp)
            for status in ("complete", "pending", "unknown"):
                (state/"releases.json").write_text(json.dumps({"kernel/v0.0.7": dict(status=status)}))
                with self.assertRaises(ValueError): r.queue_retry(state, "kernel/v0.0.7")
            for tag in ("other/v1.0", "../../tmp/x", "kernel/v0.0.7 --command sh", None):
                with self.assertRaises(ValueError): r.queue_retry(state, tag)
            with (state/"poll.lock").open("a") as lock:
                fcntl.flock(lock, fcntl.LOCK_EX)
                with self.assertRaises(BlockingIOError): r.queue_retry(state, "kernel/v0.0.7")
            self.assertFalse((state/"retry-request.json").exists())

    def test_cli_rejects_extra_arguments(self):
        with patch.object(r.sys, "argv", ["retry.py", "kernel/v0.0.7", "--state", "/tmp"]), patch.object(r, "queue_retry") as queue:
            self.assertEqual(2, r.main()); queue.assert_not_called()


if __name__ == "__main__": unittest.main()
