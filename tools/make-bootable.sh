#!/usr/bin/env bash
# Compatibility entry point: installation now writes its own boot files.
# Read-only verification; never replace an installed kernel from host build output.
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
exec python3 "$root/tools/boot_image.py" "${1:?usage: tools/make-bootable.sh <disk.img>}"
