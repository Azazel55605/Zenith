#!/usr/bin/env python3
"""Recovery readiness, real authentication and input transport regressions."""
from pathlib import Path
import re
import socket
import tempfile
import threading
import types
import unittest
from unittest.mock import Mock, patch
import recovery_console as recovery


class Tests(unittest.TestCase):
    def console(self, log):
        machine = Mock(); machine.log.return_value = log; machine.process.poll.return_value = None
        headless = types.SimpleNamespace(efi=types.SimpleNamespace(smoke=types.SimpleNamespace(COSMOS_NOISE=re.compile(r'\[noise\]\n'))))
        return recovery.RecoveryConsole(machine, Path('/tmp/not-a-real-monitor'), headless)

    def test_serial_boot_still_authenticates_and_uses_uart(self):
        c=self.console('kernel: headless ready (installed)')
        self.assertTrue(c.wait_startup()); self.assertFalse(c.graphical)
        c.machine.wait.return_value=True
        self.assertTrue(c.login('root','secret'));c.send('boot status\r')
        self.assertEqual([(('login root\r',),{}),(('secret\r',),{}),(('boot status\r',),{})],c.machine.send.call_args_list)

    def test_display_boot_requires_real_login_then_routes_to_keyboard(self):
        c=self.console('session: login ready; selection=headless')
        self.assertTrue(c.wait_startup());self.assertTrue(c.graphical)
        c.machine.wait.return_value=True
        with patch.object(recovery,'keyboard_type') as keyboard:
            self.assertTrue(c.login('root','secret'));c.send('boot status\r')
        self.assertEqual(['root\t','secret\n','boot status\n'],[x.args[-1] for x in keyboard.call_args_list])
        c.machine.wait.assert_called_once_with('headless ready (local text console)',len(c.machine.log()),180)
        c.machine.send.assert_not_called()

    def test_greeter_does_not_grant_authenticated_command_access(self):
        c=self.console('session: login ready; selection=headless');self.assertTrue(c.wait_startup())
        with self.assertRaisesRegex(RuntimeError,'before authentication'):c.send('boot rollback\r')
        c.machine.wait.return_value=False
        with patch.object(recovery,'keyboard_type'):
            self.assertFalse(c.login('root','wrong'))
        with self.assertRaisesRegex(RuntimeError,'before authentication'):c.send('boot rollback\r')

    def test_desktop_selection_is_not_silently_treated_as_console(self):
        c=self.console('session: login ready; selection=de');self.assertTrue(c.wait_startup())
        with patch.object(recovery,'keyboard_type') as keyboard:
            with self.assertRaisesRegex(RuntimeError,'saved Headless'):c.login('root','secret')
            keyboard.assert_not_called()

    def test_wrong_kernel_crash_exit_and_deadline_fail_with_state(self):
        for log,state in [('ZENITH_BROKEN_KERNEL_ENTRY','broken kernel selected'),('[INT] FATAL: Exception 0','guest startup failure'),('panic: failed','guest startup failure')]:
            c=self.console(log);self.assertFalse(c.wait_startup());self.assertEqual(state,c.state)
        c=self.console('');c.machine.process.poll.return_value=1
        self.assertFalse(c.wait_startup());self.assertEqual('QEMU exited',c.state)
        c=self.console('');self.assertFalse(c.wait_startup(timeout=0));self.assertEqual('startup deadline reached',c.state)

    def test_split_boot_message_and_unrecognized_boot_still_require_state(self):
        c=self.console('session: login [noise]\nready; selection=headless');self.assertTrue(c.wait_startup())
        c=self.console('unrelated output')
        with self.assertRaisesRegex(RuntimeError,'recognized startup'):c.login('root','secret')

    def test_hmp_response_reads_split_prompts_and_reports_error(self):
        for reply,failed in [(b'\r\n(qemu) ',False),(b'Error: no display\r\n(qemu) ',True)]:
            with tempfile.TemporaryDirectory() as directory:
                path=Path(directory)/'m.sock';server=socket.socket(socket.AF_UNIX);server.bind(str(path));server.listen(1)
                def serve():
                    with server.accept()[0] as connection:
                        connection.sendall(b'QEMU\r\n(qe');connection.sendall(b'mu) ')
                        connection.recv(4096);connection.sendall(reply)
                thread=threading.Thread(target=serve);thread.start()
                try:
                    if failed:
                        with self.assertRaisesRegex(RuntimeError,'command failed'):recovery.monitor_command(path,'screendump fixture.ppm')
                    else:recovery.monitor_command(path,'sendkey down 30')
                finally:thread.join(timeout=5);server.close()

if __name__=='__main__':unittest.main()
