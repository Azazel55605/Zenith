"""Shared authentication waits for direct and immutable-tag guest harnesses.

A secret prompt and a PBKDF2/account-save result are different phases. Never
resubmit credentials, infer authentication from CPU/log activity, or print input.
"""
import re
import time

# Bump when policy behavior changes so newer controllers can wrap older helpers.
REVISION = 2
PROMPT_SECONDS = 60
COMPLETION_SECONDS = 180
NOISE = re.compile(r'\[(?!zenith\])[A-Za-z][A-Za-z0-9 ]*\][^\n]*\n')
PROMPTS = {
    'Password: ': 'password-prompt', 'Current password: ': 'password-current',
    'New password: ': 'password-new', 'Repeat password: ': 'password-confirmation',
    'User password: ': 'password-new', 'Repeat user password: ': 'password-confirmation',
    'Root password: ': 'password-new', 'Repeat root password: ': 'password-confirmation',
}
RESULT = re.compile(r'(password updated for|authenticated as) [a-z_][a-z0-9_-]{0,31}')
FAILURES = (
    (r'(?:(?:login|su|passwd): |session: )authentication failed', 'credential-rejected'),
    (r'passwd: passwords do not match; unchanged', 'confirmation-mismatch'),
    (r'passwd: secure entropy unavailable; password unchanged', 'entropy-unavailable'),
    (r'\[INT\] FATAL:|\[zenith\][^\r\n]*panic:', 'guest-fatal'),
)


def phase(marker):
    if marker in PROMPTS: return PROMPTS[marker], PROMPT_SECONDS
    match = RESULT.fullmatch(marker)
    if match: return ('password-update' if match[1] == 'password updated for' else 'authentication'), COMPLETION_SECONDS
    if marker in ('desktop ready (installed)', 'headless ready (local text console)'):
        return 'session-authentication', COMPLETION_SECONDS
    return None


def install(machine_type):
    # New harnesses install this themselves; the controller also installs its
    # current copy for older tags. Status/network wrappers must not duplicate it.
    if getattr(machine_type, '_zenith_auth_revision', None) == REVISION: return
    original_wait = machine_type.wait

    def wait(machine, marker, offset=0, timeout=180):
        machine._validation_auth = None
        selected = phase(marker)
        if selected is None: return original_wait(machine, marker, offset, timeout)
        kind, minimum = selected
        effective = max(timeout, minimum)
        started = time.monotonic(); deadline = started + effective; report = started + 30
        record = dict(phase=kind, requested_seconds=timeout, effective_seconds=effective, outcome='waiting')
        machine._validation_auth = record
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                reason = 'deadline'; break
            try:
                found = original_wait(machine, marker, offset, min(remaining, 5))
            except Exception:
                record.update(outcome='error', reason='wait-error')
                raise
            # Inspect only this interaction. Do not expose raw output or usernames
            # in public diagnostics, and never use a prompt as a successful result.
            source = machine.log()[offset:]
            raw = NOISE.sub('', source)
            raw = re.sub(r'\x1b\[[0-9;]*[A-Za-z]', '', raw)
            reason = next((reason for pattern, reason in FAILURES
                           if re.search(pattern, raw) or re.search(pattern, source)), None)
            if reason: break
            if found and RESULT.fullmatch(marker):
                # A username prefix or partial UART line cannot prove identity.
                # A debug writer may begin its line before the complete result.
                # Filtering that line would erase real completion. Accept the
                # exact terminated result from either view, never a username
                # prefix, an incomplete line or a prompt alone.
                complete = re.escape(marker) + r'[\r\n]'
                found = re.search(complete, raw) is not None or re.search(complete, source) is not None
                if not found: time.sleep(.05)
            if found:
                record['outcome'] = 'passed'; return True
            if machine.process.poll() is not None:
                reason = 'qemu-exited'; break
            now = time.monotonic()
            if now >= report:
                report = now + 30
                print('[authentication] '+kind+' still waiting; elapsed='+str(round(now-started, 1))+
                      's; limit='+str(effective)+'s', flush=True)
        record.update(outcome='failed', reason=reason)
        print('[authentication] '+kind+' failed; reason='+reason+'; elapsed='+str(round(time.monotonic()-started, 1))+
              's; limit='+str(effective)+'s; credentials were not repeated', flush=True)
        return False

    machine_type.wait = wait
    machine_type._zenith_auth_revision = REVISION
