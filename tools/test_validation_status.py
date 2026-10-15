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
        self.assertEqual(1,len(m.sent))  # An actual failing exit needs no readback/replay.
        self.assertEqual(1, m._validation_failure['observed_status'])
        self.assertEqual('nonzero-or-unexpected-status', m._validation_failure['reason'])
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

    def test_progress_diagnostic_uses_last_real_stage_not_scheduler_ticks(self):
        m=self.machine([])
        m.text=('PRIVATE-CREDENTIAL\nfetch progress [#####-----------] 35% 5.9/16.8 MiB\r'
                'Staging signed kernel 2.0.0\rkernel copy 12%\r'
                '[SCHED] Tick 56000 enabled=1\n[GC] Collection #8\n')
        result=status.observation(m,'P4_KERNEL_STAGE',0,'')
        self.assertEqual(dict(observed_status=None,last_guest_phase='kernel-copy-12%'),result)
        self.assertNotIn('PRIVATE',str(result))

    def test_status_requires_complete_line_and_does_not_match_command_echo(self):
        m=self.machine([])
        for text in ('echo P4_STAGE=0\r','P4_STAGE=0', 'prefix P4_STAGE=0\n', 'P4_STAGE=0oops\n'):
            m.text=text
            self.assertIsNone(status.observation(m,'P4_STAGE',0,'')['observed_status'])
        m.text='P4_STAGE=130\r\n'
        self.assertEqual(130,status.observation(m,'P4_STAGE',0,'')['observed_status'])
        m.text='[zenith] [ 126.132] user: K_STAGE=1\r\n'
        self.assertEqual(1,status.observation(m,'K_STAGE',0,' | logger')['observed_status'])

    def test_failure_diagnostic_distinguishes_dead_guest_from_missing_output(self):
        m=self.machine([]);m.text='kernel copy 100%\r';m.poll=lambda:7
        self.assertFalse(status.failed(m,'P4_STAGE','P4_STAGE=0','P4_STAGE=',0,'','status-unavailable'))
        self.assertEqual('qemu-exited',m._validation_failure['reason'])
        self.assertEqual(7,m._validation_failure['qemu_exit'])
        self.assertEqual('kernel-copy-100%',m._validation_failure['last_guest_phase'])

    def test_late_nonzero_result_does_not_wait_the_rest_of_the_deadline(self):
        m=self.machine([False,False]);m.send('pkg update --yes; echo P4_STAGE=$?\r')
        path=m._validation_status[1];m.text='boot'+path+'\r\n'
        def replies():
            yield False
            m.text+='P4_STAGE=1\r\n[console] root@zenith:~$ '
            yield False
        m.replies=replies()
        self.assertFalse(m.wait('P4_STAGE=0',4,600))
        self.assertEqual(1,len(m.sent))
        self.assertEqual(2,len(m.waited))
        self.assertEqual(1,m._validation_failure['observed_status'])

    def test_noise_only_is_not_application_progress(self):
        m=self.machine([]);m.text='[SCHED] Tick 1\n[GC] Freed 50 objects\n'
        self.assertEqual('unknown',status.observation(m,'P4_STAGE',0,'')['last_guest_phase'])

