"""Bounded cleanup of known distributor build evidence, under the poll/build locks."""
import fcntl
import json
import os
from pathlib import Path
import re
import shutil
import stat
import tempfile
import time

TAG = re.compile(r'[a-z][a-z0-9-]{0,63}/v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*))?')
ATTEMPTS = 2
MAX_AGE = 7 * 86400
LOG_BYTES = 1024 * 1024
SCRATCH = Path('/var/tmp')

def register_native(directory, details):
    if not details: return
    state = Path(details).parent
    build = Path(details).name.removesuffix('.details.json')
    prefix, separator, version = build.rpartition('-v')
    tag = prefix + '/v' + version
    if not separator or not TAG.fullmatch(tag): raise ValueError('invalid native workspace identity')
    directory = Path(directory)
    if directory.parent.resolve() != SCRATCH.resolve() or directory.is_symlink():
        raise ValueError('native workspace outside scratch root')
    owner = dict(path=str(directory), state=str(state.resolve()), tag=tag, created=time.time())
    save(directory / '.zenith-owner.json', owner)
    save(state / ('native-workspace-' + directory.name + '.json'), owner)

def cleanup_native(state, records, now):
    removed = []; warnings = []
    for record in state.glob('native-workspace-zn-*.json'):
        try:
            if record.is_symlink(): raise ValueError('linked native registration')
            owner = json.loads(record.read_text()); path = Path(owner['path']); tag = owner['tag']
            if owner['state'] != str(state.resolve()) or not TAG.fullmatch(tag) or tag not in records:
                raise ValueError('unknown native workspace owner')
            if path.parent.resolve() != SCRATCH.resolve() or not re.fullmatch(r'zn-[a-zA-Z0-9_-]+', path.name):
                raise ValueError('unsafe native workspace path')
            if path.is_symlink(): raise ValueError('linked native workspace')
            if not path.exists(): record.unlink(); continue
            marker = path / '.zenith-owner.json'
            if marker.is_symlink() or path.stat().st_uid != os.getuid() or json.loads(marker.read_text()) != owner:
                raise ValueError('native ownership marker mismatch')
            if records[tag].get('status') in ('complete', 'failed', 'interrupted') and now - owner['created'] > MAX_AGE:
                shutil.rmtree(path); record.unlink(); removed.append(path.name)
        except (OSError, ValueError, KeyError, TypeError) as error: warnings.append(record.name + ': ' + str(error))
    return removed, warnings

def save(path, value):
    descriptor, temporary = tempfile.mkstemp(prefix='.storage-', dir=path.parent)
    try:
        with os.fdopen(descriptor, 'w') as output:
            os.fchmod(output.fileno(), 0o640); json.dump(value, output)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary): os.unlink(temporary)

def allocated(path):
    total = 0; scanned = 0; pending = [path]; partial = False
    while pending:
        directory = pending.pop()
        try:
            with os.scandir(directory) as entries:
                for entry in entries:
                    if entry.is_symlink(): continue
                    info = entry.stat(follow_symlinks=False); total += info.st_blocks * 512; scanned += 1
                    if stat.S_ISDIR(info.st_mode): pending.append(Path(entry.path))
                    if scanned >= 200000: partial = True; return total, partial
        except OSError: partial = True
    return total, partial

def real_child(path, root):
    return not path.is_symlink() and path.resolve().is_relative_to(root.resolve()) and path.resolve() != root.resolve()

def cleanup(state, records, now):
    removed = []; warnings = []
    failures = [(tag, row) for tag, row in records.items() if TAG.fullmatch(tag) and row.get('status') in ('failed', 'interrupted')]
    def finished(row):
        value = row.get('finished')
        return value if isinstance(value, (int, float)) and value > 0 else now
    latest_failure = max(failures, key=lambda item: finished(item[1]), default=(None, None))[0]
    for tag, row in records.items():
        if not TAG.fullmatch(tag) or row.get('status') not in ('complete', 'failed', 'interrupted'): continue
        checkout = state / ('build-' + tag.replace('/', '-'))
        try:
            if checkout.exists():
                if not real_child(checkout, state): raise ValueError('unsafe checkout path')
                expired = now - finished(row) > MAX_AGE
                if row['status'] == 'complete' or (expired and tag != latest_failure):
                    shutil.rmtree(checkout); removed.append(checkout.name)
                else:
                    parent = checkout / 'obj/distributor'
                    if parent.exists():
                        if not real_child(parent, checkout): raise ValueError('unsafe attempt directory')
                        attempts = sorted((p for p in parent.glob('release-*') if p.is_dir()), key=lambda p: p.stat().st_mtime, reverse=True)
                        for path in attempts[ATTEMPTS:]:
                            if not real_child(path, checkout): raise ValueError('unsafe attempt path')
                            shutil.rmtree(path); removed.append(str(path.relative_to(state)))
            # Append-only per-tag history keeps a bounded tail; durable pins stay.
            log = state / (tag.replace('/', '-') + '.log')
            if log.exists():
                if not real_child(log, state): raise ValueError('unsafe log path')
                if log.stat().st_size > LOG_BYTES:
                    with log.open('rb') as source: source.seek(-LOG_BYTES, 2); tail = source.read(LOG_BYTES)
                    descriptor, temporary = tempfile.mkstemp(prefix='.log-tail-', dir=state)
                    try:
                        with os.fdopen(descriptor, 'wb') as output:
                            os.fchmod(output.fileno(), 0o640); output.write(tail)
                        os.replace(temporary, log)
                    finally:
                        if os.path.exists(temporary): os.unlink(temporary)
                    removed.append(log.name + ' (older output)')
        except (OSError, ValueError, TypeError) as error: warnings.append(tag + ': ' + str(error))
    return removed, warnings

def run(state, records, config, apply=True):
    state = Path(state)
    if state.is_symlink(): raise ValueError('symlink state directory refused')
    root = Path(config['root'])
    with (root.parent / '.distributor-build.lock').open('a') as lock:
        try: fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            return {'warnings': ['Storage cleanup deferred: build/publication lock busy']}
        removed, warnings = cleanup(state, records, time.time()) if apply else ([], [])
        if apply:
            native_removed, native_warnings = cleanup_native(state, records, time.time())
            removed.extend(native_removed); warnings.extend(native_warnings)
        physical, partial = allocated(state)
        disk = shutil.disk_usage(state)
        report = dict(refreshed=time.time(), total=disk.total, used=disk.used, free=disk.free,
                      allocated=physical, partial=partial, removed=removed, warnings=warnings,
                      policy=dict(attempts=ATTEMPTS, inactive_days=7, log_bytes=LOG_BYTES, preserve_latest_failure=True))
        save(state / 'storage.json', report)
        return report
