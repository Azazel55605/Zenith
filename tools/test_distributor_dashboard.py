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

    def test_retry_failed_interrupted_and_abandoned_builds(self):
        for status in ("failed", "interrupted", "building"):
            self.records["kernel/v0.0.7"]["status"] = status
            (self.state / "releases.json").write_text(json.dumps(self.records))
            before = (self.state / "releases.json").read_bytes()
            with patch.object(d.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)) as run:
                code, body, _ = self.request("/manage/api/retry", b'{"tag":"kernel/v0.0.7"}', self.valid_headers())
            self.assertEqual(202, code, body)
            self.assertEqual(["/usr/bin/sudo", "-n", "-u", "zenith-release", "/usr/bin/python3", "-B",
                              "/opt/zenith-dashboard/retry.py", "kernel/v0.0.7"], run.call_args_list[0].args[0])
            self.assertEqual(["/usr/bin/sudo", "-n", "/usr/bin/systemctl", "start", "--no-block", d.SERVICE], run.call_args_list[1].args[0])
            self.assertEqual(before, (self.state / "releases.json").read_bytes())

    def test_retry_rejects_invalid_complete_unknown_and_cross_site(self):
        with patch.object(d.subprocess, "run") as run:
            for body, code in ((b'{"tag":"kernel/v0.0.7"}', 409), (b'{"tag":"unknown/v1.0"}', 409),
                               (b'{"tag":"--help"}', 400), (b'{"tag":[]}', 400), (b'{"tag":"kernel/v0.0.7","command":"sh"}', 400)):
                self.assertEqual(code, self.request("/manage/api/retry", body, self.valid_headers())[0])
            self.assertEqual(403, self.request("/manage/api/retry", b'{"tag":"kernel/v0.0.7"}')[0])
            run.assert_not_called()

    def test_retry_pending_queue_and_lock_refuse_duplicates(self):
        self.records["kernel/v0.0.7"]["status"] = "failed"
        (self.state / "releases.json").write_text(json.dumps(self.records))
        request = self.state / "retry-request.json"; request.write_text('{"tag":"kernel/v0.0.7"}')
        self.assertEqual("kernel/v0.0.7", self.manager.snapshot()["queued_retry"])
        with patch.object(d.subprocess, "run") as run:
            self.assertEqual(409, self.manager.retry("kernel/v0.0.7")[0]); run.assert_not_called()
        request.unlink()
        with (self.state / "poll.lock").open("a") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            with patch.object(d.subprocess, "run") as run:
                self.assertEqual(409, self.manager.retry("kernel/v0.0.7")[0]); run.assert_not_called()

    def test_retry_reports_helper_and_service_failures(self):
        self.records["kernel/v0.0.7"]["status"] = "failed"
        (self.state / "releases.json").write_text(json.dumps(self.records))
        with patch.object(d.subprocess, "run", return_value=subprocess.CompletedProcess([], 2)) as run:
            self.assertEqual(409, self.manager.retry("kernel/v0.0.7")[0]); self.assertEqual(1, run.call_count)
        with patch.object(d.subprocess, "run", side_effect=[subprocess.CompletedProcess([], 0), subprocess.CompletedProcess([], 1)]):
            code, result = self.manager.retry("kernel/v0.0.7")
        self.assertEqual(503, code); self.assertIn("next distributor timer", result["error"])

    def test_real_status_and_static_routes(self):
        code, body, headers = self.request("/manage/api/status")
        result = json.loads(body)
        self.assertEqual(200, code); self.assertEqual("15min", result["timer"]["interval"])
        self.assertEqual("kernel/v0.0.7", result["builds"][0]["tag"])
        self.assertIn("frame-ancestors 'none'", headers["Content-Security-Policy"])
        for path in ("/manage/", "/manage/app.js", "/manage/style.css"):
            self.assertEqual(200, self.request(path)[0])

    def test_storage_report_and_live_disk_capacity(self):
        (self.state / "storage.json").write_text(json.dumps(dict(allocated=4096, refreshed=10,
            removed=["old-attempt"], policy=dict(attempts=2), free=0)))
        result = json.loads(self.request("/manage/api/status")[1])["storage"]
        self.assertEqual(4096, result["allocated"])
        self.assertEqual(["old-attempt"], result["removed"])
        self.assertGreater(result["free"], 0)
        self.assertGreaterEqual(result["total"], result["free"] + result["used"])

    def test_invalid_and_symlink_storage_reports_preserve_dashboard(self):
        path = self.state / "storage.json"; path.write_text("not-json")
        result = json.loads(self.request("/manage/api/status")[1])
        self.assertIn("Storage report unavailable", result["warnings"])
        self.assertEqual(1, len(result["builds"]))
        path.unlink(); target = self.root / "secret"; target.write_text("private-content")
        path.symlink_to(target)
        body = self.request("/manage/api/status")[1]
        self.assertNotIn(b"private-content", body)
        self.assertIn(b"Storage report unavailable", body)

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

    def test_delivery_failure_after_passed_index_shows_controller_error_by_default(self):
        record=self.records['kernel/v0.0.7']
        record.update(status='failed',error='build/verification failed (worker exit 1)')
        (self.state/'releases.json').write_text(json.dumps(self.records))
        (self.state/'kernel-v0.0.7.log').write_text('Checking complete delivery\nDistributor polling failed: Name or service not known\n')
        (self.state/'kernel-v0.0.7.details.json').write_text(json.dumps(dict(phases=[
            dict(name='Index extra extended',status='passed',started=100,elapsed=.2,updated=101,
                 text='sealed extra generation=15 entries=4')])) )
        route='/manage/api/build-output?build=kernel-v0.0.7'
        data=json.loads(self.request(route)[1])
        self.assertEqual('controller',data['selected'])
        self.assertIn('Name or service not known',data['text'])
        self.assertEqual(record['error'],data['error'])
        selected=json.loads(self.request(route+'&view=0')[1])
        self.assertIn('sealed extra',selected['text'])
        self.assertEqual(record['error'],selected['error'])
        record['status']='complete'
        (self.state/'releases.json').write_text(json.dumps(self.records))
        completed=json.loads(self.request(route)[1])
        self.assertEqual('0',completed['selected'])
        self.assertEqual('',completed['error'])

    def test_failed_phase_remains_selected_and_interrupted_postphase_shows_controller(self):
        self.records['kernel/v0.0.7'].update(status='failed',error='native validation failed')
        (self.state/'releases.json').write_text(json.dumps(self.records))
        details=self.state/'kernel-v0.0.7.details.json'
        row=dict(name='Native kernel',status='failed',started=100,elapsed=30,text='FAIL real assertion')
        details.write_text(json.dumps(dict(phases=[row])))
        result=self.manager.detailed_output('kernel-v0.0.7')
        self.assertEqual('0',result['selected'])
        self.assertEqual('FAIL real assertion',result['text'])
        self.records['kernel/v0.0.7'].update(status='interrupted',error='worker cancelled')
        (self.state/'releases.json').write_text(json.dumps(self.records))
        row['status']='passed';details.write_text(json.dumps(dict(phases=[row])))
        self.assertEqual('controller',self.manager.detailed_output('kernel-v0.0.7')['selected'])

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

    def test_hidden_completions_keep_logs_and_other_states_visible(self):
        self.records['kernel/v0.0.7']['dashboard_hidden'] = True
        for version, status in [('0.0.8', 'failed'), ('0.0.9', 'interrupted'), ('0.0.10', 'pending'), ('0.0.11', 'building')]:
            self.records['kernel/v' + version] = dict(commit='a' * 40, status=status, dashboard_hidden=True)
        (self.state / 'releases.json').write_text(json.dumps(self.records))
        rows = self.manager.snapshot()['builds']
        self.assertEqual(4, len(rows))
        self.assertNotIn('kernel/v0.0.7', [row['tag'] for row in rows])
        self.assertIn('Host tests', self.manager.output('kernel-v0.0.7'))

    def test_newer_success_hides_only_superseded_failures_without_deleting_state(self):
        self.records = {
            'kernel/v0.0.9': dict(status='failed', started=500),
            'kernel/v0.0.10': dict(status='interrupted', started=600),
            'kernel/v0.0.11': dict(status='complete', dashboard_hidden=True, started=100),
            'kernel/v0.0.12': dict(status='failed'),
            'kernel/v0.0.8': dict(status='pending'),
            'kernel/v0.0.6': dict(status='building'),
            'zenith-docs/v0.0.1': dict(status='failed'),
        }
        path=self.state/'releases.json';path.write_text(json.dumps(self.records));before=path.read_bytes()
        (self.state/'kernel-v0.0.9.log').write_text('Retained failure evidence')
        with patch.object(self.manager,'poll_busy',return_value=True):
            code,body,_=self.request('/manage/api/status')
        self.assertEqual(200,code)
        self.assertEqual({'kernel/v0.0.12','kernel/v0.0.8','kernel/v0.0.6','zenith-docs/v0.0.1'},
                         {row['tag'] for row in json.loads(body)['builds']})
        self.assertEqual(before,path.read_bytes())
        self.assertIn('Retained failure evidence',self.manager.output('kernel-v0.0.9'))

    def test_newer_failed_or_running_version_does_not_hide_failures(self):
        self.records = {'kernel/v0.0.9':dict(status='failed'),
                        'kernel/v0.0.10':dict(status='failed'),
                        'kernel/v0.0.11':dict(status='building')}
        (self.state/'releases.json').write_text(json.dumps(self.records))
        self.assertEqual(3,len(self.manager.snapshot()['builds']))

    def test_equal_version_spellings_do_not_hide_failure(self):
        self.records = {'zenith-docs/v1.0':dict(status='failed'),
                        'zenith-docs/v1.0.0':dict(status='complete')}
        (self.state/'releases.json').write_text(json.dumps(self.records))
        self.assertEqual(2,len(self.manager.snapshot()['builds']))

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
