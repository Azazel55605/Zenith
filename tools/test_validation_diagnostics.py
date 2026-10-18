#!/usr/bin/env python3
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import socket
import struct
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('diagnostics', ROOT/'deploy/distributor/validation-diagnostics.py')
diagnostics = importlib.util.module_from_spec(spec); spec.loader.exec_module(diagnostics)


class Connection:
    def __init__(self, fragments): self.fragments = iter(fragments); self.commands = []
    def __enter__(self): return self
    def __exit__(self, *args): pass
    def settimeout(self, value): self.timeout = value
    def connect(self, path): self.path = path
    def recv(self, maximum): return next(self.fragments, b'')
    def sendall(self, data): self.commands.append(data)


class DiagnosticTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.machine = SimpleNamespace(directory=self.directory)

    def capture(self, label='N_STREAM_BYTES_0', elf=None):
        with contextlib.redirect_stdout(io.StringIO()) as output:
            result = diagnostics.capture(self.machine, label, elf)
        self.output = output.getvalue()
        return json.loads(result.read_text()) if result else None

    def test_fragmented_hmp_response_is_read_only_and_excludes_register_payloads(self):
        connection = Connection([b'QEMU monitor\n(qe', b'mu) ', b'RAX=534543524554\nPRIVATE_TEXT\nRIP=ffffffff80001000 ',
                                 b'RSP=ffff800010001000 RFL=00000246\n(qemu) '])
        with patch.object(diagnostics.socket, 'socket', return_value=connection):
            result = diagnostics.registers(self.directory/'monitor.sock')
        self.assertEqual({'RIP':'ffffffff80001000', 'RSP':'ffff800010001000', 'RFL':'00000246'}, result)
        self.assertEqual([b'info registers\n'], connection.commands)
        self.assertGreater(connection.timeout,0); self.assertLessEqual(connection.timeout,.5)
        self.assertNotIn('PRIVATE', json.dumps(result))
        self.assertNotIn('534543524554', json.dumps(result))

    def test_missing_registers_are_not_reported_as_a_snapshot(self):
        connection = Connection([b'(qemu) ', b'RAX=1234\n(qemu) '])
        with patch.object(diagnostics.socket, 'socket', return_value=connection):
            with self.assertRaises(ValueError): diagnostics.registers(self.directory/'monitor.sock')

    def test_three_bounded_samples_are_symbolized_against_the_selected_elf(self):
        elf = self.directory/'kernel.elf'; elf.write_bytes(b'exact failed kernel')
        sample = dict(RIP='ffffffff80001008', RSP='ffff800010001000', RFL='00000246')
        rows = [(0xffffffff80001000, 'Kernel_Mutex_Acquire'), (0xffffffff80001100, 'Kernel_Next')]
        with patch.object(diagnostics, 'registers', return_value=sample.copy()) as read, \
             patch.object(diagnostics, 'symbols', return_value=rows), patch.object(diagnostics.time, 'sleep'):
            report = self.capture(elf=elf)
        self.assertEqual(3, read.call_count)
        self.assertTrue(report['observation_only'])
        self.assertEqual(3, len(report['samples']))
        self.assertEqual('Kernel_Mutex_Acquire', report['samples'][0]['symbol'])
        self.assertEqual(8, report['samples'][0]['symbol_offset'])
        self.assertEqual(diagnostics.hashlib.sha256(elf.read_bytes()).hexdigest(), report['elf_sha256'])
        self.assertNotIn('exact failed kernel', json.dumps(report))

    def test_missing_monitor_and_symbols_remain_observations(self):
        with patch.object(diagnostics, 'registers', side_effect=socket.timeout):
            report = self.capture(elf=self.directory/'missing.elf')
        self.assertEqual([], report['samples']); self.assertIn('monitor_error', report)
        self.assertIn('symbol_error', report)

    def test_unrelated_and_secret_markers_never_open_the_monitor(self):
        with patch.object(diagnostics, 'registers') as read:
            for label in ('password-prompt', 'authentication', 'N_STREAM_LIVE', 'private marker'):
                self.assertIsNone(self.capture(label))
        read.assert_not_called()
        self.assertFalse((self.directory/'pipeline-stall.json').exists())

    def test_scheduler_snapshot_accepts_packed_four_byte_alignment_and_stable_sequence(self):
        pointer=0xffff800001234004
        header=struct.pack('<IIIIQ',0xc05d0001,1,2,0,8)
        entries=struct.pack('<IIIIIIII',78,0,2,0,80,0,1,0)
        with patch.object(diagnostics,'words',side_effect=[struct.pack('<Q',pointer),header,header+entries,struct.pack('<Q',8)]) as read:
            report=diagnostics.scheduler_snapshot(self.directory/'monitor.sock',0xffffffff80500000)
        self.assertEqual([dict(id=78,cpu=0,state=2,flags=0),dict(id=80,cpu=0,state=1,flags=0)],report['threads'])
        self.assertEqual(pointer,read.call_args_list[1].args[1])

    def test_scheduler_snapshot_rejects_torn_reads_and_unknown_formats(self):
        pointer=struct.pack('<Q',0xffff800001234004)
        header=struct.pack('<IIIIQ',0xc05d0001,1,0,0,8)
        with patch.object(diagnostics,'words',side_effect=[pointer,header,header,struct.pack('<Q',10),header,header,struct.pack('<Q',10)]):
            with self.assertRaisesRegex(ValueError,'changed during read'):
                diagnostics.scheduler_snapshot(self.directory/'monitor.sock',0xffffffff80500000)
        for magic,version,count in [(0,1,0),(0xc05d0001,2,0),(0xc05d0001,1,65)]:
            with patch.object(diagnostics,'words',side_effect=[pointer,struct.pack('<IIIIQ',magic,version,count,0,8)]):
                with self.assertRaisesRegex(ValueError,'unsupported'):
                    diagnostics.scheduler_snapshot(self.directory/'monitor.sock',0xffffffff80500000)

    def test_scheduler_read_is_bounded_and_only_returns_requested_numeric_words(self):
        address=0xffff800001234004
        response='PRIVATE_GUEST_TEXT\nffff800001234004: 0x00000001c05d0001 0x0000000000000000\n'
        with patch.object(diagnostics,'query',return_value=response) as query:
            data=diagnostics.words('monitor',address,2)
        self.assertEqual(struct.pack('<QQ',0x00000001c05d0001,0),data)
        query.assert_called_once_with('monitor','x /2gx 0xffff800001234004')
        for pointer,count in [(0,1),(address,132),(address+1,1),(address,0)]:
            with self.assertRaises(ValueError): diagnostics.words('monitor',pointer,count)

    def test_halt_caller_retains_only_a_verified_direct_call_return_address(self):
        sample=dict(symbol='_native_cpu_halt',symbol_offset=1,RSP='ffff800010001000')
        rows=[(0xffffffff80001000,'_native_cpu_halt'),(0xffffffff80002000,'Kernel_Wait'),(0xffffffff80003000,'Kernel_Next')]
        output=SimpleNamespace(stdout='ffffffff80002003: e8 f8 ef ff ff  call ffffffff80001000 <_native_cpu_halt>\nPRIVATE_TEXT\n')
        with patch.object(diagnostics,'words',return_value=struct.pack('<Q',0xffffffff80002008)) as read, \
             patch.object(diagnostics.subprocess,'run',return_value=output) as run:
            caller=diagnostics.halt_caller('monitor',sample,Path('/kernel.elf'),rows)
        self.assertEqual(dict(return_rip='ffffffff80002008',symbol='Kernel_Wait',symbol_offset=8),caller)
        read.assert_called_once_with('monitor',0xffff800010001000,1)
        self.assertEqual(3,run.call_args.kwargs['timeout'])
        self.assertNotIn('PRIVATE',json.dumps(caller))

    def test_halt_caller_does_not_read_other_frames_or_keep_unverified_values(self):
        rows=[(0xffffffff80001000,'_native_cpu_halt'),(0xffffffff80002000,'Kernel_Wait'),(0xffffffff80003000,'Kernel_Next')]
        with patch.object(diagnostics,'words') as read:
            self.assertIsNone(diagnostics.halt_caller('monitor',dict(symbol='other',symbol_offset=1),Path('/kernel.elf'),rows))
            read.assert_not_called()
        sample=dict(symbol='_native_cpu_halt',symbol_offset=1,RSP='ffff800010001000')
        with patch.object(diagnostics,'words',return_value=struct.pack('<Q',0x534543524554)):
            with self.assertRaisesRegex(ValueError,'outside code'):
                diagnostics.halt_caller('monitor',sample,Path('/kernel.elf'),rows)
        with patch.object(diagnostics,'words',return_value=struct.pack('<Q',0xffffffff80002008)), \
             patch.object(diagnostics.subprocess,'run',return_value=SimpleNamespace(stdout='ffffffff80002003: e9 00 00 00 00 jmp ffffffff80001000 <_native_cpu_halt>')):
            with self.assertRaisesRegex(ValueError,'unverified'):
                diagnostics.halt_caller('monitor',sample,Path('/kernel.elf'),rows)

    def test_symbol_lookup_is_bounded_and_has_no_shell(self):
        result = SimpleNamespace(stdout='ffffffff80001000 t code\nffffffff80002000 d PRIVATE_DATA\n')
        with patch.object(diagnostics.subprocess, 'run', return_value=result) as run:
            self.assertEqual([(0xffffffff80001000,'code')], diagnostics.symbols(Path('/kernel.elf')))
        args, kwargs = run.call_args
        self.assertEqual(['nm','-n','--defined-only','/kernel.elf'], args[0])
        self.assertEqual(3, kwargs['timeout']); self.assertNotIn('shell', kwargs)


if __name__ == '__main__': unittest.main()
