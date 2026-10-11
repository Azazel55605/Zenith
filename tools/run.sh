#!/usr/bin/env bash
# Boots Zenith in QEMU.
#
#   tools/run.sh live        boot the ISO (live system, RAM root) with disk.img attached as a target
#   tools/run.sh installed   boot disk.img itself with UEFI firmware (after `install` + make-bootable.sh)
#
# Environment: DISK (default disk.img, created at 512 MiB if missing), MEM (default 512M),
#              OVMF_CODE / OVMF_VARS (UEFI firmware, defaults for Arch's edk2-ovmf),
#              NETWORK=user|tap|off (user default), TAP_IFACE (preconfigured tap),
#              UI=desktop|headless (desktop default), QEMU (executable override).
set -euo pipefail

mode=${1:-live}
[[ $# -le 1 && "$mode" =~ ^(live|installed)$ ]] || { echo "usage: tools/run.sh [live|installed]" >&2; exit 2; }
network=${NETWORK:-user}
ui=${UI:-desktop}
netargs=()
case "$network" in
    user) netargs=(-netdev user,id=net0 -device e1000e,netdev=net0,id=nic0) ;;
    tap)
        [[ ${TAP_IFACE:-} =~ ^[a-zA-Z0-9_.-]{1,15}$ ]] || { echo "NETWORK=tap requires TAP_IFACE naming a preconfigured TAP interface" >&2; exit 2; }
        netargs=(-netdev "tap,id=net0,ifname=$TAP_IFACE,script=no,downscript=no" -device e1000e,netdev=net0,id=nic0) ;;
    off) ;;
    *) echo "NETWORK must be user, tap or off" >&2; exit 2 ;;
esac
case "$ui" in
    desktop) uiargs=(-vga none -device virtio-gpu-pci -device virtio-keyboard-pci -device virtio-mouse-pci -display gtk,grab-on-hover=on,zoom-to-fit=off) ;;
    headless) uiargs=(-vga none -display none) ;;
    *) echo "UI must be desktop or headless" >&2; exit 2 ;;
esac
root=$(cd "$(dirname "$0")/.." && pwd)
disk=${DISK:-$root/disk.img}
ovmf_code=${OVMF_CODE:-/usr/share/edk2/x64/OVMF_CODE.4m.fd}
ovmf_vars=${OVMF_VARS:-/usr/share/edk2/x64/OVMF_VARS.4m.fd}

[[ -f "$disk" ]] || qemu-img create -f raw "$disk" 512M >/dev/null

args=(
    -M q35 -m "${MEM:-512M}"
    -accel kvm -accel tcg -cpu max     # KVM when available (much faster), TCG otherwise
    -serial stdio -no-reboot -no-shutdown
    -monitor none -nic none
    "${netargs[@]}" "${uiargs[@]}"
    # Paravirtual devices Cosmos has drivers for: the virtio-gpu canvas replaces the GOP
    # framebuffer, and virtio input delivers whole motion events instead of PS/2 bytes.
    # The mouse is relative-only (Cosmos has no tablet driver yet): grab the pointer as soon as
    # it enters the window so host and guest motion stay in step. Ctrl+Alt+G releases it.
)

case "$mode" in
    live)
        exec "${QEMU:-qemu-system-x86_64}" "${args[@]}" \
            -cdrom "$root/output-x64/Zenith.iso" -boot order=d \
            -drive "file=$disk,format=raw,if=none,id=disk0" -device ide-hd,drive=disk0
        ;;
    installed)
        vars=$root/obj/OVMF_VARS.fd
        [[ -f "$vars" ]] || cp "$ovmf_vars" "$vars"
        exec "${QEMU:-qemu-system-x86_64}" "${args[@]}" \
            -drive "if=pflash,format=raw,readonly=on,file=$ovmf_code" \
            -drive "if=pflash,format=raw,file=$vars" \
            -drive "file=$disk,format=raw,if=none,id=disk0" -device ide-hd,drive=disk0
        ;;
    *)
        echo "usage: tools/run.sh [live|installed]" >&2
        exit 2
        ;;
esac
