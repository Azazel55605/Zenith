# Signed kernel updates

> Technical/operating reference for kernel-only updates. Work steps: [Boot/recovery](components/BOOT-RECOVERY.md). Whole-root snapshots remain planned.

## On this page

- [Build and deliver a release](#build-and-deliver-a-release)
- [Upgrade the installed system](#upgrade-the-installed-system)
- [Failed updates and rollback](#failed-updates-and-rollback)
- [Container and validation](#container-and-validation)

The offline kernel update path is separate from P1 scripts/data. A kernel package
contains one bounded x64 ELF and signed release metadata. It never replaces the
Limine EFI loader, account configuration, installed user data or ordinary package DB.
All compiled applications are still linked into the ELF and change with that release.
This is kernel rollback on a shared root, not a filesystem snapshot or system rollback.

## Build and deliver a release

Build the new kernel on the development machine. Choose an increasing numeric release
version (two or three components). Keep the signing key on that machine, outside the
served directory. Use the existing pinned package signing key; generate one only for
initial setup. A publisher must test the release and its root/config compatibility:
a valid signature and loadable ELF headers do not prove that a kernel works.

```sh
dotnet run --project tools/Zenith.PackageTool -- keygen /path/to/private-signing-keys
# keygen prints the exact public PEM SHA256; retain it through a trusted channel.
PATH="$HOME/.dotnet/tools:$PATH" dotnet publish -c Release -r linux-x64 -o output-x64
mkdir -p obj/update-server
dotnet run --project tools/Zenith.PackageTool -- kernel-seal 1.0.0 output-x64/Zenith.elf /path/to/private-signing-keys/private.pem obj/update-server/zenith-kernel-1.0.0.zpk
dotnet run --project tools/Zenith.PackageTool -- kernel-check obj/update-server/zenith-kernel-1.0.0.zpk /path/to/private-signing-keys/public.pem
cp /path/to/private-signing-keys/public.pem obj/update-server/public.pem
python3 -m http.server 8080 --directory obj/update-server
```

The small HTTP service is a temporary LAN/QEMU fixture, not the package-server VM
or signed online repository index. With QEMU user networking the host is 10.0.2.2;
use the actual server IP on the LAN. HTTP provides no transport authentication;
verify the public-key fingerprint independently. HTTPS uses separate TLS CA trust.

## Upgrade the installed system

Use the new installation ISO for the initial system; old kernels without these
commands require reinstall/migration. Legacy unversioned/ambiguous boot layouts are
refused. Run as root on an installed ext2 system with its validated ESP mounted.
Keep a copy of the live ISO outside the installed disk.

```sh
fetch http://10.0.2.2:8080/public.pem /root/update-public.pem
pkg trust /root/update-public.pem EXACT_PUBLIC_PEM_SHA256
fetch --kernel http://10.0.2.2:8080/zenith-kernel-1.0.0.zpk /root/zenith-kernel.zpk
pkg kernel check /root/zenith-kernel.zpk
pkg kernel install /root/zenith-kernel.zpk
boot status
poweroff
```

Use an installed-root destination: `/tmp` is a small RAM filesystem. `fetch --replace
--kernel` can replace an existing regular download. Normal fetch remains limited to
8 MiB/30 seconds with a 3-second idle timeout; explicit kernel transfers allow
32 MiB + 4096 bytes/15 minutes with a 30-second idle timeout per attempt, including
HTTPS body reads. Streaming/staged delivery and two attempts remain bounded.

The updater verifies RSA-PSS/SHA256, exact signed metadata, x64 executable ELF bounds
and a versioned `zenith-x64-boot-1` contract. It checks mounted root/ESP UUIDs,
retained kernel/EFI digests, free space and staging collisions before publishing.
Versions must exceed the stored high-water version, even after a deliberate rollback.
The installer baseline is version 0.0.0. Repeated versions and reused hashes are refused.

The loaded kernel continues running. `boot status` distinguishes `running-kernel`,
`next-kernel`, `previous-kernel`, `reboot-required`, `pending-confirmation` and
`kernel-recovery-required`. Reboot from disk, log in, check your files, network and
package operations, then run:

```sh
pkg verify
boot status
boot confirm
```

Confirmation is explicit and requires the selected new kernel actually to be running,
valid retained artifacts and a resolved package recovery state. A second update is
blocked until confirmation or rollback. The previous kernel stays available;
confirmation reclaims only the explicitly retired generation (an older third kernel
or the rejected kernel after rollback).

## Failed updates and rollback

- If the new kernel fails to start, choose **Zenith previous kernel** in the actual
  Limine menu. There is no automatic attempt counter or automatic failed-boot rollback.
- From a working installed boot, `boot rollback` selects the retained previous kernel
  for the next reboot. It does not change the currently loaded kernel. Test after
  reboot and run `boot confirm`; the version high-water mark remains.
- If copying/activation was interrupted, run `boot recover` from a working boot before
  another update. The durable undo record restores the old generation/menu and removes
  the transaction's candidate files. A cancellation before activation also requires
  this recovery. Cancellation is not honored inside menu publication.
- If the normal menu is unavailable, Limine can find the retained configuration at
  `/boot/limine/limine.conf` on the ESP. If neither menu/kernel is usable, boot the
  current ISO's normal entry against the installed disk and run `boot recover`.
  `boot status` must identify the intended root/ESP before repairing. Forced RAM rescue
  remains available, but cannot run the installed-root updater without mounting/repair.

The root undo/state lives under `/var/lib/zenith/boot/`; payloads and menus are on
`/boot/efi`. Files are written/read-verified in chunks, flushed and replaced with
retained rename backups; menus follow flushed generation metadata. A stable secondary menu
covers the tested missing-primary-menu window. Both search paths select the active
generation after publication because Limine can prefer the secondary configuration.
Mount changes and normal shutdown are refused while installation/update work runs.
The validated ESP must actually be mounted; boot metadata cannot cross another mount.
Recovery does not depend on a server.
Corrupt identity/journal/retained bytes fail closed instead of guessing a different root.

An interrupted boot leaves ext2's mounted-state flag unclean even after a full flush.
Before automatic root mounting, Zenith runs its bounded read-only consistency audit
on that supported dirty state. Only a passing audit permits writable startup; no
repair or clean-bit override is performed. Damaged or error-marked roots require
external e2fsck/recovery instead of mounting for boot recover.

FAT and ext2 are not transactional filesystems. Guest termination is tested at selected
flushed boundaries; this does not promise recovery from every physical power-loss or
controller-cache failure. Keep the external ISO and backups. No snapshots, Secure Boot,
bootloader package replacement or rollback of changes made by a broken kernel is provided.
Physical hardware/USB acceptance and the previously observed KVM installed-boot failures
remain separate from TCG VM acceptance. See [PROGRESS](PROGRESS.md) for exact evidence.

## Container and validation

`.zpk` kernel files start with `ZENITH-SIGNED-KERNEL`, distinct from P1's signed
package header. The RSA-PSS signature covers the header, a 4-byte metadata length,
canonical ASCII metadata and the entire uncompressed ELF. Metadata binds name
`zenith-kernel`, release version, `x86_64`, boot ABI, size and SHA256. Limits: kernel
32 MiB, metadata 1024 bytes, RSA keys 2048..4096 bits. No executable hooks, dependency
solver, forced downgrade, arbitrary payload paths or root schema migration is supported.
Incompatible root/ABI releases need a new coordinator contract, not this package type.

`python3 -B tools/kernel-update-smoke.py obj/kernel-updates` builds disposable signed
fixtures, installs a guest-written disk, downloads real ELF-sized packages, proves
actual reboot activation, retained-kernel rollback, guest termination/undo recovery
and failed-new-kernel manual menu fallback without host boot-file repair. It also
checks signatures, P1 separation, ordinary-user refusal and independent ext2/FAT consistency.
The FAT audit records two existing Cosmos formatter quirks: an EFI boot label without
a directory label and an unknown optional free-cluster hint. Only those exact warnings
are accepted; allocation/chain/directory errors fail. No audit repairs the guest image.
Fixture release markers change the original ELF hash without changing its loaded code;
a separate signed fixture deliberately loops at the entry point to exercise fallback.
Opt-in `kernel-test-stop=staged|menu-gap` boot flags pause the worker at precise crash
checkpoints; they are never enabled during normal updates.

Kernel installation automatically flushes all mounted filesystems before successful
return. Zenith's `reboot`, `poweroff` and desktop power actions perform checked sync,
unmount and device-cache flush before the hardware action. A separate `sync` is
not required. A reported sync/unmount/flush failure prevents the hardware action;
inspect the error rather than assuming shutdown succeeded. These checks do not
establish arbitrary power-loss safety or explain a previous corruption incident.
