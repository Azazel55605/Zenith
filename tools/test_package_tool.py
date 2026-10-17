#!/usr/bin/env python3
"""Exercise the shared P0 host builder/planner on disposable payloads."""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "tools/Zenith.PackageTool/Zenith.PackageTool.csproj"
DLL = PROJECT.parent / "bin/Debug/net10.0/Zenith.PackageTool.dll"


class PackageToolTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        subprocess.run(["dotnet", "build", str(PROJECT), "--nologo"], check=True,
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="zenith-pkg-test-")
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.source = self.directory / "source"
        self.payload = self.source / "usr/share/doc/example.txt"
        self.payload.parent.mkdir(parents=True)
        self.payload.write_bytes(b"hello\n")
        self.bundle = self.directory / "bundle"
        self.root = self.directory / "root"
        self.root.mkdir()

    def run_tool(self, *args):
        return subprocess.run(["dotnet", str(DLL), *map(str, args)], capture_output=True,
                              text=True, timeout=10)

    def build(self):
        result = self.run_tool("build", "hello-docs", "1.0.0", self.source, self.bundle)
        self.assertEqual(0, result.returncode, result.stderr)

    def test_native_builder_requires_valid_abi_and_records_execution_capability(self):
        subprocess.run(["python3", str(ROOT / "tools/build-userland.py"), str(self.source)],
                       check=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        result = self.run_tool("build-native", "sunbeam-tools", "0.1.0", self.source, self.bundle, "--repo", "extra")
        self.assertEqual(0, result.returncode, result.stderr)
        text = (self.bundle / "manifest.tsv").read_text()
        self.assertIn("architecture\tx86_64", text)
        self.assertIn("requires\tzenith-native-abi\t1.0", text)
        self.assertIn("requires\tzenith-native-fs-abi\t1.0", text)
        self.assertIn("requires\tzenith-native-io-abi\t1.0", text)
        self.assertIn("requires\tzenith-native-stream-abi\t1.0", text)
        self.assertIn("sunbeam.elf\t0755\t", text)
        elf = self.source / "usr/share/zenith/native/sunbeam.elf"
        self.assertEqual(0, self.run_tool("native-check", elf).returncode)
        elf.write_bytes(b"not-a-program")
        result = self.run_tool("build-native", "sunbeam-tools", "0.1.0", self.source, self.directory / "bad")
        self.assertEqual(1, result.returncode)
        self.assertFalse((self.directory / "bad").exists())
        self.assertEqual([], list(self.directory.glob("bad.staging-*")))

    def test_native_builder_refuses_data_only_payload(self):
        result = self.run_tool("build-native", "fake-tools", "0.1.0", self.source, self.bundle)
        self.assertEqual(1, result.returncode)
        self.assertIn("requires a valid Zenith ABI executable", result.stderr)
        self.assertFalse(self.bundle.exists())

    def test_roundtrip_deterministic_and_dry_run(self):
        self.build()
        manifest = self.bundle / "manifest.tsv"
        self.assertEqual(0, self.run_tool("inspect", manifest).returncode)
        result = self.run_tool("plan", manifest, self.root)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("create /usr/share/doc/example.txt", result.stdout)
        self.assertEqual([], list(self.root.iterdir()))
        second = self.directory / "second"
        self.assertEqual(0, self.run_tool("build", "hello-docs", "1.0.0", self.source, second).returncode)
        self.assertEqual(manifest.read_bytes(), (second / "manifest.tsv").read_bytes())

    def test_existing_file_and_ancestor_symlink_conflicts(self):
        self.build()
        target = self.root / "usr/share/doc/example.txt"
        target.parent.mkdir(parents=True)
        target.write_text("local data")
        result = self.run_tool("plan", self.bundle / "manifest.tsv", self.root)
        self.assertEqual(1, result.returncode)
        self.assertIn("unowned path conflict", result.stdout)
        self.assertEqual("local data", target.read_text())
        target.unlink()
        target.parent.rmdir()
        target.parent.symlink_to(self.payload.parent, target_is_directory=True)
        self.assertEqual(1, self.run_tool("plan", self.bundle / "manifest.tsv", self.root).returncode)

    def test_tampered_payload_is_refused(self):
        self.build()
        (self.bundle / "payload/usr/share/doc/example.txt").write_bytes(b"HELLO\n")
        result = self.run_tool("plan", self.bundle / "manifest.tsv", self.root)
        self.assertEqual(1, result.returncode)
        self.assertIn("integrity mismatch", result.stderr)
        self.assertEqual([], list(self.root.iterdir()))

    def test_builder_refuses_symlinks(self):
        self.payload.unlink()
        self.payload.symlink_to("/etc/hostname")
        self.assertEqual(1, self.run_tool("build", "hello-docs", "1.0.0", self.source, self.bundle).returncode)
        self.assertFalse(self.bundle.exists())

    def test_builder_refuses_fifo_without_opening_it(self):
        self.payload.unlink()
        os.mkfifo(self.payload)
        result = self.run_tool("build", "hello-docs", "1.0.0", self.source, self.bundle)
        self.assertEqual(1, result.returncode)
        self.assertIn("special nodes", result.stderr)

    def test_output_inside_source_or_existing_output_refused(self):
        self.assertEqual(1, self.run_tool("build", "hello-docs", "1.0.0", self.source, self.source / "bundle").returncode)
        self.bundle.mkdir()
        marker = self.bundle / "marker"
        marker.write_text("keep")
        self.assertEqual(1, self.run_tool("build", "hello-docs", "1.0.0", self.source, self.bundle).returncode)
        self.assertEqual("keep", marker.read_text())

    def test_unsupported_path_cleans_staging(self):
        (self.source / "etc").mkdir()
        (self.source / "etc/shadow").write_text("not a data payload")
        self.assertEqual(1, self.run_tool("build", "hello-docs", "1.0.0", self.source, self.bundle).returncode)
        self.assertFalse(self.bundle.exists())
        self.assertEqual([], list(self.directory.glob("bundle.staging-*")))

    def test_payload_symlink_refused_by_planner(self):
        self.build()
        payload = self.bundle / "payload/usr/share/doc/example.txt"
        payload.unlink()
        payload.symlink_to(self.payload)
        result = self.run_tool("plan", self.bundle / "manifest.tsv", self.root)
        self.assertEqual(1, result.returncode)
        self.assertIn("link", result.stderr)

    def test_empty_directory_tree_bound_and_output_alias(self):
        for i in range(8):
            for j in range(70):
                (self.source / ("d" + str(i)) / str(j)).mkdir(parents=True)
        result = self.run_tool("build", "hello-docs", "1.0.0", self.source, self.bundle)
        self.assertEqual(1, result.returncode)
        self.assertIn("entry limit", result.stderr)
        alias = self.directory / "alias"
        alias.symlink_to(self.source, target_is_directory=True)
        result = self.run_tool("build", "hello-docs", "1.0.0", self.source, alias / "bundle")
        self.assertEqual(1, result.returncode)
        self.assertFalse((self.source / "bundle").exists())


if __name__ == "__main__":
    unittest.main()
