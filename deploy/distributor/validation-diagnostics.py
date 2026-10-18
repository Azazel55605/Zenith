"""Bounded, read-only CPU observations after a native pipeline timeout.

Capture code/stack addresses, flags and the documented numeric scheduler
snapshot and validated halt return addresses, never general registers, arbitrary
memory, serial text or input.
These observations cannot pass a gate.
"""
import bisect
import hashlib
import json
from pathlib import Path
import re
import socket
import subprocess
import struct
import time

PIPELINE = re.compile(r'N_STREAM_(?:BYTES_[0-9]+|FP_BYTES)')
REGISTERS = re.compile(r'\b(RIP|RSP|RFL)=([0-9a-fA-F]{1,16})\b')


def query(monitor, command):
    def response(connection):
        data = bytearray(); deadline = time.monotonic()+.5
        while len(data) < 65536:
            remaining = deadline-time.monotonic()
            if remaining <= 0: raise TimeoutError("monitor response deadline")
            connection.settimeout(remaining)
            chunk = connection.recv(min(4096, 65536-len(data)))
            if not chunk: break
            data.extend(chunk)
            if b'(qemu)' in data: break
        return bytes(data)
    with socket.socket(socket.AF_UNIX) as connection:
        connection.settimeout(.5)
        connection.connect(str(monitor))
        response(connection)
        connection.sendall(command.encode('ascii')+b'\n')
        return response(connection).decode('ascii', errors='replace')


def registers(monitor):
    raw = query(monitor, 'info registers')
    result = {name: value.lower() for name, value in REGISTERS.findall(raw)}
    if not {'RIP', 'RSP', 'RFL'} <= result.keys():
        raise ValueError('register response incomplete')
    return result


def symbols(elf):
    result = subprocess.run(['nm', '-n', '--defined-only', str(elf)],
                            capture_output=True, text=True, timeout=3, check=True)
    rows = []
    for line in result.stdout.splitlines():
        parts = line.split(maxsplit=2)
        if len(parts) == 3 and parts[1] in ('t', 'T'):
            rows.append((int(parts[0], 16), parts[2]))
    return rows


SNAPSHOT_SLOT = '__NONGCSTATICSCosmos_Kernel_Core_Cosmos_Kernel_Core_Runtime_DebugLiveSnapshot'


def snapshot_slot(elf):
    result = subprocess.run(['nm', '-n', '--defined-only', str(elf)],
                            capture_output=True, text=True, timeout=3, check=True)
    matches = []
    for line in result.stdout.splitlines():
        parts = line.split(maxsplit=2)
        if len(parts) == 3 and parts[2] == SNAPSHOT_SLOT and parts[1] in ('b', 'B', 'd', 'D'):
            matches.append(int(parts[0],16))
    if len(matches) != 1: raise ValueError('scheduler debug pointer unavailable')
    return matches[0]


def words(monitor, address, count):
    if not 0 < count <= 131 or address % 4 or not 0xffff800000000000 <= address <= 0xffffffffffffffff-count*8:
        raise ValueError('invalid scheduler debug read bounds')
    raw = query(monitor, 'x /'+str(count)+'gx 0x'+format(address,'x'))
    values = []
    for line in raw.splitlines():
        match = re.fullmatch(r'([0-9a-fA-F]{16}):\s+((?:0x[0-9a-fA-F]{16}\s*)+)',line.strip())
        if match:
            if int(match[1],16) != address+len(values)*8: raise ValueError('debug read address mismatch')
            values.extend(int(word,16) for word in match[2].split())
    if len(values) != count: raise ValueError('incomplete scheduler debug read')
    return struct.pack('<'+'Q'*count,*values)


def scheduler_snapshot(monitor, slot):
    pointer, = struct.unpack('<Q', words(monitor,slot,1))
    for _ in range(2):
        header = words(monitor,pointer,3)
        magic, version, count, reserved, sequence = struct.unpack('<IIIIQ',header)
        if magic != 0xc05d0001 or version != 1 or count > 64 or reserved != 0:
            raise ValueError('unsupported scheduler snapshot')
        if sequence & 1: continue
        raw = words(monitor,pointer,3+count*2)
        after, = struct.unpack('<Q',words(monitor,pointer+16,1))
        observed, = struct.unpack_from('<Q',raw,16)
        if observed != sequence or after != sequence or raw[:24] != header: continue
        threads = []
        for offset in range(24,len(raw),16):
            identity,cpu,state,flags = struct.unpack_from('<IIII',raw,offset)
            if state > 5: raise ValueError('unsupported scheduler state')
            threads.append(dict(id=identity,cpu=cpu,state=state,flags=flags))
        return dict(format='CosmosDebugLiveSnapshot-v1',sequence=sequence,threads=threads)
    raise ValueError('scheduler snapshot changed during read')


