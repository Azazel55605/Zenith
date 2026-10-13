#!/usr/bin/env python3
"""Release orchestration tests; real signing/publication and HTTP delivery fixtures."""
import argparse
import functools
import http.server
import importlib.util
import json
from pathlib import Path
import shutil
import socketserver
import subprocess
import tempfile
import threading
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent.parent


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec); spec.loader.exec_module(result)
    return result


release = module("release", ROOT / "deploy/distributor/release.py")
fixtures = module("fixtures", ROOT / "tools/test_publish_package.py")


class ReleaseTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        fixtures.PublicationTests.setUpClass.__func__(cls)

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="zenith-release-workflow-")
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name); self.root = self.repo / "published"; self.root.mkdir()
        self.keys = self.repo / "keys"; shutil.copytree(self.source / "keys", self.keys); self.keys.chmod(0o700)
        self.config = dict(keys=str(self.keys), root=str(self.root), base_url="http://127.0.0.1", tls_ca=None)

    def publish(self):
        with patch.object(release, "REPO", self.repo), patch.object(release, "phase"):
            return release.publish_existing(self.config, "0.0.2", self.source / "kernel.zpk", "a" * 40)

    def test_saved_configuration_is_data_and_does_not_touch_keys(self):
        target = self.repo / "config/distributor.json"
        args = argparse.Namespace(url="https://mirror.home/", keys=self.keys, root=self.root, tls_ca=None)
        before = (self.keys / "private.pem").read_bytes()
        with patch.object(release, "CONFIG", target):
            release.configure(args)
            saved = release.configuration()
        self.assertEqual("https://mirror.home", saved["base_url"])
        self.assertEqual(0o600, target.stat().st_mode & 0o777)
        self.assertEqual(before, (self.keys / "private.pem").read_bytes())

    def test_versions_and_credential_urls_are_rejected_early(self):
        for value in ("../0.0.2", "01.2", "main", "0.0.2;touch /tmp/file"):
            with self.assertRaises(ValueError): release.version(value)
        for value in ("ftp://mirror.home", "https://user:secret@mirror.home", "https://mirror.home/?token=secret"):
            with self.assertRaises(ValueError): release.base_url(value)

    def test_signing_permissions_and_served_keys_are_refused(self):
        self.keys.chmod(0o755)
        with self.assertRaisesRegex(ValueError, "mode 700"): release.paths(self.config, signing=True)
        self.keys.chmod(0o700); (self.keys / "private.pem").chmod(0o644)
        with self.assertRaisesRegex(ValueError, "chmod 600"): release.paths(self.config, signing=True)
        with self.assertRaisesRegex(ValueError, "outside"):
            release.paths(dict(self.config, root=str(self.repo)))

    def test_publish_existing_verifies_signed_archive_and_preserves_immutability(self):
        receipt = self.publish()
        self.assertEqual("a" * 40, receipt["source_commit"])
        with self.assertRaisesRegex(ValueError, "already published"): self.publish()
        self.assertEqual(receipt, json.loads((self.root / "core/zenith-kernel/0.0.2/release.json").read_text()))

    def test_build_failure_never_signs_or_publishes_and_keeps_log(self):
        calls = []
        def phase(label, command, log):
            calls.append(label); log.write_text("fixture host test failure\n")
            raise ValueError("Host tests failed")
        with patch.object(release, "REPO", self.repo), patch.object(release, "clean"), \
             patch.object(release, "source_commit", return_value="a" * 40), \
             patch.object(release, "prerequisites"), patch.object(release, "phase", side_effect=phase):
            with self.assertRaisesRegex(ValueError, "Host tests failed"): release.build(self.config, "0.0.3")
        self.assertEqual(["Host tests"], calls)
        self.assertEqual([], list(self.root.iterdir()))
        self.assertEqual(1, len(list((self.repo / "obj/distributor").glob("release-*/tests.log"))))

    def test_source_change_blocks_signing_and_publication(self):
        calls = []
        with patch.object(release, "REPO", self.repo), patch.object(release, "clean"), \
             patch.object(release, "source_commit", side_effect=["a" * 40, "b" * 40]), \
             patch.object(release, "prerequisites"), \
             patch.object(release, "phase", side_effect=lambda label, *args: calls.append(label)):
            with self.assertRaisesRegex(ValueError, "changed during build"): release.build(self.config, "0.0.3")
        self.assertEqual(["Host tests", "Kernel build"], calls)
        self.assertEqual([], list(self.root.iterdir()))

    def test_build_orchestration_uses_real_signature_and_publisher(self):
        original = release.phase; calls = []
        def phase(label, command, log):
            calls.append(label)
            if label == "Kernel build":
                output = Path(command[-1]); output.mkdir()
                shutil.copyfile(self.source / "kernel.elf", output / "Zenith.elf")
            if label == "Sign kernel" or label.startswith("Index "): original(label, command, log)
            else: log.write_text("host/build fixture stage\n")
        with patch.object(release, "REPO", self.repo), patch.object(release, "clean"), \
             patch.object(release, "source_commit", return_value="a" * 40), \
             patch.object(release, "prerequisites"), patch.object(release, "phase", side_effect=phase):
            release.build(self.config, "0.0.3")
        self.assertEqual(["Host tests", "Kernel build", "Package tool", "Sign kernel", "Index core", "Index extra"], calls)
        self.assertTrue((self.root / "core/zenith-kernel/0.0.3/zenith-kernel.zpk").is_file())

    def test_phase_failure_reports_useful_tail_and_retains_full_log(self):
        log = self.repo / "failure.log"
        with patch.object(release, "REPO", self.repo):
            with self.assertRaisesRegex(ValueError, "actual failure reason"):
                release.phase("Fixture", ["python3", "-c", "print('actual failure reason'); raise SystemExit(1)"], log)
        self.assertIn("actual failure reason", log.read_text())

    def test_data_build_sign_publish_uses_committed_payload_and_extra_identity(self):
        payload = self.repo / "payload"; shutil.copytree(self.source / "data", payload)
        original = release.phase
        def phase(label, command, log):
            if label == "Package tool": log.write_text("existing fixture tool\n")
            else: original(label, command, log)
        with patch.object(release, "REPO", self.repo), patch.object(release, "clean"), \
             patch.object(release, "source_commit", return_value="a" * 40), patch.object(release, "phase", side_effect=phase):
            release.data(self.config, "zenith-docs", "0.0.2", payload, "extra")
        target = self.root / "extra/zenith-docs/0.0.2"
        receipt = json.loads((target / "release.json").read_text())
        self.assertEqual("data", receipt["kind"]); self.assertEqual("extra", receipt["repository"])
        release.publisher.verify(release.TOOL, target / "zenith-docs.zpk", self.keys / "public.pem",
                                 "data", "zenith-docs", "0.0.2", "extra")

    def test_external_payload_and_invalid_name_are_refused(self):
        with patch.object(release, "REPO", self.repo):
            with self.assertRaisesRegex(ValueError, "inside the committed"):
                release.data(self.config, "zenith-docs", "0.0.2", self.source / "data", "extra")
            with self.assertRaisesRegex(ValueError, "invalid package name"):
                release.data(self.config, "../escape", "0.0.2", self.repo, "extra")

    def test_web_delivery_must_match_locally_verified_package(self):
        self.publish()
        class Handler(http.server.SimpleHTTPRequestHandler):
            def log_message(self, *args): pass
        handler = functools.partial(Handler, directory=str(self.root))
        with socketserver.TCPServer(("127.0.0.1", 0), handler) as server:
            thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
            config = dict(self.config, base_url="http://127.0.0.1:" + str(server.server_address[1]))
            try:
                release.verify(config, "0.0.2")
                receipt = self.root / "core/zenith-kernel/0.0.2/release.json"
                data = json.loads(receipt.read_text()); data["archive_bytes"] += 1; receipt.write_text(json.dumps(data))
                with self.assertRaisesRegex(ValueError, "disagrees"): release.verify(config, "0.0.2")
            finally: server.shutdown(); thread.join(timeout=5)

    def test_concurrent_operation_is_refused(self):
        with patch.object(release, "REPO", self.repo):
            lock, _ = release.locked_work()
            with lock:
                with self.assertRaisesRegex(ValueError, "another distributor"): release.locked_work()


if __name__ == "__main__": unittest.main()
