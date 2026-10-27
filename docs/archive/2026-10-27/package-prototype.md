# P0 offline package prototype

> Historical unsigned P0 experiment; not the supported installation path. Use [Signed packages](../../PACKAGES.md) and [Online packages](../../ONLINE-PACKAGES.md).


Experimental implementation, 2026-10-11. The shared host/guest reader and planner
establish bounded input, integrity, compatibility and conflict checks before an
installer is built. These unsigned directory commands do not install or activate packages.
Signed offline containers, ownership DB and transactions are now a separate
[P1 implementation](../../PACKAGES.md). Online trust and system activation remain gates
in [PACKAGE-MANAGER](../../PACKAGES.md#architecture-and-activation). Networking is not needed for these tests.

## Build and inspect

The current host tool requires Linux (.NET 10); it uses `statx` to reject symlinks and
special nodes before opening them, including FIFOs. Put regular files in a source
tree matching their eventual destination, then run:

```sh
mkdir -p /tmp/pkg-source/usr/share/doc
printf 'hello\n' > /tmp/pkg-source/usr/share/doc/example.txt
dotnet run --project tools/Zenith.PackageTool -- build hello-docs 1.0.0 /tmp/pkg-source /tmp/pkg-bundle
dotnet run --project tools/Zenith.PackageTool -- inspect /tmp/pkg-bundle/manifest.tsv
mkdir -p /tmp/pkg-test-root
dotnet run --project tools/Zenith.PackageTool -- plan /tmp/pkg-bundle/manifest.tsv /tmp/pkg-test-root
```

Output is a `manifest.tsv` plus a `payload/` directory, built through a temporary
directory and published only when validation completes. Existing output is never
overwritten; output inside the source is refused. File order and manifest bytes
are deterministic. Payload files are represented as root-owned regular files with
0644 mode by the builder; the reader also accepts 0755. Empty directories, links,
devices, ownership overrides, compression and hooks have no representation.

Guest commands read a copied bundle from a directory on an accessible filesystem:

```sh
pkg list
pkg inspect /path/to/bundle/manifest.tsv
pkg plan /path/to/bundle/manifest.tsv
```

`pkg list` labels the current kernel/base/test-DE as bundled image components, all
revision 0.1, with kernel linkage and reboot activation. This is a bootstrap inventory,
not an installed package DB. A DLL dropped into `/bin` is not runnable userland.
`pkg plan` verifies payload bytes and prints creates/blockers without modifying the
root. A blocked plan exits 1; inspect validates metadata without verifying payload.
Hashes detect corruption, **not authenticity**. No signature/trust claims apply.

## Experimental manifest contract

Records are printable ASCII, TAB-separated with LF endings. First record is
`ZENITH-PACKAGE<TAB>1`; required unique fields are `name`, `version`, `architecture`,
`kind`, `runtime`. Only `kind=data` is accepted. Runtime identity is `bootstrap-1`,
a prototype reader/environment compatibility marker rather than a userspace ABI.
Architectures are `any`, `x86_64`, `arm64`; the current guest planner targets x86_64.
Names use lowercase letters/digits/hyphens with a leading letter. Versions are two
or three canonical unsigned numeric components; dependencies use exact equality.
There is no version ordering, general solver or downgrade/upgrade operation yet.

Repeated records:

- `requires<TAB>NAME<TAB>VERSION`: exact dependency on a known bundled component.
- `file<TAB>/DEST/PATH<TAB>0644|0755<TAB>LENGTH<TAB>LOWERCASE_SHA256`: regular payload.

Source bytes reside at `payload` followed by the canonical absolute destination
path. Only `/usr/share/doc/` and `/usr/share/zenith/` data paths are allowed. The
reserved component-inventory path is excluded. Reject duplicate paths/fields/
dependencies, parent-file collisions, traversal, repeated separators, invalid
hashes/modes/types and unknown fields/versions. A data package cannot impersonate
a bundled component. Exact dependencies and architecture/runtime mismatches block
a plan; the planner does not fetch dependencies.

| Limit | Bound |
|---|---|
| Manifest | 32 KiB, 1024 characters per record |
| File records / dependencies | 64 / 32 |
| Each file / total payload | 256 KiB / 4 MiB |
| Destination path | 240 ASCII characters |
| Builder directory depth / per-directory / total entries | 32 / 128 / 512 |

Payload reads enforce declared length plus an EOF check and SHA-256 verification.
Native manifest/configuration readers reject device inputs and enforce bounded reads
even if a regular file grows. Parent/leaf symlinks in payloads are rejected. Existing
target files, directories at a file destination, and non-directory ancestors are
unowned conflicts; nothing is overwritten. Planning is a snapshot, not a reservation
or transaction. An eventual executor must revalidate, enforce privileges and refuse
races independently.

## What this makes concrete, and what follows

The kernel, base services and temporary DE have distinct component identities.
The data reader/planner has no GUI or transport dependency and is used by both host
tooling and the guest CLI. Future system/userland package kinds can evolve separately
with explicit compatibility rather than ad hoc file replacement. `/etc`, accounts,
user data and boot activation stay outside ordinary data extraction.

This directory-bundle contract is an experiment, not a frozen production archive or
trust format. Before P1: choose a streamable container, record installed ownership,
default/config digests and conflict policy, enforce space/cancellation and exclusive
mutation, and prove staging/recovery with disk faults and reboots. Before online
distribution: prove a maintained native signature verifier and metadata trust/freshness.
System releases additionally require versioned boot artifacts and matched root/recovery
selection. Independent desktop/app updates require the M5 loader/isolation/API work.

Run `python3 -B tools/test_package_tool.py` for actual builder/plan attacks and
`python3 tools/foundation-smoke.py /tmp/zenith-foundation` for native checks. Host
unit coverage includes malformed manifests, compatibility/dependencies, corruption
and file/directory/ancestor conflicts. Exact results are in [Master roadmap](../../ROADMAP.md#current-focus-and-execution-order).
