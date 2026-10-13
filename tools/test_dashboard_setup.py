#!/usr/bin/env python3
"""Existing nginx site activation and rollback without touching the host service."""
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("setup", ROOT / "deploy/distributor/dashboard-setup.py")
setup = importlib.util.module_from_spec(spec); spec.loader.exec_module(setup)


class SetupTests(unittest.TestCase):
    def test_add_route_preserves_existing_site_and_ignores_comments(self):
        original = '# server { fake }\nserver {\n listen 80;\n root /srv/zenith/packages;\n}\n'
        result = setup.include_route(original)
        self.assertIn("include /etc/nginx/snippets/zenith-dashboard.conf;", result)
        self.assertIn("root /srv/zenith/packages;", result)
        self.assertEqual(result, setup.include_route(result))

    def test_existing_exact_wildcard_and_quoted_include_are_idempotent(self):
        for path in ("/etc/nginx/snippets/zenith-dashboard.conf", "/etc/nginx/snippets/zenith-dashboard*.conf", '"/etc/nginx/snippets/zenith-dashboard.conf"'):
            text = "server { include " + path + "; }"
            self.assertEqual(text, setup.include_route(text))

    def test_multiple_servers_require_explicit_operator_selection(self):
        with self.assertRaisesRegex(ValueError, "one nginx server"):
            setup.include_route("server { listen 80; } server { listen 443; }")
        self.assertIn("include ", setup.include_route('server { auth_basic "server { # realm"; }'))

    def test_failed_validation_restores_site_and_does_not_reload(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "site"; original = "server { listen 80; }\n"; path.write_text(original)
            with patch.object(setup.subprocess, "run", side_effect=subprocess.CalledProcessError(1, "nginx")) as run:
                with self.assertRaisesRegex(ValueError, "original site restored"): setup.activate(path)
            self.assertEqual(original, path.read_text()); self.assertEqual(1, run.call_count)
            self.assertEqual(1, len(list(Path(folder).glob("site.before-dashboard-*"))))

    def test_success_validates_then_reloads_and_preserves_backup(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "site"; path.write_text("server { listen 80; }\n")
            with patch.object(setup.subprocess, "run") as run:
                setup.activate(path); setup.activate(path)
            self.assertEqual([["nginx", "-t"], ["systemctl", "reload", "nginx"]] * 2, [call.args[0] for call in run.call_args_list])
            self.assertEqual(1, len(list(Path(folder).glob("site.before-dashboard-*"))))


if __name__ == "__main__": unittest.main()
