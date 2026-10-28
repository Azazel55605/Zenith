#!/usr/bin/env python3
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('native_validation',ROOT/'deploy/distributor/native_validation.py')
native=importlib.util.module_from_spec(spec);spec.loader.exec_module(native)


class ResourceTests(unittest.TestCase):
    def test_auto_reserves_host_and_scales_to_four(self):
        self.assertEqual(4,native.workers('auto',12,16*1024**3))
        self.assertEqual(8,native.workers('8',12,32*1024**3))
        self.assertEqual(1,native.workers('auto',2,16*1024**3))
        self.assertEqual(2,native.workers('8',16,4*1024**3))
        self.assertEqual(1,native.workers('auto',1,512*1024**2))
    def test_invalid_worker_override_fails(self):
        for value in ('0','9','-1','many'):
            with self.assertRaises(ValueError):native.workers(value,8,16*1024**3)


class SchedulingTests(unittest.TestCase):
    def test_independent_gates_overlap_with_bounded_workers(self):
        barrier=threading.Barrier(3);lock=threading.Lock();active=0;maximum=0;seen=[]
        def run(check):
            nonlocal active,maximum
            with lock:active+=1;maximum=max(maximum,active);seen.append(check[0])
            if check[0] in ('u','k','r'):barrier.wait(timeout=3)
            time.sleep(.01)
            with lock:active-=1
        checks=[(x,x+'.py') for x in ('r','k','u','n','g')]
        native.execute(checks,run,3,threading.Event())
        self.assertEqual(3,maximum);self.assertCountEqual([x[0] for x in checks],seen)
    def test_settings_and_login_finish_before_expensive_gates(self):
        seen=[];done=set();lock=threading.Lock()
        def run(check):
            with lock:
                if check[0] not in ('a','s'):self.assertTrue({'a','s'}<=done)
                seen.append(check[0]);done.add(check[0])
        native.execute([(x,x+'.py') for x in ('k','s','r','a')],run,4,threading.Event())
        self.assertCountEqual(['k','s','r','a'],seen)
    def test_failure_stops_new_launches_but_retains_other_running_pass(self):
        both=threading.Barrier(2);finished=[]
        def run(check):
            if check[0] not in ('a','s'):self.fail('launched new work after failure')
            both.wait(timeout=3)
            if check[0]=='a':raise ValueError('real application failure')
            time.sleep(.05);finished.append('s')
        with self.assertRaisesRegex(ValueError,'real application failure'):
            native.execute([(x,x+'.py') for x in ('a','s','k','u')],run,2,threading.Event())
        self.assertEqual(['s'],finished)
    def test_serial_override_keeps_original_order(self):
        seen=[];native.execute([('r','r.py'),('k','k.py')],seen.append,1,threading.Event())
        self.assertEqual([('r','r.py'),('k','k.py')],seen)


class ReceiptTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name)
        self.base=dict(source='a'*40,artifacts={'Zenith.elf':'b'*64,'Zenith.iso':'c'*64},tools={'qemu':'one'})
        self.cache=native.Receipts(self.root/'receipts',self.base,'input-one')
        self.log=self.root/'result.log';self.log.write_text('actual successful test output\n')
        self.check=('a','native-settings-smoke.py')
    def test_only_completed_intact_exact_input_pass_is_reused(self):
        self.assertIsNone(self.cache.get(self.check));self.cache.put(self.check,self.log)
        self.assertEqual(self.log.read_bytes(),self.cache.get(self.check).read_bytes())
        for changed in (dict(source='z'*40),dict(artifacts={'Zenith.elf':'changed'}),dict(tools={'qemu':'two'})):
            other=native.Receipts(self.root/'receipts',dict(self.base,**changed),'input-one')
            self.assertIsNone(other.get(self.check))
        self.assertIsNone(self.cache.get(('k','kernel-update-smoke.py')))
    def test_input_policy_change_invalidates_gui_but_not_unrelated_gates(self):
        unrelated=('k','kernel-update-smoke.py')
        self.cache.put(self.check,self.log);self.cache.put(unrelated,self.log)
        other=native.Receipts(self.root/'receipts',self.base,'input-two')
        self.assertIsNone(other.get(self.check));self.assertIsNotNone(other.get(unrelated))
    def test_changed_log_missing_log_and_false_receipt_fail_closed(self):
        self.cache.put(self.check,self.log);cached=self.cache.get(self.check);cached.write_text('tampered')
        self.assertIsNone(self.cache.get(self.check))
        self.cache.put(self.check,self.log);cached.unlink();self.assertIsNone(self.cache.get(self.check))
        self.cache.put(self.check,self.log);record=self.root/'receipts'/(self.cache.key(self.check)+'.json')
        row=json.loads(record.read_text());row['passed']=False;record.write_text(json.dumps(row))
        self.assertIsNone(self.cache.get(self.check))
    def test_expired_future_malformed_and_linked_receipts_not_accepted(self):
        self.cache.put(self.check,self.log);record=self.root/'receipts'/(self.cache.key(self.check)+'.json')
        original=record.read_text()
        for completed in (time.time()-8*native.DAY,time.time()+3600):
            row=json.loads(original);row['completed']=completed;record.write_text(json.dumps(row));self.assertIsNone(self.cache.get(self.check))
        record.write_text('{broken');self.assertIsNone(self.cache.get(self.check))
        record.unlink();record.symlink_to(self.log);self.assertIsNone(self.cache.get(self.check))
    def test_retry_runs_only_missing_gate_and_never_caches_failure(self):
        checks=[('a','native-settings-smoke.py'),('s','login-session-smoke.py')];failed=True;executed=[]
        def run(check):
            if self.cache.get(check):return
            executed.append(check[0])
            if failed and check[0]=='a':raise ValueError('failed assertion')
            self.cache.put(check,self.log)
        # Real scheduler runs both preflight gates; the independent pass is retained.
        with self.assertRaises(ValueError):native.execute(checks,run,2,threading.Event())
        self.assertIsNone(self.cache.get(checks[0]));self.assertIsNotNone(self.cache.get(checks[1]))
        failed=False;executed.clear();native.execute(checks,run,2,threading.Event())
        self.assertEqual(['a'],executed)

    def test_directory_writable_by_others_is_refused(self):
        self.cache.directory.chmod(0o777)
        with self.assertRaises(ValueError):native.Receipts(self.cache.directory,self.base,'input-one')
    def test_pruning_is_bounded_and_leaves_unknown_files(self):
        self.cache.put(self.check,self.log);record=self.root/'receipts'/(self.cache.key(self.check)+'.json')
        stamp=time.time()-8*native.DAY;os.utime(record,(stamp,stamp));unknown=self.root/'receipts/operator-note';unknown.write_text('keep')
        self.cache.prune();self.assertFalse(record.exists());self.assertTrue(unknown.exists())


if __name__=='__main__':unittest.main()
