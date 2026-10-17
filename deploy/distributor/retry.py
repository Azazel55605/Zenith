#!/usr/bin/env python3
"""Narrow, installed retry helper; never builds or accepts commands/paths."""
import fcntl
import json
import os
from pathlib import Path
import re
import sys
import tempfile

STATE = Path("/home/zenith-release/.local/state/zenith/distributor")
TAG = re.compile(r"[a-z][a-z0-9-]{0,63}/v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*))?")


def queue_retry(state, tag):
    if not isinstance(tag, str) or not TAG.fullmatch(tag):
        raise ValueError("Invalid release tag.")
    with (state / "poll.lock").open("a") as lock:
        os.fchmod(lock.fileno(), 0o640)
        fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        records = json.loads((state / "releases.json").read_text())
        record = records.get(tag)
        # A building record under a free controller lock is an abandoned run.
        if not isinstance(record, dict) or record.get("status") not in ("failed", "interrupted", "building"):
            raise ValueError("Only failed or interrupted releases can be retried.")
        request = state / "retry-request.json"
        if request.exists():
            raise ValueError("A retry is already queued.")
        descriptor, temporary = tempfile.mkstemp(prefix=".retry-", dir=state)
        try:
            with os.fdopen(descriptor, "w") as output:
                os.fchmod(output.fileno(), 0o640)
                json.dump(dict(tag=tag), output)
                output.write("\n"); output.flush(); os.fsync(output.fileno())
            os.replace(temporary, request)
            directory = os.open(state, os.O_RDONLY | os.O_DIRECTORY)
            try: os.fsync(directory)
            finally: os.close(directory)
        finally:
            Path(temporary).unlink(missing_ok=True)


def main():
    try:
        if len(sys.argv) != 2: raise ValueError("Supply exactly one release tag.")
        queue_retry(STATE, sys.argv[1])
    except BlockingIOError:
        print("A distributor operation is already running.", file=sys.stderr); return 2
    except ValueError as error:
        print(str(error), file=sys.stderr); return 2
    except OSError:
        print("Retry state unavailable; check distributor permissions.", file=sys.stderr); return 1
    return 0


if __name__ == "__main__": sys.exit(main())
