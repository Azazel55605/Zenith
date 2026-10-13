"""Bounded, dashboard-readable phase snapshots; no private-directory access needed."""
import json
import os
from pathlib import Path
import tempfile
import time

LIMIT = 16384

def tail(path):
    with path.open('rb') as stream:
        stream.seek(0, 2); stream.seek(max(0, stream.tell() - LIMIT))
        return stream.read(LIMIT).decode('utf-8', errors='replace')

class Reporter:
    def __init__(self, path):
        self.path = Path(path); self.rows = []; self.last = 0

    def start(self, label):
        self.rows.append(dict(name=label, status='running', started=time.time(), elapsed=0, updated=None, text='', diagnostic=''))
        self.rows = self.rows[-32:]

    def update(self, log, command, elapsed, status='running', force=False):
        if not force and time.monotonic() - self.last < 2: return
        self.last = time.monotonic()
        try:
            row = self.rows[-1]; row.update(status=status, elapsed=round(elapsed, 1))
            text = tail(log)
            if text != row['text']: row.update(text=text, updated=time.time())
            if row['name'].startswith('Native ') and command:
                keep = Path(command[-1])
                # Only QEMU stderr; never publish guest serial/password input.
                logs = [p for p in keep.glob('*/qemu.log') if p.is_file() and not p.is_symlink()]
                if logs:
                    latest = max(logs, key=lambda p: p.stat().st_mtime)
                    row['diagnostic'] = latest.parent.name + ':\n' + tail(latest)
            descriptor, temporary = tempfile.mkstemp(prefix='.build-output-', dir=self.path.parent)
            try:
                with os.fdopen(descriptor, 'w') as output:
                    os.fchmod(output.fileno(), 0o640)
                    json.dump(dict(phases=self.rows, refreshed=time.time()), output)
                os.replace(temporary, self.path)
            finally:
                if os.path.exists(temporary): os.unlink(temporary)
        except (OSError, ValueError):
            # Observation must not change release acceptance or mask its error.
            pass

reporter = None

def start(label):
    global reporter
    destination = os.environ.get('ZENITH_BUILD_OUTPUT')
    if not destination: return None
    if reporter is None: reporter = Reporter(destination)
    reporter.start(label)
    return reporter
