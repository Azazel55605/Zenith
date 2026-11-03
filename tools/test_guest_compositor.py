#!/usr/bin/env python3
"""A delivered benchmark prefix is not a complete result or a failed worker."""
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import unittest

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('guest_compositor',ROOT/'tools/guest_compositor.py')
records=importlib.util.module_from_spec(spec);spec.loader.exec_module(records)

class RecordTests(unittest.TestCase):
    def test_retained_kvm_prefix_does_not_report_missing_resumed_tiles(self):
        partial='\n[zenith] [   38.409] compose: parallel bench verify=0 scalar-us=11944 pa'
        complete=partial+'rallel-us=11200 ap-tiles=40 materials=7\n'
        self.assertIsNone(records.BENCHMARK.search(partial))
        self.assertEqual(records.BENCHMARK.search(complete).groups(),('0','11944','11200','40','7'))
    def test_every_split_waits_for_record_newline_without_replaying(self):
        complete='[zenith] [   38.409] compose: parallel bench verify=0 scalar-us=11944 parallel-us=11200 ap-tiles=40 materials=7\n'
        for length in range(len(complete)):
            self.assertIsNone(records.BENCHMARK.search(complete[:length]))
        chunks=iter([complete[:80],complete[:-1],complete])
        machine=SimpleNamespace(log=lambda:next(chunks),process=SimpleNamespace(poll=lambda:None),serial='fixture.log')
        self.assertEqual(records.wait_record(machine,records.BENCHMARK,0,1)[4],'40')
    def test_filter_hash_requires_complete_user_log_record(self):
        record='[zenith] [   12.000] user: material blur verified; cpus=4 scalar-us=302000 parallel-us=21000 ap-tiles=120 hash=8a2f0f7845d6a309'
        self.assertIsNone(records.FILTER.search(record))
        self.assertEqual(records.FILTER.search(record+'\n')[5],'8a2f0f7845d6a309')
        self.assertIsNone(records.FILTER.search(record.replace('user:','console:')+'\n'))
    def test_wrong_tag_or_command_echo_is_not_a_compositor_result(self):
        text='parallel bench verify=0 scalar-us=1 parallel-us=1 ap-tiles=40 materials=7\n'
        self.assertIsNone(records.BENCHMARK.search(text))
        self.assertIsNone(records.BENCHMARK.search('[zenith] [    1.000] user: '+text))
if __name__=='__main__':unittest.main()
