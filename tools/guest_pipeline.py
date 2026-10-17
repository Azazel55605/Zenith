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
