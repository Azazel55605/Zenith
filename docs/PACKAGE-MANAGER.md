# Package architecture and remaining design

Reviewed **2026-10-15**. Signed installed updates are implemented, including online
named packages and kernel staging. This is the design overview; use the
[component checklist](components/PACKAGES.md) for work steps,
[online guide](ONLINE-PACKAGES.md) for commands and [format guide](PACKAGES.md) for
container/transaction contracts.

## Package classes and activation

| Class | Current use | Activation / boundary |
|---|---|---|
| Scripts/data | Docs, manuals, fonts, branding, theme | Replace owned files; reload consumer or login as documented |
| Native executable | Signed Sunbeam ELF, with runtime capability dependencies | New invocation loads verified replacement; ABI must be supplied by kernel |
| Metapackage | `zenith-style`, dependency-only manifest 3 | Installs required packages; removal retains dependencies |
| Kernel | Dedicated signed kernel container | Versioned ESP artifacts; reboot and explicit confirmation |

Most builtins, GUI and services remain linked into the kernel. They cannot yet be
replaced with independent packages. Their extraction depends on
[native runtime APIs](USERLAND.md), not merely splitting source directories.

## Delivery and trust

The server builds immutable package-specific tags such as `kernel/v0.0.13` and
`sunbeam-tools/v0.2.0`. Each package has its own version sequence. Declarative
recipes select repository (`core`/`extra`), inputs and dependencies. Validation
must succeed before signing/publication. Archives and receipts are immutable;
signed repository snapshots provide discovery and freshness/generation checks.

Guest transport trust and package trust are separate. HTTPS validates the imported
CA/certificate, hostname and dates; a pinned signing public key authenticates
catalogs/packages. Expired/replayed/tampered metadata must fail before installation.
An HTTPS fetch alone does not authorize an install. See
[server runbook](PACKAGE-SERVER.md) and [online protocol](ONLINE-PACKAGES.md).

## Planning and ownership

The planner resolves installed/available versions, dependency ranges, conflicts
and file ownership before applying files. It exposes a reviewable plan and asks
for confirmation unless explicitly requested otherwise. Local edits and protected
system/account/boot paths are not silently overwritten by data packages.
Signed native executables are verified again on launch.

Local `/etc` and `/home` data remain administrator/user state. Configuration merges,
maintainer hooks and arbitrary forced overwrites are not implemented features.
Manifest 3 metas and extended catalogs preserve explicit compatibility rules; an
old kernel may not manage a newer installed package database after boot rollback.

## Transactions and updates

Installed data updates use bounded ext2 staging/backups, ownership/database commit
and undo/recovery. Kernel updates use the dedicated generation coordinator and
boot undo state. Shared maintenance gates prevent conflicting mutations and unsafe
power actions during active work.

`pkg upgrade` upgrades installed data packages. `pkg update` refreshes/plans
installed data plus a newer kernel. Their commit boundaries remain separate; this
is not an atomic system snapshot. The currently loaded kernel remains active
until reboot. New native bundles can require capabilities absent from the running
kernel: upgrade/reboot/confirm the compatible kernel before installing those bundles.

## Remaining design and implementation

| Work | Why it matters | Required acceptance |
|---|---|---|
| Broader independent tools | Kernel-linked commands still require kernel releases | New ABI + same-kernel executable upgrade/fault tests |
| Configuration merge policy | Preserve local edits while changing shipped defaults | Conflict review, cancellation and recoverable partial failures |
| Restart policy and compatibility | Updated active services/apps can keep old code/state | Explicit activation boundary and incompatible dependency rejection |
| Public trust lifecycle | Current private deployment does not cover rotation/revocation/multiple roles | Compromised/expired/rotated key and metadata rollback tests |
| Coordinated recovery | Old kernel and new root/package DB may disagree | Matched generation/trial/restore and interrupted activation tests |
| Larger installed roots | Current ext2 profile limits package growth | Coordinated checker/formatter/installer/memory redesign |

Build automation, named installs, all-installed updates, dependency/conflict
handling and automatic build-file cleanup are already implemented. Do not reopen
them as unimplemented proposals. Detailed original rationale and alternatives
remain in the [archived design discussion](archive/2026-10-15/PACKAGE-MANAGER.md).
