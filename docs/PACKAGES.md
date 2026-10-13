# Signed offline packages

P1 implements independently installed **scripts/data** on an installed ext2 root.
Compiled tools, drivers and the test DE remain kernel-linked. User-selected order:
install scripts/data first, then establish native executable/process/ABI boundaries
before unbundling compiled tools. Do not treat dropping a DLL into /bin as execution.

## Repositories and base packages

`pkg repos` reads `/etc/zenith/repos.conf`; defaults are **core** (priority 10) and
**extra** (20). Core is the essential system/base collection; extra is optional tools
and applications. Repository section and release channel are distinct concepts.
Mirrors default to unset because deployment URLs and TLS trust belong to the installation.
The home VM is 10.0.20.7; verified HTTPS at zenith-stable.net.local and complete
signed downloads are user-confirmed. Base URLs use HTTPS without credentials,
query or fragment. P1 remains the transaction engine described here; 0.0.7 adds
[signed online discovery and dependency download](ONLINE-PACKAGES.md).

`pkg list` distinguishes bundled identities from the installed DB. Bundled identities
include zenith-kernel, zenith-base, base-tools (shell/text), net-tools (IPv4 commands),
fs-tools (filesystem/accounts), and zenith-desktop-test, all bootstrap version 0.1.
These reserve future package names and describe the running image; their compiled
code cannot yet be independently removed or updated. The first third-party sample
below is hello-tools, a real installed shell script, not a native executable.

## Build, sign and deliver

Host tooling currently requires Linux, .NET 10 and OpenSSL for validation fixtures.
Use a signing directory outside any served repository. Generate a development key
once, keep its private half on the builder and convey its public fingerprint through
a trusted channel. No signing keys are generated into or committed with Zenith.

```sh
# This creates a NEW directory; private.pem and the directory are owner-only.
dotnet run --project tools/Zenith.PackageTool -- keygen /tmp/zenith-signing
mkdir -p /tmp/hello-source/usr/share/zenith/scripts
printf 'echo hello-from-package\n' > /tmp/hello-source/usr/share/zenith/scripts/hello.sh
dotnet run --project tools/Zenith.PackageTool -- build hello-tools 1.0.0 /tmp/hello-source /tmp/hello-bundle --repo extra
dotnet run --project tools/Zenith.PackageTool -- seal /tmp/hello-bundle /tmp/zenith-signing/private.pem /tmp/hello-tools.zpk
dotnet run --project tools/Zenith.PackageTool -- check /tmp/hello-tools.zpk /tmp/zenith-signing/public.pem
sha256sum /tmp/zenith-signing/public.pem /tmp/hello-tools.zpk
```

The builder defaults to 0644 files. Explicit `sh PATH` runs the example. For an
executable script, change its manifest file mode to 0755 before sealing. Additional
requires/conflicts records can also be added before sealing; the tool validates the
complete manifest and checks payload lengths/hashes. Neither builder nor sealer
replaces existing output. Link/special-node sources are rejected. No extraction
runs maintainer hooks or native code; shell scripts run built-ins with caller privileges.

Initially copy the signed .zpk and public PEM to an accessible disk or use `fetch`
from a known LAN endpoint. Verified HTTPS needs separate TLS CA setup from
[NETWORKING](NETWORKING.md). A downloaded PEM is trusted only after its exact file
fingerprint is verified out of band. Unsigned HTTP can transport these fixture bytes;
the trusted signing key authenticates packages. It does not authenticate an unsigned
repository index, establish freshness, or protect against denial of service.

In the installed Zenith root, as root:

```sh
pkg repos
pkg trust /path/public.pem EXACT_PUBLIC_PEM_SHA256
pkg check /path/hello-tools.zpk
pkg install /path/hello-tools.zpk
pkg list
sh /usr/share/zenith/scripts/hello.sh
pkg verify
pkg remove hello-tools
```

`pkg trust` validates a public RSA key and checks its SHA256 before atomic replacement.
The trust file is `/etc/zenith/package-key.pem`. Private PEMs, malformed/small keys
and wrong fingerprints are rejected, preserving existing trust. Public package
verification needs no entropy, wall clock or TLS connection. Importing a replacement
key is currently an explicit root operation; multi-role/key rotation is future work.

## Dependencies, conflicts and ownership

