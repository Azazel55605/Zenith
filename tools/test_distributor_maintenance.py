#!/usr/bin/env python3
"""Real disposable build cleanup, ownership/path boundaries and concurrent locks."""
import fcntl
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('maintenance',ROOT/'deploy/distributor/maintenance.py')
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)

class MaintenanceTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name);self.state=self.root/'state';self.state.mkdir()
        self.public=self.root/'packages';self.public.mkdir();self.config={'root':str(self.public)}
        self.now=time.time();self.records={}
    def checkout(self,tag,status,age=0):
        self.records[tag]=dict(status=status,finished=self.now-age)
        path=self.state/('build-'+tag.replace('/','-'));path.mkdir()
        (path/'output').write_text('compiler cache');return path
    def test_completed_expired_and_protected_failed_and_active_work(self):
        complete=self.checkout('kernel/v1.0.0','complete')
        expired=self.checkout('kernel/v1.0.1','failed',m.MAX_AGE+1)
        latest=self.checkout('kernel/v1.0.2','failed',m.MAX_AGE+0.5)
        active=self.checkout('kernel/v1.0.3','building',m.MAX_AGE+100)
        (self.public/'release.json').write_text('immutable public receipt')
        (self.state/'releases.json').write_text(json.dumps(self.records))
        m.run(self.state,self.records,self.config)
        self.assertFalse(complete.exists());self.assertFalse(expired.exists())
        self.assertTrue(latest.exists());self.assertTrue(active.exists())
        self.assertEqual('immutable public receipt',(self.public/'release.json').read_text())
        self.assertEqual(self.records,json.loads((self.state/'releases.json').read_text()))
    def test_only_latest_two_attempts_survive_without_discarding_compiler_cache(self):
        checkout=self.checkout('kernel/v1.0.0','failed')
        parent=checkout/'obj/distributor';parent.mkdir(parents=True)
        for i in range(4):
            attempt=parent/('release-'+str(i));attempt.mkdir();(attempt/'disk.img').write_bytes(b'guest disk')
            os.utime(attempt,(self.now+i,self.now+i))
        m.run(self.state,self.records,self.config)
        self.assertEqual(['release-2','release-3'],sorted(p.name for p in parent.iterdir()))
        self.assertEqual('compiler cache',(checkout/'output').read_text())
    def test_symlink_checkout_cannot_delete_external_files(self):
        outside=self.root/'signing';outside.mkdir();(outside/'private.pem').write_text('protected')
        (self.state/'build-kernel-v1.0.0').symlink_to(outside)
        records={'kernel/v1.0.0':dict(status='complete')}
        report=m.run(self.state,records,self.config)
        self.assertEqual('protected',(outside/'private.pem').read_text());self.assertTrue(report['warnings'])
    def test_build_lock_defers_cleanup(self):
        checkout=self.checkout('kernel/v1.0.0','complete')
        with (self.root/'.distributor-build.lock').open('a') as lock:
            fcntl.flock(lock,fcntl.LOCK_EX)
            self.assertIn('deferred',m.run(self.state,self.records,self.config)['warnings'][0])
        self.assertTrue(checkout.exists())
    def test_history_tail_is_bounded_and_active_history_untouched(self):
        self.records={'kernel/v1.0.0':dict(status='complete'),'kernel/v1.0.1':dict(status='building')}
        for version in ('1.0.0','1.0.1'):(self.state/('kernel-v'+version+'.log')).write_bytes(b'x'*(m.LOG_BYTES+100)+b'latest output')
        m.run(self.state,self.records,self.config)
        self.assertEqual(m.LOG_BYTES,(self.state/'kernel-v1.0.0.log').stat().st_size)
        self.assertTrue((self.state/'kernel-v1.0.0.log').read_bytes().endswith(b'latest output'))
        self.assertGreater((self.state/'kernel-v1.0.1.log').stat().st_size,m.LOG_BYTES)
    def test_registered_orphans_require_matching_owner_and_inactive_age(self):
        scratch=self.root/'scratch';scratch.mkdir()
        with patch.object(m,'SCRATCH',scratch),patch.object(m.time,'time',return_value=self.now-m.MAX_AGE-10):
            good=scratch/'zn-good';good.mkdir();m.register_native(good,self.state/'kernel-v1.0.0.details.json')
            active=scratch/'zn-active';active.mkdir();m.register_native(active,self.state/'kernel-v1.0.1.details.json')
            unknown=scratch/'zn-unregistered';unknown.mkdir()
        records={'kernel/v1.0.0':dict(status='failed'),'kernel/v1.0.1':dict(status='building')}
        with patch.object(m,'SCRATCH',scratch):removed,warnings=m.cleanup_native(self.state,records,self.now)
        self.assertEqual(['zn-good'],removed);self.assertEqual([],warnings)
        self.assertFalse(good.exists());self.assertTrue(active.exists());self.assertTrue(unknown.exists())
    def test_registered_workspace_marker_mismatch_cannot_delete_directory(self):
        scratch=self.root/'scratch';scratch.mkdir();path=scratch/'zn-mismatch';path.mkdir()
        with patch.object(m,'SCRATCH',scratch),patch.object(m.time,'time',return_value=self.now-m.MAX_AGE-10):
            m.register_native(path,self.state/'kernel-v1.0.0.details.json')
        (path/'.zenith-owner.json').write_text('{}')
        with patch.object(m,'SCRATCH',scratch):
            removed,warnings=m.cleanup_native(self.state,{'kernel/v1.0.0':dict(status='failed')},self.now)
        self.assertEqual([],removed);self.assertTrue(warnings);self.assertTrue(path.exists())

    def test_expired_failed_checkout_with_linked_attempt_cannot_delete_external_data(self):
        checkout=self.checkout('kernel/v1.0.0','failed')
        parent=checkout/'obj/distributor';parent.mkdir(parents=True)
        outside=self.root/'backup';outside.mkdir();(outside/'important').write_text('keep')
        old=parent/'release-linked';old.symlink_to(outside);os.utime(outside,(self.now-100,self.now-100))
        for name in ('release-one','release-two'):(parent/name).mkdir()
        report=m.run(self.state,self.records,self.config)
        self.assertTrue(report['warnings']);self.assertEqual('keep',(outside/'important').read_text())
        self.assertTrue(old.is_symlink())

    def test_storage_report_permissions_and_readonly_preview(self):
        checkout=self.checkout('kernel/v1.0.0','complete')
        report=m.run(self.state,self.records,self.config,apply=False)
        self.assertTrue(checkout.exists());self.assertGreater(report['allocated'],0)
        self.assertGreater(report['total'],report['free'])
        self.assertEqual(0o640,(self.state/'storage.json').stat().st_mode&0o777)

if __name__=='__main__':unittest.main()
