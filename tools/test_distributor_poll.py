#!/usr/bin/env python3
"""Real local Git polling; build execution is stubbed, never uses production keys."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("poll", ROOT / "deploy/distributor/poll.py")
poll = importlib.util.module_from_spec(spec); spec.loader.exec_module(poll)


class PollTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name); self.repo = self.root / "remote"
        subprocess.run(["git", "init", "-b", "main", str(self.repo)], check=True, stdout=subprocess.DEVNULL)
        poll.git(self.repo, "config", "user.email", "fixture@example.test")
        poll.git(self.repo, "config", "user.name", "Fixture")
        (self.repo / "payload").write_text("one")
        poll.git(self.repo, "add", "payload"); poll.git(self.repo, "commit", "-m", "initial")
        self.commit = poll.git(self.repo, "rev-parse", "HEAD")
        self.state = self.root / "state"
        self.recipes = json.loads((ROOT / "deploy/distributor/recipes.json").read_text())
        self.calls = []
        self.config = patch.object(poll.release, "configuration", return_value={}); self.config.start(); self.addCleanup(self.config.stop)
        self.index = patch.object(poll.release, "indexes"); self.index.start(); self.addCleanup(self.index.stop)
        self.runner = patch.object(poll, "launch", side_effect=self.launch); self.mock = self.runner.start(); self.addCleanup(self.runner.stop)

    def launch(self, checkout, version, recipe, log):
        self.calls.append((poll.git(checkout, "rev-parse", "HEAD"), version, recipe["name"]))
        self.assertEqual("", poll.git(checkout, "status", "--porcelain"))
        return 0

    def run_poll(self, retry=None):
        poll.poll(str(self.repo), self.state, self.recipes, retry)

    def test_independent_versions_and_idempotent_pinned_checkouts(self):
        for tag in ("kernel/v0.0.7", "zenith-docs/v0.1.0", "v0.4", "unknown/v1.0.0"):
            poll.git(self.repo, "tag", tag)
        self.run_poll(); self.run_poll()
        self.assertEqual([(self.commit, "0.0.7", "zenith-kernel"), (self.commit, "0.1.0", "zenith-docs")], self.calls)
        self.assertEqual([], list(self.state.glob("build-*")))
        records = json.loads((self.state / "releases.json").read_text())
        self.assertTrue(all(item["status"] == "complete" for item in records.values()))
        self.assertEqual(0o750, self.state.stat().st_mode & 0o777)
        for name in ("releases.json", "poll.lock", "kernel-v0.0.7.log"):
            self.assertEqual(0o640, (self.state / name).stat().st_mode & 0o777)

    def test_failure_is_retained_until_explicit_retry(self):
        poll.git(self.repo, "tag", "kernel/v0.0.7")
        self.mock.side_effect = lambda *args: 1
        with self.assertRaisesRegex(ValueError, "failed releases"): self.run_poll()
        self.assertTrue((self.state / "build-kernel-v0.0.7").exists())
        self.run_poll(); self.assertEqual(1, self.mock.call_count)
        self.mock.side_effect = self.launch
        self.run_poll("kernel/v0.0.7")
        self.assertEqual("complete", json.loads((self.state / "releases.json").read_text())["kernel/v0.0.7"]["status"])
        self.assertNotIn("error", json.loads((self.state / "releases.json").read_text())["kernel/v0.0.7"])

    def test_retry_keeps_ignored_compiler_outputs_but_refuses_source_changes(self):
        (self.repo / ".gitignore").write_text("obj/\n")
        poll.git(self.repo, "add", ".gitignore"); poll.git(self.repo, "commit", "-m", "ignore outputs")
        poll.git(self.repo, "tag", "kernel/v0.0.7")
        self.mock.side_effect = lambda *args: 1
        with self.assertRaises(ValueError): self.run_poll()
        checkout = self.state / "build-kernel-v0.0.7"
        cache = checkout / "obj/compiler-cache"; cache.parent.mkdir(); cache.write_text("cached")
        (checkout / "payload").write_text("dirty")
        with self.assertRaises(ValueError): self.run_poll("kernel/v0.0.7")
        self.assertEqual(1, self.mock.call_count)
        self.assertEqual("cached", cache.read_text())
        poll.git(checkout, "restore", "payload")
        def resumed(*args):
            self.assertEqual("cached", cache.read_text()); return 0
        self.mock.side_effect = resumed
        self.run_poll("kernel/v0.0.7")
        self.assertFalse(checkout.exists())

    def test_retained_checkout_pin_and_local_override_are_refused(self):
        checkout = self.root / "retained"
        poll.prepare_checkout(self.repo, checkout, self.commit)
        with self.assertRaisesRegex(ValueError, "pinned commit"):
            poll.prepare_checkout(self.repo, checkout, "a" * 40)
        (checkout / "cosmos.local.props").write_text("override")
        with self.assertRaisesRegex(ValueError, "dirty|override"):
            poll.prepare_checkout(self.repo, checkout, self.commit)
        self.assertTrue(checkout.exists())

    def test_fresh_publication_does_not_sign_indexes_twice(self):
        with patch.object(poll.release, "REPO", self.repo), patch.object(poll.release, "TOOL"), \
             patch.object(poll.release, "build") as build, patch.object(poll.release, "verify") as verify:
            poll.execute({"root": str(self.root / "published")}, self.repo, "0.0.7", self.recipes["kernel"])
        build.assert_called_once(); verify.assert_called_once(); poll.release.indexes.assert_not_called()

    def test_successful_publication_suppresses_same_poll_daily_renewal(self):
        poll.git(self.repo, "tag", "kernel/v0.0.7")
        self.run_poll(); self.run_poll()
        poll.release.indexes.assert_not_called()

    def test_force_moved_remote_tag_is_refused(self):
        poll.git(self.repo, "tag", "kernel/v0.0.7"); self.run_poll()
        (self.repo / "payload").write_text("two")
        poll.git(self.repo, "commit", "-am", "next")
        poll.git(self.repo, "tag", "-f", "kernel/v0.0.7")
        with self.assertRaises(subprocess.CalledProcessError): self.run_poll()
        self.assertEqual(1, len(self.calls))

    def test_non_main_tag_refused_before_any_build(self):
        poll.git(self.repo, "checkout", "-b", "unreviewed")
        (self.repo / "payload").write_text("branch")
        poll.git(self.repo, "commit", "-am", "branch")
        poll.git(self.repo, "tag", "kernel/v0.0.8")
        with self.assertRaises(subprocess.CalledProcessError): self.run_poll()
        self.assertEqual([], self.calls)

    def test_persisted_pin_rejects_local_tag_rewrite(self):
        records = {"kernel/v0.0.7": dict(commit="a" * 40, status="complete")}
        with self.assertRaisesRegex(ValueError, "tag moved"):
            poll.pin(records, [("kernel/v0.0.7", "b" * 40, "0.0.7", {})])

    def test_index_renewal_only_after_one_day(self):
        self.run_poll(); self.run_poll()
        self.assertEqual(1, poll.release.indexes.call_count)
        poll.save(self.state / "index-renewal.json", {"finished": 0})
        self.run_poll(); self.assertEqual(2, poll.release.indexes.call_count)

    def test_partial_publication_resumes_verification_without_rebuilding(self):
        target = self.root / "published/core/zenith-kernel/0.0.7"
        target.mkdir(parents=True)
        (target / "release.json").write_text(json.dumps({"source_commit": self.commit}))
        with patch.object(poll.release, "REPO", self.repo), patch.object(poll.release, "TOOL"), \
             patch.object(poll.release, "source_commit", return_value=self.commit), \
             patch.object(poll.release, "build") as build, patch.object(poll.release, "verify") as verify, \
             patch.object(poll.subprocess, "run"):
            poll.execute({"root": str(self.root / "published")}, self.repo, "0.0.7", self.recipes["kernel"])
        build.assert_not_called(); verify.assert_called_once()
        poll.release.indexes.assert_called_once()

    def test_existing_release_with_other_provenance_is_refused(self):
        target = self.root / "published/core/zenith-kernel/0.0.7"
        target.mkdir(parents=True)
        (target / "release.json").write_text(json.dumps({"source_commit": "a" * 40}))
        with patch.object(poll.release, "REPO", self.repo), patch.object(poll.release, "TOOL"):
            with self.assertRaisesRegex(ValueError, "another source commit"):
                poll.execute({"root": str(self.root / "published")}, self.repo, "0.0.7", self.recipes["kernel"])

    def test_manual_environment_finds_user_tools_in_children(self):
        home = self.root / "home"; tools = home / ".dotnet/tools"; tools.mkdir(parents=True)
        command = tools / "cosmos.patcher"
        command.write_text("#!/bin/sh\nprintf 'fixture-cosmos\\n'\n"); command.chmod(0o700)
        with patch.dict(os.environ, HOME=str(home), PATH="/usr/bin:/bin"):
            poll.configure_environment(); first = os.environ["PATH"]
            poll.configure_environment(); self.assertEqual(first, os.environ["PATH"])
            self.assertIn("/usr/sbin", first.split(os.pathsep))
            self.assertIn("/usr/bin", first.split(os.pathsep))
            self.assertEqual("fixture-cosmos", subprocess.check_output(["cosmos.patcher"], text=True).strip())
            child = subprocess.check_output([sys.executable, "-c", "import shutil; print(shutil.which('cosmos.patcher'))"], text=True).strip()
            self.assertEqual(str(command), child)

    def test_main_configures_path_before_execute(self):
        with patch.object(sys, "argv", ["poll.py", "--execute", str(self.repo), "--version", "0.0.7", "--recipe", json.dumps(self.recipes["kernel"])]), \
             patch.object(poll, "configure_environment") as configure, patch.object(poll, "execute") as execute:
            self.assertEqual(0, poll.main())
            configure.assert_called_once(); execute.assert_called_once()

    def test_queued_retry_is_consumed_by_background_poll(self):
        poll.git(self.repo, "tag", "kernel/v0.0.7")
        self.mock.side_effect = lambda *args: 1
        with self.assertRaises(ValueError): self.run_poll()
        poll.queue_retry(self.state, "kernel/v0.0.7")
        self.mock.side_effect = self.launch; self.run_poll()
        self.assertFalse((self.state / "retry-request.json").exists())
        self.assertEqual("complete", json.loads((self.state / "releases.json").read_text())["kernel/v0.0.7"]["status"])

    def test_cancellation_records_reason_and_waits_for_explicit_retry(self):
        poll.git(self.repo, "tag", "kernel/v0.0.7")
        self.mock.side_effect = KeyboardInterrupt("Received SIGHUP")
        with self.assertRaises(KeyboardInterrupt): self.run_poll()
        record = json.loads((self.state / "releases.json").read_text())["kernel/v0.0.7"]
        self.assertEqual("interrupted", record["status"]); self.assertIn("SIGHUP", record["error"])
        self.run_poll(); self.assertEqual(1, self.mock.call_count)

    def test_launch_terminates_child_on_cancellation(self):
        self.runner.stop()
        with (self.root / "child.log").open("a") as log, patch.object(poll.subprocess, "Popen") as popen:
            process = popen.return_value; process.poll.return_value = None
            process.wait.side_effect = [KeyboardInterrupt(), 0]
            with self.assertRaises(KeyboardInterrupt): poll.launch(self.repo, "0.0.7", self.recipes["kernel"], log)
            process.terminate.assert_called_once()


if __name__ == "__main__": unittest.main()