Manifest v2 adds required `repository` core/extra, optional repeated `conflicts NAME`,
and numeric dependency constraints. Name/version/runtime/architecture/file rules
retain the bounded [P0 contract](PACKAGE-PROTOTYPE.md). V1 remains readable and is
classified as extra for offline compatibility. A signed v1 package is authenticated
by its outer container; an unsigned directory manifest remains inspect/dry-run only.

```text
ZENITH-PACKAGE<TAB>2
repository<TAB>extra
requires<TAB>net-tools<TAB>>=0.1,<1.0
conflicts<TAB>alternative-tools
```

Numeric two/three-component versions compare numerically; 1.0 equals 1.0.0. Supported
constraints are exact/`=`/`>`/`>=`/`<`/`<=`, or two comma-separated comparisons forming
a range. Epochs, prereleases, wildcard/OR constraints, virtual providers and automatic
replacement are not supported. Conflicts apply regardless of which package declared
them. There is no force-overwrite or implicit conflicting-package removal.

`pkg install A.zpk B.zpk` authenticates up to 16 explicitly supplied candidates and
checks the entire resulting installed set plus bundled components. Dependencies may
be satisfied by already installed packages or another supplied candidate. Missing or
incompatible dependencies block the whole transaction and name the requirement;
they do not download packages silently. Cyclic script/data dependencies may be
satisfied as one transaction because no install hooks execute. Upgrades recheck the
requirements of existing dependents; downgrades are refused. Removal is blocked while
another remaining package depends on a removed identity. `pkg remove A B` removes a
complete supplied set atomically, including dependency cycles, without implicit autoremove.

The DB records signed manifest, package SHA256, signing-key fingerprint and file
ownership in `/var/lib/zenith/pkg/installed.tsv`. Packages cannot own the same file,
collide with another file's ancestor, overwrite unowned files or impersonate bundled
identities. Current destinations are /usr/share/doc and /usr/share/zenith, except the
reserved component-inventory path. /etc, /boot, /bin, user data, devices, links and
cross-mount writes are excluded. Compiled tools will get executable paths after the
native ABI is implemented, not by weakening this data installer.

Owned files must match their recorded digest and mode before update/removal. A local
edit blocks mutation; save/move the edit or restore the shipped file before retrying.
This is conservative preservation, not a .pacnew/config merge implementation. Because
/etc payloads are excluded, default configuration packages remain a later extension.
`pkg verify` reports modified/missing owned files without repairing or deleting them.

## Transaction and recovery

Mutations require root and an installed root with Unix/ext2 metadata. Package commands
are serialized and cancellation is checked before verification/staging and during
mutation. Payloads are atomically renamed into place, modes applied, and DB publication
is the commit decision. Cancellation/faults attempt explicit undo; failure to undo
leaves a journal and blocks `pkg list`/verify until root runs `pkg recover`.

The undo journal and hashed backups are flushed before any payload mutation. Recovery
validates all records/backups before writing, detects the committed DB by its digest,
then either finishes cleanup or restores the previous files/DB. Removing the journal
is synced before deleting backups. `pkg recover` is idempotent and callable after a
reboot; normal install/remove first attempts recovery. Empty created directories and
inert backups after cleanup interruption may remain. Occupied .pkg-new staging paths
are refused; after filesystem repair, root may need to inspect/save/remove a stale
staging file before retrying recovery. Unowned staging files are never overwritten. Package mutation of another
mount or through a symlink ancestor is rejected. Other privileged kernel code/root
sessions remain trusted; this is not per-process isolation or a general root-writer lock.

Bounds: 32 installed packages, 64 files per package, 32 dependencies/conflicts,
32 KiB manifest, 256 KiB/file, 4 MiB/package, 1 MiB installed DB, 1024 changed paths,
8 MiB old+new transaction payload and a 256 KiB free-space reserve plus DB sizes.
Space checking is conservative but does not reserve blocks/inodes; late write failures
must roll back. Each input container is bounded before allocation. Package commands
can retain up to 16 candidate containers; independent userland/resource quotas remain
future work.

Recovery tests cover injected storage failures, cancellation and a prepared journal
surviving a **clean** installed reboot. Ext2 is still journal-free: sudden power loss
can leave the filesystem dirty and require host repair before Zenith can mount it.
This installer does not supply Btrfs snapshots, trial boot or arbitrary power-cut safety.

## Container v1 and planned online delivery

.zpk starts with printable ASCII, TAB-separated, LF-terminated header:

```text
ZENITH-SIGNED-PACKAGE<TAB>1<TAB>PUBLIC_PEM_SHA256<TAB>CONTENT_LENGTH<TAB>SIGNATURE_LENGTH<LF>
```

