import unittest
from guest_graphics import records, terminal_paste
from unittest.mock import Mock

class GraphicsRecordTests(unittest.TestCase):
    def test_probe_exit_does_not_substitute_for_pasted_command_completion(self):
        old='user: PASTE_TWO\n'; new='user: PASTE_ONE\nuser: G_TERMINAL=0\n'
        machine=Mock();machine.log.return_value=old+new
        def complete(marker,offset,timeout):
            self.assertEqual(('user: PASTE_TWO\n',len(old),60),(marker,offset,timeout))
            machine.log.return_value+='user: PASTE_TWO\n';return True
        machine.wait.side_effect=complete
        self.assertEqual(new+'user: PASTE_TWO\n',terminal_paste(machine,len(old)))
        machine.wait.assert_called_once()
    def test_missing_or_reversed_pasted_output_still_fails(self):
        machine=Mock();machine.wait.return_value=False
        with self.assertRaisesRegex(RuntimeError,'did not complete'):terminal_paste(machine,0)
        machine.wait.return_value=True
        for raw in ('user: PASTE_TWO\n','user: PASTE_TWO\nuser: PASTE_ONE\n'):
            machine.log.return_value=raw
            with self.assertRaisesRegex(RuntimeError,'out of order'):terminal_paste(machine,0)
    def test_public_records_survive_known_trace_interruption(self):
        text='[zenith] [ 1.0] user: GUI_PO[LAPIC] Timer tick 1\nINTER\n[zenith] [ 1.1] user: GUI_[SCHED] Tick 2\nKEY\n'
        self.assertEqual(['GUI_POINTER','GUI_KEY'],records(text))
    def test_command_echo_and_partial_names_are_not_events(self):
        text='echo GUI_KEY\nuser: GUI_KEY_EXTRA\nuser: GUI_KEY truncated\nuser: GUI_KEY\n'
        self.assertEqual(['GUI_KEY_EXTRA','GUI_KEY'],records(text))
    def test_unknown_bracket_output_is_not_stripped(self):
        self.assertEqual([],records('user: GUI_[application] forged\nKEY\n'))
    def test_preserves_counts_for_focus_isolation(self):
        text='\n'.join('user: '+v for v in ('GUI_FOCUS_IN','GUI_POINTER','GUI_FOCUS_OUT','GUI_FOCUS_IN','GUI_KEY','GUI_KEY','G_INTERACTIVE=0'))+'\n'
        self.assertEqual(2,records(text).count('GUI_KEY'))
        self.assertIn('G_INTERACTIVE=0',records(text))

if __name__=='__main__':unittest.main()
