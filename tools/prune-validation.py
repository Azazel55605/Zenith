#!/usr/bin/env python3
"""Preview/remove disposable validation images; retain logs, recovery data and builds."""
import argparse
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "obj"
PREFIXES = ("prior-validation", "transfer-", "progress-ui-", "auto-sync-", "kernel-", "p1-", "m3-", "m4-", "guided-", "dhcp-", "rx-poll-", "inet-pton-", "vpn-fetch-")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true", help="delete listed generated images")
    args = parser.parse_args()
    if ROOT.is_symlink(): raise RuntimeError("refuse symlinked obj directory")
    files = []
    for path in ROOT.rglob("*"):
        if path.is_symlink() or not path.is_file() or path.suffix not in {".img", ".ext2", ".raw", ".fat"}: continue
        if not path.relative_to(ROOT).parts[0].startswith(PREFIXES): continue
        if path == ROOT / "transfer-bench-reuse/checked-root.ext2": continue
        if not path.resolve().is_relative_to(ROOT.resolve()): raise RuntimeError("image outside obj")
        files.append(path)
    physical = sum(path.stat().st_blocks * 512 for path in files)
    apparent = sum(path.stat().st_size for path in files)
    for path in files:
        print(path.relative_to(ROOT))
        if args.apply: path.unlink()
    print(f"{'Removed' if args.apply else 'Would remove'} {len(files)} images; {physical / 2**30:.2f} GiB allocated, {apparent / 2**30:.2f} GiB apparent")
    print("Logs, build caches, signed packages, relay and vmware-recovery directories are retained.")


if __name__ == "__main__": main()
