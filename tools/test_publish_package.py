#!/usr/bin/env python3
"""Test real signature verification, immutable publication and static delivery."""
import fcntl
import functools
import hashlib
import http.server
import importlib.util
import json
import os
from pathlib import Path
import socketserver
import struct
import subprocess
import tempfile
import threading
import unittest
from unittest.mock import patch
import urllib.request

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("publisher", ROOT / "tools/publish-package.py")
publisher = importlib.util.module_from_spec(spec); spec.loader.exec_module(publisher)
TOOL = ROOT / "tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll"
COMMIT = "a" * 40


def run(*args):
    subprocess.run(["dotnet", str(TOOL), *map(str, args)], check=True,
                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60)


class PublicationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        subprocess.run(["dotnet", "build", str(TOOL.parents[3]), "-c", "Release", "--nologo"],
                       check=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)
        cls.fixtures = tempfile.TemporaryDirectory(prefix="zenith-publisher-fixtures-")
        cls.addClassCleanup(cls.fixtures.cleanup)
        cls.source = Path(cls.fixtures.name)
        run("keygen", cls.source / "keys"); run("keygen", cls.source / "other-keys")
        elf = bytearray(128); elf[:7] = b"\x7fELF\x02\x01\x01"
        elf[16] = 2; elf[18] = 62; elf[20] = 1; elf[52] = 64; elf[54] = 56; elf[56] = 1
        struct.pack_into("<QQ", elf, 24, 0x1000, 64)
        struct.pack_into("<IIQQQQQ", elf, 64, 1, 1, 120, 0x1000, 0, 8, 8)
        (cls.source / "kernel.elf").write_bytes(elf)
        run("kernel-seal", "0.0.2", cls.source / "kernel.elf", cls.source / "keys/private.pem", cls.source / "kernel.zpk")
        data = cls.source / "data/usr/share/doc/example.txt"; data.parent.mkdir(parents=True); data.write_text("hello Zenith\n")
        run("build", "zenith-docs", "0.0.2", cls.source / "data", cls.source / "bundle", "--repo", "extra")
        run("seal", cls.source / "bundle", cls.source / "keys/private.pem", cls.source / "data.zpk")

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="zenith-publication-"); self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name); self.root = self.directory / "published"; self.root.mkdir()

    def publish(self, **changes):
        args = dict(root=self.root, package=self.source / "kernel.zpk", key=self.source / "keys/public.pem",
                    tool=TOOL, kind="kernel", name="zenith-kernel", version="0.0.2", repository="core", commit=COMMIT)
        args.update(changes); return publisher.publish(**args)

    def test_kernel_signature_roundtrip_and_static_http_bytes(self):
        target, receipt = self.publish()
        self.assertEqual({"zenith-kernel.zpk", "public.pem", "release.json"}, {p.name for p in target.iterdir()})
        self.assertFalse(receipt["signed_index"])
        self.assertEqual(receipt, json.loads((target / "release.json").read_text()))
        self.assertEqual(COMMIT, receipt["source_commit"])
        self.assertEqual(hashlib.sha256((self.source / "keys/public.pem").read_bytes()).hexdigest(), receipt["public_pem_sha256"])
        class Handler(http.server.SimpleHTTPRequestHandler):
            def log_message(self, *args): pass
        handler = functools.partial(Handler, directory=str(self.root))
        with socketserver.TCPServer(("127.0.0.1", 0), handler) as server:
            thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
            try:
                url = f"http://127.0.0.1:{server.server_address[1]}/core/zenith-kernel/0.0.2/zenith-kernel.zpk"
                with urllib.request.urlopen(url, timeout=5) as response:
                    received = response.read()
                    self.assertEqual(len(received), int(response.headers["Content-Length"]))
                self.assertEqual((self.source / "kernel.zpk").read_bytes(), received)
                self.assertEqual(receipt["archive_sha256"], hashlib.sha256(received).hexdigest())
                fetched = self.directory / "fetched.zpk"; fetched.write_bytes(received)
                run("kernel-check", fetched, target / "public.pem")
            finally: server.shutdown(); thread.join(timeout=5)

    def test_extra_data_package_identity(self):
        target, receipt = self.publish(package=self.source / "data.zpk", kind="data", name="zenith-docs", repository="extra")
        self.assertEqual(self.root / "extra/zenith-docs/0.0.2", target)
        run("check", target / "zenith-docs.zpk", target / "public.pem")
        self.assertEqual("data", receipt["kind"])

    def test_service_umask_does_not_hide_public_repository_parents(self):
        previous = os.umask(0o077)
        try: target, _ = self.publish()
        finally: os.umask(previous)
        for path in (self.root / 'core', self.root / 'core/zenith-kernel', target):
            self.assertEqual(0o755, path.stat().st_mode & 0o777)
        for path in target.iterdir(): self.assertEqual(0o644, path.stat().st_mode & 0o777)

    def test_tampered_signature_and_wrong_key_leave_no_release(self):
        bad = self.directory / "bad.zpk"; content = bytearray((self.source / "kernel.zpk").read_bytes())
        content[-1] ^= 1; bad.write_bytes(content)
        for changes in ({"package": bad}, {"key": self.source / "other-keys/public.pem"}):
            with self.assertRaisesRegex(ValueError, "verification failed"): self.publish(**changes)
            self.assertFalse((self.root / "core/zenith-kernel/0.0.2").exists())
            self.assertEqual([], list(self.directory.glob(".published-staging-*")))

    def test_mismatched_signed_identity_is_refused(self):
        with self.assertRaisesRegex(ValueError, "identity"): self.publish(version="0.0.3")
        with self.assertRaisesRegex(ValueError, "identity"):
            self.publish(package=self.source / "data.zpk", kind="data", name="zenith-docs", repository="core")

    def test_existing_version_cannot_be_replaced(self):
        target, receipt = self.publish()
        with self.assertRaisesRegex(ValueError, "already published"): self.publish()
        self.assertEqual(receipt["archive_sha256"], publisher.digest(target / "zenith-kernel.zpk"))

    def test_traversal_and_symlink_publication_are_refused(self):
        for changes in ({"name": "../secret"}, {"version": "../0.0.2"}, {"commit": "main"}):
            with self.assertRaises(ValueError): self.publish(**changes)
        (self.root / "core").symlink_to(self.source, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "symlink"): self.publish()

    def test_private_key_is_never_published(self):
        with self.assertRaisesRegex(ValueError, "public SPKI"): self.publish(key=self.source / "keys/private.pem")
        self.assertEqual([], list(self.root.iterdir()))

    def test_failed_staging_never_exposes_a_partial_release(self):
        with patch.object(publisher, "sync_directory", side_effect=OSError("test storage failure")):
            with self.assertRaisesRegex(OSError, "storage failure"): self.publish()
        self.assertFalse((self.root / "core/zenith-kernel/0.0.2").exists())
        self.assertEqual([], list(self.directory.glob(".published-staging-*")))

    def test_release_is_invisible_until_verification_finishes(self):
        original = publisher.verify; seen = []
        def verify(*args):
            self.assertFalse((self.root / "core/zenith-kernel/0.0.2").exists())
            self.assertEqual([], list(self.root.glob("**/*.zpk")))
            seen.append(True); original(*args)
        with patch.object(publisher, "verify", verify): target, _ = self.publish()
        self.assertEqual([True], seen); self.assertTrue((target / "zenith-kernel.zpk").is_file())

    def test_concurrent_publisher_fails_without_release(self):
        with (self.directory / ".published-publish.lock").open("w") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            with self.assertRaises(BlockingIOError): self.publish()
        self.assertEqual([], list(self.root.iterdir()))


if __name__ == "__main__": unittest.main()
