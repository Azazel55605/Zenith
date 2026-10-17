#!/usr/bin/env python3
"""Execute the pinned collector on a synthetic heap; no real kernel memory is used."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class ConservativeRootTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.build = tempfile.TemporaryDirectory(prefix='zenith-gc-roots-')
        folder = Path(cls.build.name)
        fixture = ROOT/'tools/fixtures/conservative-roots.cs'
        (folder/'check.csproj').write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><AllowUnsafeBlocks>true</AllowUnsafeBlocks>'
            '</PropertyGroup><ItemGroup><Compile Include="' + str(fixture) + '" />'
            '<Reference Include="Mono.Cecil"><HintPath>' + str(ROOT/'tools/PatcherFixup/bin/Release/net10.0/Mono.Cecil.dll') + '</HintPath></Reference></ItemGroup></Project>')
        result = subprocess.run(['dotnet', 'build', str(folder/'check.csproj'), '-c', 'Release', '--nologo'],
                                capture_output=True, text=True)
        if result.returncode:
            cls.build.cleanup()
            raise RuntimeError(result.stdout + result.stderr)
        cls.driver = folder/'bin/Release/net10.0/check.dll'

    @classmethod
    def tearDownClass(cls): cls.build.cleanup()

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        packages = Path(os.environ.get('NUGET_PACKAGES', Path.home()/'.nuget/packages'))
        self.source = packages/'cosmos.kernel.core/3.0.89/runtimes/linux-x64/lib/net10.0/Cosmos.Kernel.Core.dll'
        self.copy = Path(self.temp.name)/self.source.name; shutil.copyfile(self.source, self.copy)
        self.tool = ROOT/'tools/PatcherFixup/bin/Release/net10.0/PatcherFixup.dll'

    def adapt(self):
        return subprocess.run(['dotnet', str(self.tool), str(self.copy)], capture_output=True, text=True)

    def tick(self, path, mode):
        result = subprocess.run(['dotnet', str(self.driver), str(path), str(ROOT/'output-x64'), mode],
                                capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        return result.stdout.strip()

    def test_real_interior_handle_corruption_is_prevented(self):
        original = self.source.read_bytes()
        for mode in ('interior', 'stack-range'):
            self.assertEqual('ownerMarked=False;handleChanged=True', self.tick(self.copy, mode))
        result = self.adapt(); self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('Adapted conservative GC roots', result.stdout)
        for mode in ('interior', 'stack-range'):
            self.assertEqual('ownerMarked=True;handleChanged=False', self.tick(self.copy, mode))
        self.assertEqual(original, self.source.read_bytes())

    def test_canonical_and_array_roots_are_retained(self):
        self.assertEqual(0, self.adapt().returncode)
        for mode in ('canonical', 'array', 'pinned', 'unaligned'):
            self.assertEqual('ownerMarked=True;handleChanged=False', self.tick(self.copy, mode))

    def test_free_unused_and_outside_addresses_are_not_marked(self):
        self.assertEqual(0, self.adapt().returncode)
        for mode in ('free', 'padding', 'outside'):
            self.assertEqual('ownerMarked=False;handleChanged=False', self.tick(self.copy, mode))

    def test_repeated_adaptation_is_byte_identical(self):
        self.assertEqual(0, self.adapt().returncode)
        before = self.copy.read_bytes(); self.assertEqual(0, self.adapt().returncode)
        self.assertEqual(before, self.copy.read_bytes())

    def test_scanner_drift_refused_without_writing(self):
        before = self.copy.read_bytes()
        changed = before.replace(b'ScanMemoryRange', b'ScanMemoryXange'); self.copy.write_bytes(changed)
        self.assertNotEqual(0, self.adapt().returncode)
        self.assertEqual(changed, self.copy.read_bytes())


if __name__ == '__main__': unittest.main()
