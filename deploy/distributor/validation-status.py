"""Recover acceptance exit statuses without repeating the operation being tested."""
import re
import secrets
import time

STATUS = re.compile(r'(.+); echo ([A-Z][A-Za-z0-9_-]{0,63})=\$\?( \| logger)?\r', re.DOTALL)


NOISE = re.compile(r"\[(?!zenith\])[A-Za-z][A-Za-z0-9 ]*\][^\n]*\n")
PROMPT = re.compile(r'(?:^|\r?\n)\[console\] [^\r\n]*[$#] ')


def observation(machine, name, offset, output):
    """Only publish structured status/progress, never arbitrary guest text/secrets."""
    raw = NOISE.sub('', machine.log()[offset:])
    raw = re.sub(r'\x1b\[[0-9;]*[A-Za-z]', '', raw)
    prefix = (r'(?:\[zenith\] \[[^\]\r\n]*\] )?user: ' if output else '')
    codes = re.findall(r'(?:^|[\r\n])' + prefix + re.escape(name) + r'=([0-9]{1,3})(?=[\r\n])', raw)
    phase = 'unknown'
    # Select the last *recognized* milestone. UART/debug volume is not progress.
    milestones = [
        (r'fetch progress [^\r\n]*?([0-9]{1,3})% [0-9.]+/[0-9.]+ MiB', 'download'),
        (r'kernel copy ([0-9]{1,3})%', 'kernel-copy'),
        (r'verified signed zenith-kernel ', 'kernel-verified'),
        (r'Preparing transaction backups', 'data-backup'),
        (r'Committing package database', 'data-commit'),
        (r'data package updates committed: [0-9]+', 'data-committed'),
        (r'Staging signed kernel ', 'kernel-staging'),
        (r'Publishing next-boot generation', 'boot-publication'),
        (r'Kernel staged; running kernel unchanged', 'kernel-staged'),
    ]
    last = -1
    kernel_download = raw.rfind('Downloading zenith-kernel ')
    for pattern, stage in milestones:
        for match in re.finditer(pattern, raw):
            if match.start() > last:
                last = match.start()
                if stage == 'download' and kernel_download >= 0 and match.start() > kernel_download:
                    stage = 'kernel-download'
                phase = stage + ('-' + match[1] + '%' if match.lastindex else '')
    return dict(observed_status=int(codes[-1]) if codes else None, last_guest_phase=phase)


def failed(machine, name, marker, prefix, offset, output, reason):
    result = observation(machine, name, offset, output)
    suffix = marker[len(prefix):]
    result['expected_status'] = int(suffix) if suffix.isdigit() else None
    result['qemu_exit'] = machine.process.poll()
    if result['observed_status'] is not None and result['expected_status'] is not None:
        reason = 'nonzero-or-unexpected-status' if result['observed_status'] != result['expected_status'] else reason
    elif result['qemu_exit'] is not None:
        reason = 'qemu-exited'
    result['reason'] = reason
    machine._validation_failure = result
    print('Guest status failure: ' + name + '; ' + '; '.join(key + '=' + str(value) for key, value in result.items()), flush=True)
    serial = getattr(machine, 'serial', None)
    if serial is not None:
        print('Retained guest serial: ' + str(serial), flush=True)
    return False


class StageBudget:
    """A bounded no-progress deadline for the one combined kernel-update gate.

    Advancing guest milestones refresh an idle budget; serial bytes, timers,
    repeated or regressing progress cannot keep a stuck operation alive.
    A separate 3x wall limit always wins. Neither deadline implies success.
    """
    def __init__(self, timeout, enabled):
        self.timeout = timeout
        self.enabled = enabled
        self.started = time.monotonic()
        self.idle_deadline = self.started + timeout
        self.hard_deadline = self.started + timeout * (3 if enabled else 1)
        self.high_water = (-1, -1)
        self.phase = 'unknown'
        self.next_report = self.started + 60

    def remaining(self, phase):
        now = time.monotonic()
        if self.enabled:
            ranks = {'download': 0, 'kernel-download': 1, 'kernel-verified': 2, 'data-backup': 3,
                     'data-commit': 4, 'data-committed': 5, 'kernel-staging': 6,
                     'kernel-copy': 7, 'boot-publication': 8, 'kernel-staged': 9}
            match = re.fullmatch(r'(download|kernel-download|kernel-copy)-([0-9]{1,3})%', phase)
            stage, percent = (match[1], int(match[2])) if match else (phase, 0)
            token = (ranks.get(stage, -1), percent)
            # Deadlines cannot be revived by output first observed after expiry.
            if token[0] >= 0 and 0 <= percent <= 100 and token > self.high_water \
                    and now < min(self.idle_deadline, self.hard_deadline):
                self.high_water = token
                self.phase = phase
                self.idle_deadline = now + self.timeout
        return max(0, min(self.idle_deadline, self.hard_deadline) - now)

    def metrics(self):
        return dict(policy='advancing-kernel-stage' if self.enabled else 'fixed',
                    idle_seconds=self.timeout, hard_seconds=self.timeout * (3 if self.enabled else 1),
                    last_guest_phase=self.phase)

    def report(self, name):
        now = time.monotonic()
        if self.enabled and now >= self.next_report:
            self.next_report = now + 60
            pending = '; copy complete; verified staging result still pending' if self.phase == 'kernel-copy-100%' else ''
            print('Guest operation ' + name + ': elapsed=' + str(round(now-self.started, 1)) +
                  's; phase=' + self.phase + '; idle_remaining=' + str(round(max(0, self.idle_deadline-now), 1)) +
                  's; hard_remaining=' + str(round(max(0, self.hard_deadline-now), 1)) + 's' + pending, flush=True)


