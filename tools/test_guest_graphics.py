import unittest
from guest_graphics import records

class GraphicsRecordTests(unittest.TestCase):
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
