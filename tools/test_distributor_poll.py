#!/usr/bin/env python3
"""Real local Git polling; build execution is stubbed, never uses production keys."""
import importlib.util
import json
from pathlib import Path
import subprocess
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

    def test_failure_is_retained_until_explicit_retry(self):
        poll.git(self.repo, "tag", "kernel/v0.0.7")
        self.mock.side_effect = lambda *args: 1
        with self.assertRaisesRegex(ValueError, "failed releases"): self.run_poll()
        self.assertTrue((self.state / "build-kernel-v0.0.7").exists())
        self.run_poll(); self.assertEqual(1, self.mock.call_count)
        self.mock.side_effect = self.launch
        self.run_poll("kernel/v0.0.7")
        self.assertEqual("complete", json.loads((self.state / "releases.json").read_text())["kernel/v0.0.7"]["status"])

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

    def test_existing_release_with_other_provenance_is_refused(self):
        target = self.root / "published/core/zenith-kernel/0.0.7"
        target.mkdir(parents=True)
        (target / "release.json").write_text(json.dumps({"source_commit": "a" * 40}))
        with patch.object(poll.release, "REPO", self.repo), patch.object(poll.release, "TOOL"):
            with self.assertRaisesRegex(ValueError, "another source commit"):
                poll.execute({"root": str(self.root / "published")}, self.repo, "0.0.7", self.recipes["kernel"])


if __name__ == "__main__": unittest.main()
