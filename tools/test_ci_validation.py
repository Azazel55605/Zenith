"""CI fan-out must retain complete coverage and exact-source artifact binding."""
import importlib.util
import ast
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('ci_validation_test', ROOT / 'tools/ci-validation.py')
ci = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ci)


class PlanTests(unittest.TestCase):
    def test_raw_uart_offsets_cannot_match_previous_repeated_cpu_status(self):
        spec=importlib.util.spec_from_file_location('cpu_offset_headless',ROOT/'tools/headless-smoke.py')
        h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)
        with tempfile.TemporaryDirectory() as temporary:
            m=object.__new__(h.Machine);m.serial=Path(temporary)/'serial.log'
            old=('old trace\r\n'*200)+'user: CPU_TEST=0\r\n'
            m.serial.write_bytes(old.encode());offset=len(m.log())
            self.assertEqual(len(old),offset)
            m.process=SimpleNamespace(poll=lambda:None)
            self.assertFalse(m.wait('user: CPU_TEST=0',offset,timeout=.01))
            with m.serial.open('ab') as f:f.write(b'user: worker=3 apic=3 actual=3\r\nuser: CPU_TEST=0\r\n')
            self.assertTrue(m.wait('user: CPU_TEST=0',offset,timeout=.05))
            self.assertIn('worker=3 apic=3 actual=3',m.log()[offset:])

    def test_current_controller_repairs_immutable_tag_log_offsets(self):
        spec=importlib.util.spec_from_file_location('cpu_offset_policy',ROOT/'deploy/distributor/validation-output.py')
        policy=importlib.util.module_from_spec(spec);spec.loader.exec_module(policy)
        class OldMachine:
            def log(self):return self.serial.read_text()
        policy.install(OldMachine)
        with tempfile.TemporaryDirectory() as temporary:
            m=OldMachine();m.serial=Path(temporary)/'serial.log'
            data=b'a\r\nuser: CPU_TEST=0\r\n';m.serial.write_bytes(data)
            self.assertEqual(data.decode(),m.log())

    def test_iso_editor_is_preflighted_on_fresh_guest_runners(self):
        with patch.object(ci.release.shutil,'which',side_effect=lambda name: None if name=='xorriso' else '/tool'):
            with self.assertRaisesRegex(ValueError,'missing native validation tools: xorriso'):
                ci.release.native_prerequisites()
    def test_process_setup_uses_one_cpu_but_installed_c4_keeps_four(self):
        tree = ast.parse((ROOT / 'tools/native-process-smoke.py').read_text())
        boot = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == 'boot')
        machine = Mock(); machine.wait.return_value = True
        transport = SimpleNamespace(Machine=Mock(return_value=machine))
        scope = dict(h=transport, keep=Path('/fixture'), cpu_workers=True, process_cpus=4, net=[])
        exec(compile(ast.Module(body=[boot], type_ignores=[]), '<actual process boot>', 'exec'), scope)
        scope['boot']('live')
        self.assertEqual(1, transport.Machine.call_args.kwargs['cpus'])
        self.assertIsNone(transport.Machine.call_args.kwargs['argument'])
        scope['boot']('installed', Path('/fixture/disk.img'))
        self.assertEqual(4, transport.Machine.call_args.kwargs['cpus'])
        self.assertEqual('smp=native', transport.Machine.call_args.kwargs['argument'])

    def test_login_keyboard_check_waits_for_fresh_complete_structured_output(self):
        spec = importlib.util.spec_from_file_location('ci_login_check', ROOT / 'tools/login-session-smoke.py')
        login = importlib.util.module_from_spec(spec); spec.loader.exec_module(login)
        machine = Mock(); machine.log.return_value = 'old user: /\n'; machine.wait.return_value = False
        with patch.object(login, 'monitor_type') as typing, patch.object(login, 'monitor_command') as keys:
            self.assertFalse(login.german_slash(machine, Path('/monitor')))
        typing.assert_called_once_with(Path('/monitor'), 'logger ')
        self.assertEqual(['sendkey shift-7 30', 'sendkey ret 30'], [call.args[1] for call in keys.call_args_list])
        machine.wait.assert_called_once_with('user: /\n', len('old user: /\n'), 60)

    def test_native_matrix_is_exact_distributor_plan_with_expensive_gates_first(self):
        gates = [row['gate'] for row in ci.matrix('native')['include']]
        self.assertCountEqual(gates, [key for key, _ in ci.release.native_gate_plan()])
        self.assertEqual(len(gates), len(set(gates)))
        self.assertEqual(['u', 'k'], gates[:2])
        self.assertTrue({'a', 's', 'q', 'p', 'j', 'b'} <= set(gates))

    def test_all_legacy_suites_remain_and_only_disk_boot_is_ordered(self):
        plan = ci.legacy_plan(Path('/disposable'))
        self.assertEqual({'ext2-pressure', 'boot', 'security', 'foundation', 'guided-gui', 'guided-headless',
                          'rescue', 'headless', 'offline-packages', 'idt', 'dhcp-receive', 'transfer', 'keyboard', 'network'}, set(plan))
        self.assertEqual(2, len(plan['boot']))
        self.assertEqual('smoke-test.py', Path(plan['boot'][0][2]).name)
        self.assertEqual('test-installed-boot.py', Path(plan['boot'][1][2]).name)
        self.assertIn('/disposable/boot/installed-disk.img', plan['boot'][1])
        self.assertEqual(1, len(plan['ext2-pressure']))
        self.assertIn('--large-ext2-stress', plan['ext2-pressure'][0])
        self.assertEqual(set(plan), {row['gate'] for row in ci.matrix('legacy')['include']})

    def test_unknown_native_gate_is_refused_before_workspace_allocation(self):
        with patch.object(ci.release.tempfile, 'mkdtemp') as allocate:
            with self.assertRaisesRegex(ValueError, 'unknown native gate'):
                ci.release.native_checks(Path('/not-created'), gate='typo')
        allocate.assert_not_called()


class ArtifactTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(); self.addCleanup(temp.cleanup)
        self.root = Path(temp.name); (self.root / 'output-x64').mkdir()
        for name in ('Zenith.iso', 'Zenith.elf'): (self.root / 'output-x64' / name).write_bytes(name.encode())
        for change in (patch.object(ci, 'ROOT', self.root), patch.object(ci.release, 'clean'),
                       patch.object(ci.release, 'source_commit', return_value='a' * 40)):
            change.start(); self.addCleanup(change.stop)
        self.build = ci.seal_build()
        self.results = self.root / 'results'; self.results.mkdir()

    def receipts(self):
        for kind in ('native', 'legacy'):
            for row in ci.matrix(kind)['include']:
                folder = self.results / (kind + '-' + row['gate']); folder.mkdir()
                (folder / 'result.json').write_text(json.dumps(dict(self.build, kind=kind, gate=row['gate'], passed=True)))

    def test_transferred_build_is_bound_to_source_and_both_binary_hashes(self):
        self.assertEqual(self.build, ci.verify_build())
        with patch.object(ci.release, 'source_commit', return_value='b' * 40):
            with self.assertRaisesRegex(ValueError, 'another source'): ci.verify_build()
        for name in ('Zenith.iso', 'Zenith.elf'):
            path = self.root / 'output-x64' / name; original = path.read_bytes(); path.write_bytes(b'changed')
            with self.assertRaisesRegex(ValueError, 'hash mismatch'): ci.verify_build()
            path.write_bytes(original)

    def test_complete_results_pass_but_empty_or_missing_gate_cannot(self):
        with self.assertRaisesRegex(ValueError, 'missing CI gates'): ci.aggregate(self.results)
        self.receipts(); ci.aggregate(self.results)
        (self.results / 'native-u/result.json').unlink()
        with self.assertRaisesRegex(ValueError, 'missing CI gates'): ci.aggregate(self.results)

    def test_failed_stale_wrong_binary_and_duplicate_results_cannot_pass(self):
        self.receipts(); path = self.results / 'native-u/result.json'; original = path.read_text()
        for changes in (dict(passed=False), dict(source='b' * 40), dict(artifacts={}), dict(kind='other'), dict(gate='typo')):
            row = json.loads(original); row.update(changes); path.write_text(json.dumps(row))
            with self.assertRaises(ValueError): ci.aggregate(self.results)
        path.write_text(original)
        duplicate = self.results / 'duplicate'; duplicate.mkdir(); (duplicate / 'result.json').write_text(original)
        with self.assertRaisesRegex(ValueError, 'duplicate'): ci.aggregate(self.results)

    def test_failed_gate_never_publishes_a_success_receipt(self):
        keep = self.root / 'gate'
        with patch.dict(ci.os.environ), patch.object(ci.release, 'native_prerequisites'), patch.object(ci.release, 'phase'), \
             patch.object(ci.release, 'native_checks', side_effect=ValueError('actual guest failed')):
            with self.assertRaisesRegex(ValueError, 'actual guest failed'): ci.run_gate('native', 'u', keep)
        self.assertFalse((keep / 'result.json').exists())

    def test_native_runner_uses_actual_release_gate_and_fresh_policy(self):
        keep = self.root / 'gate'
        def check(path, gate):
            self.assertEqual('u', gate); self.assertEqual(keep, path)
            self.assertEqual('1', ci.os.environ['ZENITH_VALIDATION_FRESH'])
        with patch.dict(ci.os.environ), patch.object(ci.release, 'native_prerequisites'), patch.object(ci.release, 'phase'), \
             patch.object(ci.release, 'native_checks', side_effect=check) as run:
            ci.run_gate('native', 'u', keep)
        run.assert_called_once_with(keep, gate='u')
        self.assertTrue(json.loads((keep / 'result.json').read_text())['passed'])

    def test_unknown_gate_never_creates_evidence_or_launches_work(self):
        with patch.object(ci.release, 'phase') as phase:
            with self.assertRaisesRegex(ValueError, 'unknown'): ci.run_gate('legacy', 'typo', self.root / 'bad')
        phase.assert_not_called(); self.assertFalse((self.root / 'bad').exists())


if __name__ == '__main__': unittest.main()
