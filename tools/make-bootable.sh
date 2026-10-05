#!/usr/bin/env bash
# Makes a disk prepared by Zenith's `install` command bootable, by adding what the running
# kernel cannot copy itself (it has no way to read its own boot medium yet):
#   ESP: /EFI/BOOT/BOOTX64.EFI (Limine, UEFI) and /boot/Zenith.elf (the kernel).
# The result boots with UEFI firmware only (OVMF in QEMU): BIOS boot from GPT would need a
# BIOS-boot partition, which the installer does not create.
#
# usage: tools/make-bootable.sh <disk.img> [Debug|Release]
# needs: mtools (mcopy, mmd), parted; build the kernel first.
set -euo pipefail

image=${1:?usage: tools/make-bootable.sh <disk.img> [Debug|Release]}
config=${2:-Debug}
root=$(cd "$(dirname "$0")/.." && pwd)
limine_dir="$root/obj/$config/net10.0/linux-x64/cosmos/limine"
kernel="$root/output-x64/Zenith.elf"

[[ -f "$kernel" ]] || { echo "missing $kernel: build first (dotnet publish ... -o output-x64)" >&2; exit 1; }
[[ -f "$limine_dir/BOOTX64.EFI" ]] || { echo "missing Limine files in $limine_dir: build with -c $config first" >&2; exit 1; }

# Partition 1 must be the EFI System Partition the installer created.
esp_start=$(parted -s -m "$image" unit B print | awk -F: '$1 == "1" { sub(/B$/, "", $2); print $2 }')
[[ -n "$esp_start" ]] || { echo "$image has no partition 1; run 'install <disk> --yes' inside Zenith first" >&2; exit 1; }
esp="$image@@$esp_start"

mmd -i "$esp" -D s ::/EFI ::/EFI/BOOT ::/boot 2>/dev/null || true
mcopy -i "$esp" -o "$limine_dir/BOOTX64.EFI" ::/EFI/BOOT/BOOTX64.EFI
mcopy -i "$esp" -o "$kernel" ::/boot/Zenith.elf

echo "$image is bootable (UEFI). Boot it with the 'Run QEMU x64 (installed disk)' task."
