"""Read complete compositor records; never interpret a UART prefix as a result."""
import re
import time

PREFIX=r'\[zenith\] \[[ 0-9.]+\] '
BENCHMARK=re.compile(PREFIX+r'compose: parallel bench verify=(\d+) scalar-us=(\d+) parallel-us=(\d+) ap-tiles=(\d+) materials=(\d+)(?=[\r\n])')
FILTER=re.compile(PREFIX+r'user: material blur verified; cpus=(\d+) scalar-us=(\d+) parallel-us=(\d+) ap-tiles=(\d+) hash=([0-9a-f]+)(?=[\r\n])')

def wait_record(machine,pattern,offset,timeout=60):
    deadline=time.monotonic()+timeout
    while True:
        match=pattern.search(machine.log()[offset:])
        if match:return match
        if time.monotonic()>=deadline or machine.process.poll() is not None:
            raise RuntimeError('complete compositor record unavailable; retained serial: '+str(machine.serial))
        time.sleep(.01)