class StageBudgetTests(unittest.TestCase):
    def setUp(self):
        self.now=0
        clock=patch.object(status.time,'monotonic',side_effect=lambda:self.now)
        clock.start();self.addCleanup(clock.stop)

    def test_advancing_copy_can_finish_after_original_total_deadline(self):
        budget=status.StageBudget(600,True)
        self.now=100;self.assertEqual(600,budget.remaining('kernel-download-20%'))
        self.now=500;self.assertEqual(600,budget.remaining('kernel-copy-5%'))
        self.now=650;self.assertEqual(600,budget.remaining('kernel-copy-100%'))
        self.now=900;self.assertEqual(600,budget.remaining('boot-publication'))
        self.assertEqual(1800,budget.metrics()['hard_seconds'])

    def test_repeated_regressing_noise_and_invalid_percent_do_not_extend(self):
        budget=status.StageBudget(600,True)
        self.now=30;self.assertEqual(600,budget.remaining('kernel-copy-90%'))
        for phase in ('kernel-copy-90%','kernel-copy-50%','unknown','kernel-copy-101%','kernel-download-100%'):
            self.now=500;self.assertEqual(130,budget.remaining(phase),phase)
        self.now=630;self.assertEqual(0,budget.remaining('kernel-copy-90%'))
        self.now=631;self.assertEqual(0,budget.remaining('kernel-copy-100%'))

    def test_overall_limit_wins_even_with_real_forward_progress(self):
        budget=status.StageBudget(600,True)
        for moment,phase in ((500,'kernel-download-10%'),(1000,'kernel-download-80%'),(1500,'kernel-copy-10%')):
            self.now=moment;self.assertGreater(budget.remaining(phase),0)
        self.now=1799;self.assertEqual(1,budget.remaining('kernel-copy-99%'))
        self.now=1800;self.assertEqual(0,budget.remaining('kernel-copy-100%'))

    def test_other_waits_keep_the_original_fixed_deadline(self):
        budget=status.StageBudget(600,False)
        self.now=500;self.assertEqual(100,budget.remaining('kernel-copy-100%'))
        self.now=600;self.assertEqual(0,budget.remaining('boot-publication'))
        self.assertEqual('fixed',budget.metrics()['policy'])

    def test_small_data_download_does_not_hide_kernel_download_progress(self):
        m=StatusTests().machine([])
        m.text=('fetch progress [################] 100% 0.1/0.1 MiB\r'
                'Downloading zenith-kernel 2.0.0\n'
                'fetch progress [#---------------] 5% 0.8/16.8 MiB\r')
        phase=status.observation(m,'P4_KERNEL_STAGE',0,'')['last_guest_phase']
        self.assertEqual('kernel-download-5%',phase)
        budget=status.StageBudget(600,True)
        self.now=10;budget.remaining('download-100%')
        self.now=500;self.assertEqual(600,budget.remaining(phase))

    def test_adapter_waits_for_real_success_beyond_original_deadline_without_replay(self):
        m=StatusTests().machine([]);m.send('pkg update --yes; echo P4_KERNEL_STAGE=$?\r')
        m.text='boot'+m._validation_status[1]+'\r\n'
        def replies():
            for moment,phase in ((30,'kernel-download-10%'),(590,'kernel-copy-90%'),(650,'kernel-copy-100%')):
                self.now=moment;m.text+=phase.replace('kernel-download-','fetch progress [] ').replace('kernel-copy-','kernel copy ')+'\r'
                # Include the complete download representation for the observation parser.
                if phase.startswith('kernel-download'):
                    m.text+='Downloading zenith-kernel 2.0.0\nfetch progress [] 10% 1.6/16.8 MiB\r'
                yield False
            self.now=720;m.text+='P4_KERNEL_STAGE=0\r\n';yield True
        m.replies=replies()
        self.assertTrue(m.wait('P4_KERNEL_STAGE=0',4,600))
        self.assertEqual(1,len(m.sent))
        self.assertEqual('advancing-kernel-stage',m._validation_budget['policy'])
        self.assertIn('P4_KERNEL_STAGE=0',m.text)

    def test_adapter_ignores_scheduler_activity_and_repeated_copy_at_deadline(self):
        m=StatusTests().machine([]);m.send('pkg update --yes; echo P4_KERNEL_STAGE=$?\r')
        m.text='boot'+m._validation_status[1]+'\r\n'
        def replies():
            self.now=30;m.text+='kernel copy 100%\r';yield False
            while True:
                self.now+=5;m.text+='[SCHED] Tick 100 enabled=1\n'+'kernel copy 100%\r'
                yield False
        m.replies=replies()
        self.assertFalse(m.wait('P4_KERNEL_STAGE=0',4,600))
        self.assertEqual(630,self.now)
        self.assertEqual(1,len(m.sent))
        self.assertEqual('operation-deadline',m._validation_failure['reason'])

    def test_lost_echo_recovery_path_also_observes_advancing_progress(self):
        m=StatusTests().machine([]);m.send('pkg update --yes; echo P4_KERNEL_STAGE=$?\r')
        m.text='boot'  # No intact submitted suffix, as with interleaved serial echo.
        def replies():
            for moment,percent in ((30,10),(200,30),(400,60),(590,90),(650,100)):
                self.now=moment;m.text+='kernel copy '+str(percent)+'%\r';yield False
            self.now=720;m.text+='P4_KERNEL_STAGE=0\r\n';yield True
        m.replies=replies()
        self.assertTrue(m.wait('P4_KERNEL_STAGE=0',4,600))
        self.assertEqual(1,sum('pkg update' in text for text in m.sent))
        self.assertEqual(3,sum(text.startswith('cat /tmp/validation-status-') for text in m.sent))

    def test_completed_progress_never_overrides_a_real_failing_status(self):
        m=StatusTests().machine([False]);m.send('pkg update --yes; echo P4_KERNEL_STAGE=$?\r')
        m.text='boot\rkernel copy 100%\rKernel staged; running kernel unchanged\rP4_KERNEL_STAGE=1\r\n'
        self.assertFalse(m.wait('P4_KERNEL_STAGE=0',4,600))
        self.assertEqual(1,m._validation_failure['observed_status'])
        self.assertEqual(1,len(m.sent))

    def test_only_exact_combined_staging_operation_gets_progress_budget(self):
        m=StatusTests().machine([])
        for text in ('pkg update --yes; echo P4_OTHER=$?\r',
                     'pkg install --yes hello; echo P4_KERNEL_STAGE=$?\r',
                     'echo pkg update --yes; echo P4_KERNEL_STAGE=$?\r'):
            m.send(text);self.assertFalse(m._validation_combined_stage)
        m.send('pkg update --yes; echo P4_KERNEL_STAGE=$?\r')
        self.assertTrue(m._validation_combined_stage)

if __name__=='__main__':unittest.main()
