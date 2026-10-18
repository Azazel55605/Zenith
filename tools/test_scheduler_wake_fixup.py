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

    def test_wake_current_then_yield_keeps_one_sorted_queue_entry(self):
        self.assertIn('queue=2;tickets=200;state=1', self.tick(self.copy, 'wake-current'))
        result = self.adapt(); self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('queue=1;tickets=200;state=1', self.tick(self.copy, 'wake-current'))

    def test_reinsertion_sorts_the_changed_pass_before_existing_workers(self):
        self.assertIn('sorted=False', self.tick(self.copy, 'wake-current-order'))
        result = self.adapt(); self.assertEqual(0, result.returncode, result.stderr)
        output = self.tick(self.copy, 'wake-current-order')
        self.assertIn('queue=2;', output); self.assertIn('sorted=True', output)

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

    def test_self_dispatch_after_current_sleep_expires_restores_running_without_switch(self):
        for mode in ('dispatch-interrupt', 'dispatch-voluntary'):
            self.assertEqual('state=1;queue=0;tickets=200;stack=4096;switchSp=0;new=0', self.tick(self.copy, mode))
        self.assertEqual(0, self.adapt().returncode)
        for mode in ('dispatch-interrupt', 'dispatch-voluntary'):
            self.assertEqual('state=2;queue=0;tickets=200;stack=4096;switchSp=0;new=0', self.tick(self.copy, mode))

    def test_repeated_short_sleep_wake_self_dispatch_preserves_tickets_and_stack(self):
        self.assertEqual('not-running-at=0', self.tick(self.copy, 'dispatch-repeat'))
        self.assertEqual(0, self.adapt().returncode)
        self.assertEqual('state=2;queue=0;tickets=200;stack=4096;switchSp=0;new=0',self.tick(self.copy,'dispatch-repeat'))

    def test_other_thread_switch_and_created_entry_keep_the_original_context_contract(self):
        self.assertEqual(0, self.adapt().returncode)
        for mode,new in [('dispatch-other',0),('dispatch-new',1)]:
            self.assertEqual('state=1;queue=1;tickets=300;stack=8192;switchSp=12288;new='+str(new),self.tick(self.copy,mode))

    def test_self_dispatch_does_not_resurrect_nonready_states(self):
        self.assertEqual(0, self.adapt().returncode)
        for state in (0,2,3,4,5):
            self.assertEqual('state='+str(state)+';queue=0;tickets=200;stack=4096;switchSp=0;new=0',self.tick(self.copy,'dispatch-preserve-'+str(state)))

    def test_dispatch_signature_drift_is_refused_without_writing(self):
        before=self.copy.read_bytes()
        changed=before.replace(b'ScheduleFromInterrupt',b'ScheduleFromInterrupX')
        self.assertNotEqual(before,changed);self.copy.write_bytes(changed)
        self.assertNotEqual(0,self.adapt().returncode)
        self.assertEqual(changed,self.copy.read_bytes())

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
