"""Check foreground pipeline completion and exact public fixture output separately."""
import json
import re

NOISE = re.compile(r"\[(?!zenith\]|console\])[A-Za-z][A-Za-z0-9 ]*\][^\n]*\n")


def check(machine, command, expected, label, timeout=120):
    if not re.fullmatch(r'N_[A-Z0-9_]+', label):
        raise ValueError('pipeline check requires a unique fixture status label')
    offset = len(machine.log())
    machine.send(command + '; echo ' + label + '=$? | logger\r')
    completed = machine.wait('user: ' + label + '=0', offset, timeout=timeout)
    # Completion follows output. A separate short drain accommodates fragmented
    # UART delivery; neither an expected substring nor a prompt proves success.
    if completed:
        machine.wait('user: ' + expected, offset, timeout=2)
    raw = NOISE.sub('', machine.log()[offset:])
    output = re.findall(r'(?:^|[\r\n])(?:\[zenith\] \[[^\]\r\n]*\] )?user: ([^\r\n]*)', raw)
    if completed and expected in output:
        print('PASS ' + label + ': exact output ' + expected + '; pipeline exited 0', flush=True)
        return
    # Only numeric fixture results/statuses are copied into the build log. Raw
    # serial transcripts remain in the retained artifact, never dumped here.
    report = dict(label=label, expected_output=expected, completed=completed,
                  observed_counts=[line for line in output if re.fullmatch(r'[0-9]+', line)],
                  observed_statuses=[line for line in output if line.startswith(label + '=')],
                  qemu_exit=machine.process.poll(), serial_bytes=len(machine.log()[offset:].encode()),
                  serial_path=str(machine.serial))
    print('Pipeline check failed: ' + json.dumps(report), flush=True)
    raise RuntimeError(label + ': ' + ('wrong/missing output' if completed else 'completion failed or timed out'))

# Only known Cosmos debug writers may interrupt a public fixture line. Preserve
# arbitrary bracketed application output, errors and command echoes.
TRACE_TAG = r'\[(?:SCHED|STRIDE|LAPIC|CV|LowLevelMonitor|Kernel|PageAllocator|SmallHeap)\]'
TRACE = re.compile(TRACE_TAG + r'[^\n]*\n')


def check_stderr(text, expected_error, label):
    """Require the real error line, empty downstream stdout and final status 0."""
    if not re.fullmatch(r'N_[A-Z0-9_]+', label):
        raise ValueError('stderr check requires a unique fixture status label')
    cleaned = TRACE.sub('', text)
    output = re.findall(r'(?:^|[\r\n])(?:\[zenith\] \[[^\]\r\n]*\] )?user: ([^\r\n]*)', cleaned)
    # A preempted trace record can also be left unterminated with the exact
    # diagnostic completing that physical line; its remainder resumes later.
    resumed = re.search(r'(?m)^' + TRACE_TAG + r'[^\n]*?' + re.escape(expected_error) + r'\r?$', text)
    if (expected_error not in cleaned.splitlines() and not resumed) or '0' not in output or label + '=0' not in output:
        raise RuntimeError(label + ': stderr line, zero downstream bytes and successful completion are all required')