Content is a little-endian uint32 manifest length, exact manifest UTF8 bytes, then
file payloads concatenated in manifest order. Trailer is an RSA-PSS signature over
SHA256 of **the exact header and content**, binding type/version/key/lengths, metadata
and payload. RSA is 2048..4096 bits; SHA256/MGF1-SHA256 with a 32-byte salt. No trailing
bytes, compression, links, devices or optional unknown metadata. Host signs with
.NET RSA; the guest verifies through the pinned Mbed TLS fixed-memory port.

The dedicated home VM will serve read-only artifacts and signed metadata:

```text
repo/
  core/stable/index.signed
  extra/stable/index.signed
  artifacts/sha256/<PACKAGE_SHA256>.zpk
```

Publish immutable artifacts first, then a complete signed index generation. Indexes
must bind repository/channel/architecture, package manifest/dependencies/conflicts,
artifact hash/size and generation/expiry. The client will verify metadata before
resolution, select candidates deterministically (core before extra), display the full
install/remove/restart plan, fetch only missing artifacts, verify their hashes and
signatures, and invoke the same offline transaction engine. TLS protects transport;
package signatures authenticate bytes. Persisted rollback floors, trustworthy expiry,
key rotation/revocation and outage/cancellation tests are gates before automatic online
installation. These indexes/resolver/online refresh and VM deployment are **planned**,
not implemented by setting a mirror URL. See [PACKAGE-SERVER](PACKAGE-SERVER.md).

## Validation

```sh
dotnet test tests/Zenith.Tests
python3 -B tools/build-tls.py /tmp/zenith-package-tls --host
python3 -B tools/test_signed_package_tool.py /tmp/zenith-package-tls/libzenith-tls.so
python3 -B tools/package-smoke.py obj/package-smoke
```

The native suite installs a disposable ext2 root, downloads fixture bytes, imports a
pinned public key, rejects tampering, checks dependencies/conflicts/local edits,
executes/upgrades a script without rebuilding, reboots with an interrupted journal,
recovers, checks user permissions, removes packages and runs independent e2fsck/debugfs.
No signing private key is served or sent to the guest. Exact results and remaining
limits are recorded in [PROGRESS](PROGRESS.md).

## Signed kernel containers

P3 uses a distinct signed header and dedicated boot coordinator; ordinary `pkg install`
still accepts scripts/data only. See [KERNEL-UPDATES](KERNEL-UPDATES.md) for the builder,
`pkg kernel check|install`, confirmation, retention and recovery. This does not add
native userland execution, arbitrary boot-file payloads or root snapshots.

Interactive `fetch`, `pkg install` and `pkg kernel` operations use one updating
status line; fetch shows a byte percentage/bar, data installation shows file progress
and commit phases, and kernel updates show the current phase. Progress is omitted
from redirected stdout; final results/errors remain. File application reaching 100%
is not a committed transaction until the final success message.


## Independently delivered manuals, fonts and native text tools

The source now defines zenith-man-pages/core and zenith-fonts/core generators,
zenith-docs/extra static documentation, and sunbeam-tools/extra native source. Full
manuals install in /usr/share/zenith/man and take precedence in `man`; kernel recovery
intro/shell/headless/license pages stay under /usr/share/man. Signed owned fonts install in
/usr/share/zenith/fonts and take precedence on the next desktop initialization;
embedded recovery copies remain available. Font licenses ship with the payload.

`build-native NAME VERSION PAYLOAD OUTPUT --repo extra` validates ELF ABI 1, emits
x86_64 metadata, requires zenith-native-abi 1.0 and records ELF paths at 0755. Native
containers remain signed data packages and cannot write boot files or arbitrary
system paths. Execution is a separate protected loader operation with ownership,
digest and mode checks ([native ABI](NATIVE-PROGRAMS.md)). Publishing base-tools,
net-tools or desktop inventory labels as if they were independent binaries would
be incorrect; their actual ports require additional APIs.

`pkg update [--yes]` maintains all installed signed packages and stages a newer
kernel. It permits up to 32 selected data packages within the existing total DB,
8 MiB transaction and per-file limits. Offline supplied-file CLI remains bounded
to 16 archives. See [ONLINE-PACKAGES](ONLINE-PACKAGES.md) for preflight, freshness,
confirmation, separate data/boot commits and first upgrade from 0.0.7.