def halt_caller(monitor, sample, elf, rows):
    # At the pinned one-byte HLT followed by RET, the top stack word is a return
    # address. Never read stack locals or a general-register-derived buffer. A
    # running VM can move after the sample, so retain only a code address whose
    # preceding instruction is independently verified as CALL _native_cpu_halt.
    if sample.get('symbol') != '_native_cpu_halt' or sample.get('symbol_offset') != 1:
        return None
    stack = int(sample['RSP'],16)
    if stack % 8: raise ValueError('unaligned halt call frame')
    returned, = struct.unpack('<Q',words(monitor,stack,1))
    addresses = [address for address,_ in rows]
    index = bisect.bisect_right(addresses,returned)-1
    if not 0 <= index < len(rows)-1: raise ValueError('halt return outside code')
    target = next(address for address,name in rows if name == '_native_cpu_halt')
    result = subprocess.run(['objdump','-d','--start-address='+hex(returned-5),
                             '--stop-address='+hex(returned),str(elf)],
                            capture_output=True,text=True,timeout=3,check=True)
    pattern = (r'^\s*'+format(returned-5,'x')+r':\s+e8(?:\s+[0-9a-f]{2}){4}\s+call\s+'
               +format(target,'x')+r'\s+<_native_cpu_halt>\s*$')
    if not re.search(pattern,result.stdout,re.M): raise ValueError('halt call site unverified')
    return dict(return_rip=format(returned,'016x'),symbol=rows[index][1],symbol_offset=returned-rows[index][0])


def capture(machine, label, elf=None):
    if not PIPELINE.fullmatch(label): return None
    directory = Path(machine.directory)
    report = dict(label=label, samples=[], observation_only=True)
    monitor = directory / 'monitor.sock'
    for _ in range(3):
        try: report['samples'].append(registers(monitor))
        except (OSError, ValueError):
            report['monitor_error'] = 'monitor unavailable or incomplete'
            break
        time.sleep(.05)
    if elf is not None:
        elf = Path(elf)
        try:
            digest = hashlib.sha256()
            with elf.open('rb') as stream:
                for chunk in iter(lambda: stream.read(1024*1024), b''): digest.update(chunk)
            report['elf_sha256'] = digest.hexdigest()
            rows = symbols(elf)
            addresses = [address for address, _ in rows]
            for sample in report['samples']:
                pc = int(sample['RIP'], 16)
                index = bisect.bisect_right(addresses, pc)-1
                # A final text symbol cannot bound the end of the code section.
                if 0 <= index < len(rows)-1:
                    sample['symbol'] = rows[index][1]
                    sample['symbol_offset'] = pc-rows[index][0]
            for sample in report['samples']:
                try:
                    caller = halt_caller(monitor,sample,elf,rows)
                    if caller is not None: sample['halt_caller'] = caller
                except (OSError,ValueError,StopIteration,subprocess.SubprocessError,struct.error):
                    sample['halt_caller_error'] = 'return unavailable or call site unverified'
        except (OSError, ValueError, subprocess.SubprocessError):
            report['symbol_error'] = 'ELF or symbols unavailable'
        if report['samples']:
            try: report['scheduler_snapshot'] = scheduler_snapshot(monitor,snapshot_slot(elf))
            except (OSError,ValueError,subprocess.SubprocessError,struct.error):
                report['scheduler_snapshot_error'] = 'snapshot absent, unstable or unsupported'
    destination = directory / 'pipeline-stall.json'
    destination.write_text(json.dumps(report, indent=2)+'\n')
    print('Native pipeline stall snapshot (read-only): '+str(destination), flush=True)
    print('Native pipeline stall state: '+json.dumps(report), flush=True)
    return destination
