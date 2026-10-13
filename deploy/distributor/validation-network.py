"""Verify guest DHCP state when concurrent Cosmos output obscures its boot message."""
import re
import secrets
import time

ONLINE = re.compile(r'network: online address=((?:[0-9]{1,3}\.){3}[0-9]{1,3})')

def wait(machine, marker, offset, timeout, original):
    match = ONLINE.fullmatch(marker)
    if not match or offset != 0 or any(int(part) > 255 for part in match[1].split('.')):
        return original(machine, marker, offset, timeout)
    started = time.monotonic()
    if original(machine, marker, offset, min(timeout, 3)): return True
    if machine.process.poll() is not None: return False
    # An online transition already logged but missing its exact address can be
    # diagnosed promptly. Otherwise retain the full original startup deadline.
    if 'network: online address=' not in machine.log():
        remaining = max(0, timeout - (time.monotonic() - started))
        if original(machine, marker, offset, remaining): return True
        if machine.process.poll() is not None: return False
    address = match[1]
    # No DHCP retry or static fallback. Read actual state from the same live guest.
    # Match a unique logger response, never the terminal's echoed command text.
    token = 'DHCP_PROBE_' + secrets.token_hex(8)
    command = ("if ip | grep 'mode=dhcp state=online' > /dev/null && "
               "ip | grep '  address=" + address + " ' > /dev/null; "
               "then logger " + token + "_OK; else logger " + token + "_FAILED; fi")
    start = len(machine.log())
    try:
        machine.send(command + '\r')
        valid = original(machine, 'user: ' + token + '_OK', start, 30)
    except (OSError, RuntimeError) as error:
        print('Guest DHCP state probe failed: ' + str(error), flush=True); return False
    print('Guest DHCP state probe ' + ('verified address=' + address if valid else 'failed; expected address=' + address), flush=True)
    return valid


def install(machine_type, instances):
    initialize, original_wait = machine_type.__init__, machine_type.wait
    def initialize_tracked(self, *args, **kwargs):
        initialize(self, *args, **kwargs); instances.append(self)
    def checked_wait(self, marker, offset=0, timeout=180):
        return wait(self, marker, offset, timeout, original_wait)
    machine_type.__init__ = initialize_tracked
    machine_type.wait = checked_wait
