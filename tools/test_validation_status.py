#!/usr/bin/env python3
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('status', ROOT/'deploy/distributor/validation-status.py')
status = importlib.util.module_from_spec(spec); spec.loader.exec_module(status)

class StatusTests(unittest.TestCase):
    def machine(self, replies):
        class Machine:
            def __init__(self):
                self.sent=[]; self.waited=[]; self.process=self; self.text='boot'; self.replies=iter(replies)
            def poll(self): return None
            def log(self): return self.text
            def send(self,text): self.sent.append(text)
            def wait(self,marker,offset=0,timeout=180):
                self.waited.append((marker,offset,timeout));return next(self.replies)
        status.install(Machine)
        return Machine()

    def test_status_is_saved_before_logger_and_operation_runs_once(self):
        m=self.machine([False,True])
        with patch.object(status.secrets,'token_hex',return_value='unique'):
            m.send('pkg kernel install /missing; echo K_DENIED=$? | logger\r')
        self.assertTrue(m.wait('user: K_DENIED=',4,600))
        self.assertEqual("pkg kernel install /missing; printf '%s\\n' K_DENIED=$? > /tmp/validation-status-unique; cat /tmp/validation-status-unique | logger\r",m.sent[0])
        self.assertEqual(['cat /tmp/validation-status-unique | logger\r'],m.sent[1:])
        self.assertEqual(1,sum('pkg kernel install' in text for text in m.sent))

    def test_read_failure_is_bounded_and_does_not_repeat_operation(self):
        m=self.machine([False]*5);m.send('pkg update --yes; echo UPDATE=$? | logger\r')
        self.assertFalse(m.wait('user: UPDATE=',4,600))
        self.assertEqual(4,len(m.sent))
        self.assertTrue(all(s.startswith('cat /tmp/validation-status-') for s in m.sent[1:]))

    def test_plain_repository_status_recovers_without_repeating_update(self):
        m=self.machine([False,True])
        with patch.object(status.secrets,'token_hex',return_value='plain'):
            m.send('pkg update --yes; echo P4_KERNEL_STAGE=$?\r')
        self.assertTrue(m.wait('P4_KERNEL_STAGE=0',4,600))
        self.assertEqual("pkg update --yes; printf '%s\\n' P4_KERNEL_STAGE=$? > /tmp/validation-status-plain; cat /tmp/validation-status-plain\r",m.sent[0])
        self.assertEqual(['cat /tmp/validation-status-plain\r'],m.sent[1:])
        self.assertNotIn('P4_KERNEL_STAGE=0',''.join(m.sent))

    def test_plain_busy_update_waits_without_queueing_readback(self):
        m=self.machine([False,True]);m.send('pkg update --yes; echo P4_STAGE=$?\r')
        path=m._validation_status[1]
        m.text='boot'+path+'\r\n'
        self.assertTrue(m.wait('P4_STAGE=0',4,600))
        self.assertEqual(1,len(m.sent));self.assertEqual(5,m.waited[1][2])

    def test_plain_wrong_exit_code_remains_failure(self):
        m=self.machine([False]*5);m.send('false; echo P4_STAGE=$?\r')
        path=m._validation_status[1]
        m.text='boot'+path+'\r\nP4_STAGE=1\r\n[console] root@zenith:~$ '
        self.assertFalse(m.wait('P4_STAGE=0',4,600))
        self.assertEqual(4,len(m.sent))
        self.assertNotIn('P4_STAGE=0',''.join(m.sent))

    def test_plain_intact_result_does_not_read_back(self):
        m=self.machine([True]);m.send('true; echo P4_OK=$?\r')
        self.assertTrue(m.wait('P4_OK=0',4,600));self.assertEqual(1,len(m.sent))

    def test_plain_busy_deadline_fails_without_readback_or_replay(self):
        m=self.machine([False]);m.send('pkg update --yes; echo P4_STAGE=$?\r')
        m.text='boot'+m._validation_status[1]+'\r\n'
        with patch.object(status.time,'monotonic',side_effect=[0,601]):
            self.assertFalse(m.wait('P4_STAGE=0',4,600))
        self.assertEqual(1,len(m.sent))

    def test_intact_status_needs_no_readback(self):
        m=self.machine([True]);m.send('false; echo WRONG=$? | logger\r')
        self.assertTrue(m.wait('user: WRONG=',4,600))
        self.assertEqual(1,len(m.sent))
        # Adapter observes a marker only. It never fabricates an expected exit code.
        self.assertNotIn('WRONG=0',m.sent[0]);self.assertIn('WRONG=$?',m.sent[0])

    def test_other_input_and_markers_keep_original_behavior(self):
        m=self.machine([False,True]);m.send('login root\r')
        self.assertEqual(['login root\r'],m.sent)
        self.assertFalse(m.wait('Password: ',4,17))
        m.send('false; echo X=$? | logger\r')
        self.assertTrue(m.wait('some other marker',4,23))
        self.assertEqual(('some other marker',4,23),m.waited[-1])

    def test_exact_expected_status_recovers_without_fabricating_value(self):
        m=self.machine([False,True]);m.send('false; echo EXACT=$? | logger\r')
        self.assertTrue(m.wait('user: EXACT=1',4,600))
        self.assertEqual(('user: EXACT=1',4,30),m.waited[1])
        self.assertTrue(m.sent[1].startswith('cat /tmp/validation-status-'))
        self.assertNotIn('EXACT=1',m.sent[1])

    def test_busy_operation_does_not_queue_readback_commands(self):
        m=self.machine([False,True]);m.send('pkg update --yes; echo BUSY=$? | logger\r')
        path=m._validation_status[1]
        m.text='boot'+path+' | logger\r\n'
        self.assertTrue(m.wait('user: BUSY=',4,600))
        self.assertEqual(1,len(m.sent))
        self.assertEqual(5,m.waited[1][2])

    def test_completed_prompt_allows_readback_but_never_counts_as_success(self):
        m=self.machine([False]*5);m.send('false; echo DONE=$? | logger\r')
        path=m._validation_status[1]
        m.text='boot'+path+' | logger\r\n[console] root@zenith:~$ '
        self.assertFalse(status.busy(m,path,4))
        self.assertFalse(m.wait('user: DONE=',4,600))
        self.assertEqual(4,len(m.sent))

    def test_dead_guest_does_not_read_back(self):
        m=self.machine([False]);m.send('false; echo DEAD=$? | logger\r');m.poll=lambda:1
        self.assertFalse(m.wait('user: DEAD=',4,600));self.assertEqual(1,len(m.sent))

    def test_each_invocation_gets_a_fresh_file_and_new_input_clears_record(self):
        m=self.machine([False])
        with patch.object(status.secrets,'token_hex',side_effect=['one','two']):
            m.send('true; echo A=$? | logger\r');m.send('false; echo A=$? | logger\r')
        self.assertIn('status-one',m.sent[0]);self.assertIn('status-two',m.sent[1])
        m.send('echo hello\r');self.assertIsNone(m._validation_status)
        self.assertFalse(m.wait('user: A=',4,9));self.assertEqual(3,len(m.sent))

if __name__=='__main__':unittest.main()
