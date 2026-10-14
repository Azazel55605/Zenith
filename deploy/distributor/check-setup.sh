#!/usr/bin/env bash
# Read-only inventory. Run as zenith-release; never print private key contents.
set -u
export PATH="$HOME/.dotnet/tools:$PATH:/usr/local/sbin:/usr/sbin:/sbin"
printf '\nSystem\n'
cat /etc/os-release
uname -m
printf '\nResources and publication storage\n'
nproc
free -h
df -h / /srv/zenith 2>/dev/null
printf '\nNative-test acceleration (current user)\n'
id
if [[ -e /dev/kvm ]]; then
    ls -l /dev/kvm
    if [[ -r /dev/kvm && -w /dev/kvm ]]; then
        printf 'KVM device accessible; verify nested acceleration with the documented QEMU probe.\n'
    else
        printf 'KVM device inaccessible; native tests fall back to software emulation.\n'
    fi
else
    printf 'No /dev/kvm; native tests use software emulation.\n'
fi
printf '\nBuild tools\n'
for tool in dotnet git python3 nginx gcc clang ld nm e2fsck debugfs qemu-system-x86_64 cosmos cosmos.patcher; do
    command -v "$tool" || printf 'MISSING: %s\n' "$tool"
done
if command -v dotnet >/dev/null; then
    dotnet --list-sdks
    dotnet tool list -g
fi
printf '\nSource checkout\n'
if [[ -d "$HOME/Zenith/.git" ]]; then
    git -C "$HOME/Zenith" rev-parse HEAD
    git -C "$HOME/Zenith" status --short
    [[ ! -f "$HOME/Zenith/cosmos.local.props" ]] || printf 'Local Cosmos override present\n'
else
    printf 'MISSING: %s/Zenith checkout\n' "$HOME"
fi
printf '\nSigning file permissions (no private contents)\n'
for path in "$HOME/signing" "$HOME/signing/private.pem" "$HOME/signing/public.pem" /srv/zenith /srv/zenith/packages; do
    stat -c '%A %U:%G %n' "$path" 2>/dev/null || printf 'MISSING: %s\n' "$path"
done
[[ ! -f "$HOME/signing/public.pem" ]] || sha256sum "$HOME/signing/public.pem"
printf '\nWeb service and listening sockets\n'
systemctl is-enabled nginx 2>&1
systemctl is-active nginx 2>&1
ss -ltn
printf '\nEnabled nginx sites\n'
ls -l /etc/nginx/sites-enabled 2>/dev/null
printf '\nPublished release receipts\n'
if [[ -d /srv/zenith/packages ]]; then
    find /srv/zenith/packages -maxdepth 4 -name release.json -type f -print
fi
printf '\nBackup policy and restore test require operator confirmation. No changes made.\n'
