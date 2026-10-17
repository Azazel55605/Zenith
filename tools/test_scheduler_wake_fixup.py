#!/usr/bin/env python3
"""Execute real pinned wake/queue code with IRQ and UART hardware stubbed on the host."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class WakeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.build = tempfile.TemporaryDirectory(prefix='zenith-stride-policy-')
        folder = Path(cls.build.name)
        fixture = ROOT/'tools/fixtures/scheduler-wake.cs'
        (folder/'check.csproj').write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
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

    def test_real_timeout_then_late_signal_does_not_enqueue_twice(self):
        original = self.source.read_bytes()
        baseline = self.tick(self.copy, 'late-signal')
        self.assertIn('queue=2;tickets=300;state=1;irq=3/3;irqFlags=512', baseline)
        result = self.adapt(); self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('Adapted scheduler wake', result.stdout)
        self.assertEqual('queue=1;tickets=200;state=1;irq=3/3;irqFlags=512;passPreserved=True', self.tick(self.copy, 'late-signal'))
        self.assertEqual(original, self.source.read_bytes())

    def test_created_thread_is_enqueued_once_without_losing_new_thread_state(self):
        self.assertIn('queue=2;tickets=300;state=0;irq=2/2;irqFlags=512', self.tick(self.copy, 'created'))
        result = self.adapt(); self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual('queue=1;tickets=200;state=0;irq=2/2;irqFlags=512;passPreserved=True', self.tick(self.copy, 'created'))

    def test_ready_running_and_dead_late_signals_preserve_state_and_irq_scope(self):
        result = self.adapt(); self.assertEqual(0, result.returncode, result.stderr)
        for mode, expected in [('ready', 'queue=1;tickets=200;state=1'),
                               ('running', 'queue=0;tickets=200;state=2'),
                               ('dead', 'queue=0;tickets=100;state=5')]:
            self.assertEqual(expected + ';irq=2/2;irqFlags=512;passPreserved=True', self.tick(self.copy, mode))

    def test_blocked_waiter_still_wakes_and_late_signal_is_idempotent(self):
        result = self.adapt(); self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual('queue=1;tickets=200;state=1;irq=2/2;irqFlags=512;passPreserved=True', self.tick(self.copy, 'blocked'))

    def test_repeated_adaptation_is_byte_identical(self):
        self.assertEqual(0, self.adapt().returncode)
        before = self.copy.read_bytes(); self.assertEqual(0, self.adapt().returncode)
        self.assertEqual(before, self.copy.read_bytes())

    def test_drift_refused_without_writing(self):
        before = self.copy.read_bytes()
        self.assertIn(b'get_State', before)
        changed = before.replace(b'get_State', b'get_Xtate'); self.copy.write_bytes(changed)
        self.assertNotEqual(0, self.adapt().returncode)
        self.assertEqual(changed, self.copy.read_bytes())


if __name__ == '__main__': unittest.main()