def busy(machine, path, offset, output=' | logger'):
    # Only postpone readback when the exact submitted suffix is visible and no
    # subsequent completed shell prompt exists. Damaged/unknown echoes retain
    # the old recovery path. Never treat a prompt as successful exit status.
    raw = NOISE.sub('', machine.log()[offset:])
    submitted = re.search(re.escape(path + output) + r'(?:\r\n|\n)', raw)
    return submitted is not None and PROMPT.search(raw, submitted.end()-1) is None


def install(machine_type):
    original_send, original_wait = machine_type.send, machine_type.wait

    def send(machine, text):
        # Adapt only the harness's exact status suffix, not interactive input or
        # arbitrary shell commands. Save $? before reporting/filesystem work changes it.
        match = STATUS.fullmatch(text)
        machine._validation_status = None
        machine._validation_failure = None
        machine._validation_budget = None
        machine._validation_combined_stage = False
        if match:
            operation, name, output = match.groups()
            machine._validation_combined_stage = operation == 'pkg update --yes' and name == 'P4_KERNEL_STAGE'
            output = output or ''
            path = '/tmp/validation-status-' + secrets.token_hex(16)
            offset = len(machine.log())
            machine._validation_status = (name, path, offset, output)
            text = (operation + "; printf '%s\\n' " + name + '=$? > ' + path +
                    '; cat ' + path + output + '\r')
        return original_send(machine, text)

    def wait(machine, marker, offset=0, timeout=180):
        record = getattr(machine, '_validation_status', None)
        prefix = (('user: ' if record[3] else '') + record[0] + '=') if record else ''
        status_marker = marker == prefix or (bool(prefix) and marker.startswith(prefix) and marker[len(prefix):].isdigit())
        if not record or not status_marker or offset > record[2]:
            return original_wait(machine, marker, offset, timeout)
        budget = StageBudget(timeout, getattr(machine, '_validation_combined_stage', False))
        name, path, _, output = record
        def remaining():
            phase = observation(machine, name, offset, output)['last_guest_phase'] if budget.enabled else 'unknown'
            available = budget.remaining(phase)
            machine._validation_budget = budget.metrics()
            if budget.enabled: budget.report(name)
            return available
        machine._validation_budget = budget.metrics()
        initial = min(timeout / 4, 30) if budget.enabled else min(timeout, 30)
        if original_wait(machine, marker, offset, initial):
            return True
        def wrong_status():
            observed = observation(machine, name, offset, output)['observed_status']
            expected = marker[len(prefix):]
            if expected.isdigit() and observed is not None and observed != int(expected):
                failed(machine, name, marker, prefix, offset, output, 'unexpected-status')
                return True
            return False
        if wrong_status(): return False
        if busy(machine, path, offset, output):
            print('Guest operation '+name+' is still running; postponing status readback', flush=True)
            while busy(machine, path, offset, output):
                available = remaining()
                if available <= 0 or machine.process.poll() is not None:
                    print('Guest operation deadline reached: '+name+'; operation was not repeated', flush=True)
                    return failed(machine, name, marker, prefix, offset, output, 'operation-deadline')
                if original_wait(machine, marker, offset, min(available, 5)):
                    return True
                if wrong_status(): return False
        # Replay only the saved observation. An absent file, dead/hung guest or
        # unreadable response still fails; the unchanged harness checks its value.
        for attempt in range(3):
            available = remaining()
            if available <= 0 or machine.process.poll() is not None:
                break
            print('Recovering saved guest status ' + name + ' (read-only attempt ' + str(attempt + 1) + ')', flush=True)
            original_send(machine, 'cat ' + path + output + '\r')
            if original_wait(machine, marker, offset, min(available, 30)):
                return True
            if wrong_status(): return False
        available = remaining()
        while available > 0 and machine.process.poll() is None:
            if original_wait(machine, marker, offset, min(available, 5) if budget.enabled else available):
                return True
            if wrong_status(): return False
            if not budget.enabled: break
            available = remaining()
        print('Guest status unavailable: ' + name + '; operation was not repeated', flush=True)
        return failed(machine, name, marker, prefix, offset, output, 'status-unavailable')

    machine_type.send = send
    machine_type.wait = wait
