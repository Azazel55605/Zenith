#!/usr/bin/env python3
"""C5.1: the scheduler lock fixup on a copy of the pinned Cosmos.Kernel.Core.dll."""
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]


class SchedulerLockFixupTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix='zenith-sched-fixup-')
        folder = Path(cls.temp.name)
        subprocess.run(['dotnet', 'build', str(ROOT / 'tools/PatcherFixup'), '-c', 'Release', '--nologo'], check=True, capture_output=True)
        (folder / 'check.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>'
            '<ItemGroup><Compile Include="' + str(ROOT / 'tools/fixtures/sched-lock-check.cs') + '" />'
            '<Reference Include="Mono.Cecil"><HintPath>' + str(ROOT / 'tools/PatcherFixup/bin/Release/net10.0/Mono.Cecil.dll') + '</HintPath></Reference>'
            '</ItemGroup></Project>')
        subprocess.run(['dotnet', 'build', str(folder / 'check.csproj'), '-c', 'Release', '--nologo'], check=True, capture_output=True)
        cls.driver = folder / 'bin/Release/net10.0/check.dll'
        packages = Path(os.environ.get('NUGET_PACKAGES', Path.home() / '.nuget/packages'))
        cls.source = next((packages / 'cosmos.kernel.core').glob('3.0.89*/runtimes/linux-x64/lib/net10.0/Cosmos.Kernel.Core.dll'))

    @classmethod
    def tearDownClass(cls): cls.temp.cleanup()

    def adapt(self, path):
        return subprocess.run(['dotnet', str(ROOT / 'tools/PatcherFixup/bin/Release/net10.0/PatcherFixup.dll'), str(path)], capture_output=True, text=True)

    def test_arm64_keeps_its_platform_scheduler(self):
        source = self.source.parents[3] / 'linux-arm64/lib/net10.0' / self.source.name
        with tempfile.TemporaryDirectory() as folder:
            copy = Path(folder) / source.name
            shutil.copyfile(source, copy)
            result = self.adapt(copy)
            self.assertEqual(0, result.returncode, result.stderr)
            metadata = copy.read_bytes()
            for name in (b'zenith_sched_lock', b'zenith_sched_unlock', b'zenith_cpu_kick'):
                with self.subTest(entry=name):
                    self.assertFalse(name + b'\0' in metadata, 'x64 scheduler import found in arm64 metadata')

    def test_every_masked_section_and_interrupt_path_takes_the_lock(self):
        work = tempfile.TemporaryDirectory(); self.addCleanup(work.cleanup)
        copy = Path(work.name) / self.source.name; shutil.copyfile(self.source, copy)
        original = self.source.read_bytes()
        first = self.adapt(copy); self.assertEqual(0, first.returncode, first.stderr + first.stdout)
        self.assertIn('Adapted scheduler locking: ', first.stdout)
        result = subprocess.run(['dotnet', str(self.driver), str(copy)], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn('whole=4 failures=0', result.stdout)
        sections = int(result.stdout.split('sections=')[1].split()[0])
        self.assertGreaterEqual(sections, 8, result.stdout)
        second = self.adapt(copy); self.assertEqual(0, second.returncode, second.stderr)
        self.assertNotIn('Adapted scheduler locking', second.stdout, 'a second run changes nothing')
        self.assertEqual(original, self.source.read_bytes(), 'the installed package is never modified')


if __name__ == '__main__': unittest.main()
