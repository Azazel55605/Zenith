#!/usr/bin/env python3
"""Shared authentication budgets, strict rejection, privacy and unchanged-tag support."""
import contextlib
import importlib.util
import io
from pathlib import Path
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('guest_auth_test',ROOT/'tools/guest_auth.py')
auth=importlib.util.module_from_spec(spec);spec.loader.exec_module(auth)

class AuthenticationTests(unittest.TestCase):
    def machine(self, events=(), exit_at=None):
        test=self;self.now=0
        clock=patch.object(auth.time,'monotonic',side_effect=lambda:self.now)
        clock.start();self.addCleanup(clock.stop)
        class Machine:
            def __init__(self):self.calls=[];self.sent=[];self.text='';self.process=self
            def poll(self):return 1 if exit_at is not None and test.now>=exit_at else None
            def log(self):return self.text
            def send(self,text):self.sent.append(text)
            def wait(self,marker,offset=0,timeout=180):
                self.calls.append((marker,offset,timeout));test.now+=timeout
                self.text=''.join(text for at,text in events if at<=test.now)
                return marker in self.text[offset:]
        auth.install(Machine);return Machine()

    def test_slow_real_completion_is_observed_without_resending_credentials(self):
        m=self.machine([(40,'password updated for root\r\n')])
        self.assertTrue(m.wait('password updated for root',timeout=30))
        self.assertEqual(40,self.now)
        self.assertEqual(180,m._validation_auth['effective_seconds'])
        self.assertEqual('password-update',m._validation_auth['phase'])
        self.assertEqual([],m.sent)
        self.assertTrue(all(call[2]<=5 for call in m.calls))

    def test_missing_completion_fails_at_bounded_deadline_despite_noise(self):
        m=self.machine([(1,'[SCHED] Tick 1\n[GC] Collection #1\n')])
        self.assertFalse(m.wait('password updated for root',timeout=30))
        self.assertEqual(180,self.now)
        self.assertEqual('deadline',m._validation_auth['reason'])
        self.assertEqual([],m.sent)

    def test_confirmation_prompt_is_not_mistaken_for_a_completed_hash(self):
        m=self.machine([(1,'Repeat password: ')])
        self.assertFalse(m.wait('password updated for root',timeout=30))
        self.assertEqual('deadline',m._validation_auth['reason'])

    def test_explicit_rejection_fails_early_instead_of_waiting_or_retrying(self):
        m=self.machine([(5,'login: authentication failed\r\n')])
        self.assertFalse(m.wait('authenticated as root',timeout=30))
        self.assertEqual(5,self.now)
        self.assertEqual('credential-rejected',m._validation_auth['reason'])
        self.assertEqual([],m.sent)

    def test_mismatch_and_fatal_and_process_exit_are_distinct_failures(self):
        for text,reason in [('passwd: passwords do not match; unchanged\r\n','confirmation-mismatch'),
                            ('[INT] FATAL: Exception 0\r\n','guest-fatal'),
                            ('passwd: secure entropy unavailable; password unchanged\r\n','entropy-unavailable')]:
            with self.subTest(reason=reason):
                m=self.machine([(1,text)])
                self.assertFalse(m.wait('password updated for root',timeout=30))
                self.assertEqual(reason,m._validation_auth['reason'])
        m=self.machine(exit_at=5)
        self.assertFalse(m.wait('authenticated as root',timeout=30))
        self.assertEqual('qemu-exited',m._validation_auth['reason'])

    def test_negative_authentication_test_still_waits_for_real_rejection(self):
        m=self.machine([(5,'login: authentication failed\r\n')])
        self.assertTrue(m.wait('authentication failed',timeout=30))
        self.assertIsNone(m._validation_auth)
        self.assertEqual([('authentication failed',0,30)],m.calls)

    def test_other_waits_keep_requested_timeout_and_offsets(self):
        m=self.machine([(1,'boot ready\n')])
        self.assertTrue(m.wait('boot ready',0,12))
        self.assertEqual([('boot ready',0,12)],m.calls)
        self.assertIsNone(m._validation_auth)

    def test_previous_failure_before_offset_does_not_poison_next_login(self):
        old='login: authentication failed\r\n'
        m=self.machine([(1,old),(10,'authenticated as root\r\n')])
        self.assertTrue(m.wait('authenticated as root',len(old),30))
        self.assertEqual('passed',m._validation_auth['outcome'])

    def test_diagnostics_do_not_disclose_raw_input_or_usernames(self):
        secret='DISPOSABLE-SECRET-NEVER-PUBLISH'
        m=self.machine([(1,'login: authentication failed\n'+secret)])
        log=io.StringIO()
        with contextlib.redirect_stdout(log):self.assertFalse(m.wait('authenticated as privateuser',timeout=30))
        self.assertNotIn(secret,log.getvalue());self.assertNotIn('privateuser',log.getvalue())
        self.assertNotIn(secret,str(m._validation_auth))

    def test_prompt_and_completion_budgets_are_separate_and_preserve_larger_requests(self):
        m=self.machine([(45,'Repeat password: ')])
        self.assertTrue(m.wait('Repeat password: ',timeout=30))
        self.assertEqual('password-confirmation',m._validation_auth['phase'])
        self.assertEqual(60,m._validation_auth['effective_seconds'])
        m=self.machine([(200,'password updated for user\n')])
        self.assertTrue(m.wait('password updated for user',timeout=300))
        self.assertEqual(300,m._validation_auth['effective_seconds'])

    def test_wait_does_not_satisfy_another_users_authentication_or_partial_output(self):
        m=self.machine([(1,'authenticated as user\n')])
        self.assertFalse(m.wait('authenticated as root',timeout=30))
        self.assertEqual('deadline',m._validation_auth['reason'])

    def test_username_prefix_and_incomplete_record_never_prove_authentication(self):
        for text in ('authenticated as rootadmin\n','authenticated as root'):
            with self.subTest(text=text):
                m=self.machine([(1,text)])
                with patch.object(auth.time,'sleep'):
                    self.assertFalse(m.wait('authenticated as root',timeout=30))
                self.assertEqual('deadline',m._validation_auth['reason'])

    def test_debug_prefix_does_not_hide_a_complete_authentication_result(self):
        m=self.machine([(5,'remaining debug fragment authenticated as root\r\n')])
        self.assertTrue(m.wait('authenticated as root',timeout=30))

    def test_transport_exception_preserves_error_and_records_phase(self):
        class Machine:
            def wait(self,*args):raise OSError('original transport failure')
        auth.install(Machine);m=Machine()
        with self.assertRaisesRegex(OSError,'original transport failure'):
            m.wait('password updated for root',timeout=30)
        self.assertEqual('password-update',m._validation_auth['phase'])
        self.assertEqual('error',m._validation_auth['outcome'])
        self.assertEqual('wait-error',m._validation_auth['reason'])

    def test_install_is_idempotent_when_status_wrappers_change_wait(self):
        m=self.machine([(5,'password updated for root\n')]);original=type(m).wait
        def wrapper(*args,**kwargs):return original(*args,**kwargs)
        type(m).wait=wrapper;auth.install(type(m))
        self.assertIs(wrapper,type(m).wait)
        self.assertTrue(m.wait('password updated for root',timeout=30))

if __name__=='__main__':unittest.main()
