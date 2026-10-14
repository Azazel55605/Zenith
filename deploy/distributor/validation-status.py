"""Recover acceptance exit statuses without repeating the operation being tested."""
import re
import secrets
import time

STATUS = re.compile(r'(.+); echo ([A-Z][A-Za-z0-9_-]{0,63})=\$\? \| logger\r', re.DOTALL)


NOISE = re.compile(r"\[(?!zenith\])[A-Za-z][A-Za-z0-9 ]*\][^\n]*\n")
PROMPT = re.compile(r'(?:^|\r?\n)\[console\] [^\r\n]*[$#] ')


def busy(machine, path, offset):
    # Only postpone readback when the exact submitted suffix is visible and no
    # subsequent completed shell prompt exists. Damaged/unknown echoes retain
    # the old recovery path. Never treat a prompt as successful exit status.
    raw = NOISE.sub('', machine.log()[offset:])
    submitted = re.search(re.escape(path) + r' \| logger(?:\r\n|\n)', raw)
    return submitted is not None and PROMPT.search(raw, submitted.end()-1) is None


def install(machine_type):
    original_send, original_wait = machine_type.send, machine_type.wait

    def send(machine, text):
        # Adapt only the harness's exact status suffix, not interactive input or
        # arbitrary shell commands. Save $? before logger/filesystem work changes it.
        match = STATUS.fullmatch(text)
        machine._validation_status = None
        if match:
            operation, name = match.groups()
            path = '/tmp/validation-status-' + secrets.token_hex(16)
            offset = len(machine.log())
            machine._validation_status = (name, path, offset)
            text = (operation + "; printf '%s\\n' " + name + '=$? > ' + path +
                    '; cat ' + path + ' | logger\r')
        return original_send(machine, text)

    def wait(machine, marker, offset=0, timeout=180):
        record = getattr(machine, '_validation_status', None)
        prefix = 'user: ' + record[0] + '=' if record else ''
        status_marker = marker == prefix or (bool(prefix) and marker.startswith(prefix) and marker[len(prefix):].isdigit())
        if not record or not status_marker or offset > record[2]:
            return original_wait(machine, marker, offset, timeout)
        deadline = time.monotonic() + timeout
        if original_wait(machine, marker, offset, min(timeout, 30)):
            return True
        name, path, _ = record
        if busy(machine, path, offset):
            print('Guest operation '+name+' is still running; postponing status readback', flush=True)
            while busy(machine, path, offset):
                remaining = deadline - time.monotonic()
                if remaining <= 0 or machine.process.poll() is not None:
                    print('Guest operation deadline reached: '+name+'; operation was not repeated', flush=True)
                    return False
                if original_wait(machine, marker, offset, min(remaining, 5)):
                    return True
        # Replay only the saved observation. An absent file, dead/hung guest or
        # unreadable response still fails; the unchanged harness checks its value.
        for attempt in range(3):
            remaining = deadline - time.monotonic()
            if remaining <= 0 or machine.process.poll() is not None:
                break
            print('Recovering saved guest status ' + name + ' (read-only attempt ' + str(attempt + 1) + ')', flush=True)
            original_send(machine, 'cat ' + path + ' | logger\r')
            if original_wait(machine, marker, offset, min(remaining, 30)):
                return True
        remaining = deadline - time.monotonic()
        if remaining > 0 and machine.process.poll() is None:
            if original_wait(machine, marker, offset, remaining):
                return True
        print('Guest status unavailable: ' + name + '; operation was not repeated', flush=True)
        return False

    machine_type.send = send
    machine_type.wait = wait
