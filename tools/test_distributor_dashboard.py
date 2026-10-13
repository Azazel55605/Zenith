#!/usr/bin/env python3
"""Management API acceptance on a disposable HTTP fixture; systemd start is stubbed."""
import fcntl
from http.server import ThreadingHTTPServer
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import threading
import unittest
from unittest.mock import patch
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("dashboard", ROOT / "deploy/distributor/dashboard.py")
d = importlib.util.module_from_spec(spec); spec.loader.exec_module(d)


class DashboardTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name); self.state = self.root / "state"; self.state.mkdir()
        self.packages = self.root / "packages"; self.packages.mkdir()
        self.manager = d.Dashboard(self.state, self.packages)
        self.records = {"kernel/v0.0.7": dict(commit="a" * 40, status="complete", started=100)}
        (self.state / "releases.json").write_text(json.dumps(self.records))
        (self.state / "kernel-v0.0.7.log").write_text("Host tests passed\n")
        self.properties = patch.object(d, "system_properties", side_effect=self.props)
        self.properties.start(); self.addCleanup(self.properties.stop)
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), d.Handler); self.server.dashboard = self.manager
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True); self.thread.start()
        self.addCleanup(self.stop)
        self.url = "http://127.0.0.1:" + str(self.server.server_port)

    def stop(self):
        self.server.shutdown(); self.server.server_close(); self.thread.join()

    def props(self, unit, properties):
        if unit == d.SERVICE: return dict(LoadState="loaded", ActiveState="inactive", Result="success", ExecMainStartTimestamp="Tue 2026-10-13 12:00:00 UTC")
        return dict(ActiveState="active", TimersMonotonic="{ OnUnitInactiveUSec=15min ; next_elapse=1h }", NextElapseUSecMonotonic="1h")

    def request(self, path, body=None, headers=None):
        request = urllib.request.Request(self.url + path, data=body, headers=headers or {})
        try: response = urllib.request.urlopen(request)
        except urllib.error.HTTPError as error: response = error
        return response.status, response.read(), response.headers

    def valid_headers(self):
        return {"Origin": self.url, "Content-Type": "application/json", "X-Zenith-CSRF": self.manager.csrf}

    def test_real_status_and_static_routes(self):
        code, body, headers = self.request("/manage/api/status")
        result = json.loads(body)
        self.assertEqual(200, code); self.assertEqual("15min", result["timer"]["interval"])
        self.assertEqual("kernel/v0.0.7", result["builds"][0]["tag"])
        self.assertIn("frame-ancestors 'none'", headers["Content-Security-Policy"])
        for path in ("/manage/", "/manage/app.js", "/manage/style.css"):
            self.assertEqual(200, self.request(path)[0])

    def test_exact_start_command_and_no_arguments(self):
        with patch.object(d.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)) as run:
            self.assertEqual(202, self.request("/manage/api/run", b"{}", self.valid_headers())[0])
        self.assertEqual(["/usr/bin/sudo", "-n", "/usr/bin/systemctl", "start", "--no-block", d.SERVICE], run.call_args.args[0])
        with patch.object(d.subprocess, "run") as run:
            self.assertEqual(503, self.request("/manage/api/run", b'{"command":"reboot"}', self.valid_headers())[0])
            run.assert_not_called()

    def test_cross_site_missing_token_and_oversized_requests_refused(self):
        with patch.object(self.manager, "run") as run:
            headers = self.valid_headers(); headers["Origin"] = "https://other.invalid"
            self.assertEqual(403, self.request("/manage/api/run", b"{}", headers)[0])
            self.assertEqual(403, self.request("/manage/api/run", b"{}")[0])
            self.assertEqual(503, self.request("/manage/api/run", b" " * 1025, self.valid_headers())[0])
            run.assert_not_called()

    def test_poll_lock_disables_run_and_marks_build_running(self):
        with (self.state / "poll.lock").open("a") as lock:
            fcntl.flock(lock.fileno(), fcntl.LOCK_EX)
            self.assertTrue(self.manager.snapshot()["busy"])
            with patch.object(d.subprocess, "run") as run:
                self.assertEqual(409, self.request("/manage/api/run", b"{}", self.valid_headers())[0])
                run.assert_not_called()

    def test_safe_log_tail_and_traversal_refusal(self):
        self.assertIn("Host tests", self.manager.log("kernel/v0.0.7"))
        with self.assertRaises(ValueError): self.manager.log("../../private.pem")
        path = self.state / "kernel-v0.0.7.log"; path.write_text("x" * 100000)
        self.assertEqual(65536, len(self.manager.log("kernel/v0.0.7")))
        path.unlink(); path.symlink_to(self.root / "secret")
        (self.root / "secret").write_text("private contents")
        with self.assertRaises(OSError): self.manager.log("kernel/v0.0.7")

    def test_output_api_uses_known_id_without_encoded_tag_slash(self):
        code, body, _ = self.request("/manage/api/build-output?build=kernel-v0.0.7")
        self.assertEqual(200, code); self.assertIn("Host tests", json.loads(body)["text"])
        self.assertEqual(400, self.request("/manage/api/build-output?build=../../private.pem")[0])

    def test_detailed_phase_selection_fallback_and_symlink_refusal(self):
        path = self.state / 'kernel-v0.0.7.details.json'
        path.write_text(json.dumps(dict(refreshed=123, phases=[
            dict(name='Host tests', status='passed', started=100, elapsed=4, updated=104, text='tests passed'),
            dict(name='Native kernel', status='running', started=105, elapsed=1600, updated=1705,
                 text='ok rollback verified', diagnostic='falling back to tcg')])) )
        route='/manage/api/build-output?build=kernel-v0.0.7'
        code, body, _ = self.request(route); data=json.loads(body)
        self.assertEqual(200, code); self.assertEqual('ok rollback verified', data['text'])
        self.assertEqual('falling back to tcg', data['diagnostic'])
        self.assertEqual('tests passed', json.loads(self.request(route+'&view=0')[1])['text'])
        self.assertIn('Host tests passed', json.loads(self.request(route+'&view=controller')[1])['text'])
        self.assertEqual(400, self.request(route+'&view=100')[0])
        self.assertEqual(400, self.request(route+'&view=../../private.pem')[0])
        path.unlink(); path.symlink_to(self.root / 'secret'); (self.root / 'secret').write_text('private')
        self.assertNotIn(b'private', self.request(route)[1])

    def test_inventory_groups_and_rejects_malformed_or_symlink_receipts(self):
        path = self.packages / "extra/zenith-docs/0.1.0"; path.mkdir(parents=True)
        receipt = path / "release.json"
        receipt.write_text(json.dumps(dict(repository="extra", name="zenith-docs", version="0.1.0", archive_bytes=2000)))
        rows, warnings = self.manager.inventory(); self.assertEqual("extra", rows[0]["repository"]); self.assertEqual([], warnings)
        receipt.write_text("invalid"); self.assertEqual([], self.manager.inventory()[0])
        receipt.unlink(); receipt.symlink_to(self.root / "secret")
        (self.root / "secret").write_text("private contents")
        self.assertEqual([], self.manager.inventory()[0])

    def test_unreadable_repository_preserves_other_packages_and_names_error(self):
        path = self.packages / 'core/zenith-kernel/0.0.7'; path.mkdir(parents=True)
        (path / 'release.json').write_text(json.dumps(dict(repository='core', name='zenith-kernel', version='0.0.7', archive_bytes=16000000)))
        original = Path.iterdir
        def listing(path):
            if path == self.packages / 'extra': raise PermissionError(13, 'Permission denied', str(path))
            return original(path)
        with patch.object(Path, 'iterdir', listing):
            snapshot = self.manager.snapshot()
        self.assertEqual('zenith-kernel', snapshot['packages'][0]['name'])
        self.assertIn('Cannot list extra: Permission denied', snapshot['warnings'])

    def test_unavailable_systemd_preserves_inventory_and_disables_action(self):
        self.properties.stop()
        with patch.object(d, "system_properties", side_effect=ValueError("unavailable")):
            result = self.manager.snapshot()
            self.assertTrue(result["warnings"]); self.assertEqual("unknown", result["timer"]["state"])
            self.assertEqual(503, self.manager.run()[0])

    def test_abandoned_build_is_interrupted_and_duration_parser(self):
        self.records["kernel/v0.0.7"]["status"] = "building"
        (self.state / "releases.json").write_text(json.dumps(self.records))
        self.assertEqual("interrupted", self.manager.snapshot()["builds"][0]["status"])
        self.assertAlmostEqual(87311.502615, d.duration("1d 15min 11.502615s"))
        self.assertIsNone(d.duration("not a timestamp"))

    def test_corrupt_display_fields_do_not_break_status(self):
        self.records["kernel/v0.0.7"].update(status={"bad": True}, started=["bad"])
        (self.state / "releases.json").write_text(json.dumps(self.records))
        record = self.manager.snapshot()["builds"][0]
        self.assertEqual("unknown", record["status"]); self.assertIsNone(record["started"])

    def test_failed_service_is_visible_even_before_a_tag_build_starts(self):
        self.properties.stop()
        with patch.object(d, "system_properties", return_value={"LoadState": "loaded", "ActiveState": "failed", "Result": "exit-code"}):
            self.assertTrue(any("Last distributor poll failed" in item for item in self.manager.snapshot()["warnings"]))


if __name__ == "__main__": unittest.main()
