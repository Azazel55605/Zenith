# Package manager and installed updates: design discussion

**Status: staged design, 2026-10-11.** The [P0 offline prototype](PACKAGE-PROTOTYPE.md)
implements a host builder and shared host/guest inspect/dry-run core. Signed offline script/data installation, ownership and dependency/conflict handling
are now implemented in [PACKAGES](PACKAGES.md), with validation recorded in [PROGRESS](PROGRESS.md).
Online signed metadata/client and system-update activation remain unimplemented. This
document records the broader design and future work; the P0 directory bundle remains an experiment.
Accepted first container: bounded uncompressed .zpk with RSA-PSS/SHA256 authentication;
core/extra sections and grouped tool identities. Native tool separation follows P1 before
independent compiled tool updates.
Clarified direction: future desktop/apps are independent userland components;
initial repository hosting is a dedicated home VM reachable on LAN and through existing VPN/DNS. See [USERLAND](USERLAND.md),
[PACKAGE-SERVER](PACKAGE-SERVER.md) and [UPDATE-RECOVERY](UPDATE-RECOVERY.md).
Networking, secure guest signature verification, application loading and boot recovery
must not be inferred from examples below. Command names and paths are illustrative.

## 1. The problem and achievable first outcome

Today a changed built-in command, desktop app or driver requires compiling Zenith.
An ISO is a delivery/installation medium, not an inherent requirement for updating
an already installed disk. We can eventually publish a compiled kernel ELF and
matched system files, download or copy them to the installed system, activate them
through the boot layout and reboot. The build still happens on the release builder;
the installed machine would not need to compile an ISO or have the .NET toolchain.

Current compiled applications cannot be updated by dropping a C# DLL into `/bin`.
There is no dynamic executable loader or stable userspace ABI. Distinguish three
package classes before choosing a format:

| Class | Examples | Activation |
|---|---|---|
| Data/script | Shell scripts, documentation, wallpapers, default configuration | File/service refresh; no new compiled code |
| System release | Kernel ELF, built-in apps/drivers, matched system data | Coordinated release generation plus reboot |
| Userland/service/application | Future compositor, DE/WM, apps, optional WASM programs and resources | Requires M5 loader/ABI/runtime, API version and launcher/process integration |

Recommended first implemented package operations are offline data/script installation
and removal on disposable ext2 installations. The first user-facing update objective is kernel/system updating using versioned boot
artifacts, while the loaded kernel continues until reboot. Coordinated releases are
needed where files/config are coupled. In the accepted future architecture, kernel,
base services, desktop and apps have independent packages and explicit API dependencies;
a whole-kernel rebuild for every desktop change is a temporary limitation.
Do not distribute untrusted scripts as though they were sandboxed: scripts execute
built-ins with the session's effective privileges.

## 2. Implementation choices

| Approach | Benefit | Cost/boundary | Recommendation |
|---|---|---|---|
| File packages plus coordinated system generations | Suits scripts/data now and runtime apps later; inspectable file ownership | Must design transactions, config handling and separate boot activation | Preferred working direction |
| Whole-root image/A-B updates | Simple matched release and fallback concept | Needs partition/space redesign; preserving writable data and migration; current 512 MiB root limit | Evaluate for system releases, not ruled out |
| Port an existing Linux package manager | Familiar ecosystems and dependency metadata | Needs compatible runtime/libc, processes, scripts, tools and policy unavailable today | Defer; importing packages is not just parsing archives |
| Ad hoc download-and-overwrite | Small prototype | Cannot provide coherent ownership, trust or recovery | Only a disposable experiment, not the update design |

The recommended approach may be hybrid: file packages for mutable data/apps, immutable
release generations or A/B roots for core system files. The recovery experiment must
choose the system layout before ordinary package transactions can write kernel paths.
Do not settle the choice simply because one archive library compiles on the host.

## 3. Package and compatibility model

Proposed package identity: name, version, architecture, kind, format version and
release/runtime compatibility. Start with exact dependencies or dependencies within
one release set rather than a general constraint solver. Validate names and normalized
version syntax; explicitly decide version ordering and downgrade policy. Record
conflicts and file ownership; never allow one package to silently replace another's
file. Initially use one official repository/channel, adding third-party trust later.

A proposed manifest should cover:

- Identity, description, license and build/source provenance.
- Minimum package-reader schema and target `x86_64`/architecture-independent identity.
- Required Zenith system generation or runtime API version, where applicable.
- Exact dependencies/conflicts and package class; essential packages cannot be removed
  without a defined recovery operation.
- File records: normalized path, type, length, digest, executable/mode metadata and
  permitted ownership policy. Distinguish default config from replaceable program data.
- Installed/temporary/backup space estimates, reboot requirement and artifact digest.

These fields are the production design checklist, not a frozen format. The P0
prototype implements a deliberately smaller data-only schema; it does not freeze
the eventual signed container. Signature metadata must bind
manifest and payload, including compatibility, sizes and paths. A payload digest without
a trusted signature is not authentication.

### Container decision

Compare a small versioned container against a restricted standard archive plus manifest.
A custom container reduces parser scope but creates tooling maintenance. Restricted tar
has familiar host tooling but needs strict validation of types, headers, extensions,
lengths and extraction behavior. ZIP brings central-directory/compression complexity.
Select only after testing candidate guest parsers under Cosmos NativeAOT.

Recommended first payload: bounded, uncompressed regular files/directories, streamed in
chunks, with no links, device nodes or arbitrary scripts/hooks. Compression can follow
once decompression limits and dependencies are verified. The current ext2 file/volume
limits constrain payloads and caches; desktop transfer limits are not package limits.
Define concrete limits for index size, files per package, pathname bytes/depth, per-file
and total expanded bytes, dependencies, transfer duration and simultaneous staging.

Reject absolute payload paths, `..`, malformed encodings, duplicate/case-alias entries,
NUL, unknown required fields/types and inconsistent lengths. Destination resolution
must resist symlink ancestors and mount escapes; string normalization alone is not
sufficient. Metadata-controlled output cannot target `/dev`, `/proc`, other mounts,
package state or boot files except through authorized class-specific operations.
First tests must prove no writes occur outside the selected install root.

## 4. Client layers and proposed CLI

Keep the core host-testable and separate from transport and GUI:

1. Manifest/index/container parser and bounded readers.
2. Trust verifier and trusted metadata state.
3. Installed database and file ownership/configuration policy.
4. Planner for dependencies, conflicts, compatibility and disk space.
5. Staging/verification and transaction/recovery executor.
6. Transport adapters for local files and later network mirrors.
7. CLI and Settings/Updates presentation over the same service.

Proposed commands: `pkg list`, `pkg info NAME`, `pkg install FILE`, `pkg remove NAME`,
`pkg verify`, `pkg refresh`, `pkg upgrade --plan` and an explicit apply operation.
Initial install is local; refresh/network upgrade remain unavailable until implemented.
Planning should print changed files/packages, download/staging space, config conflicts
and reboot requirement without mutating the installation. Status must separate installed,
staged and pending-boot generations. No background auto-apply in the first version.

Proposed state locations: `/var/lib/zenith/pkg` for installed/trusted/transaction state,
`/var/cache/zenith/pkg` for bounded downloads, `/etc/zenith/pkg` for policy/repository
configuration. These paths are not created yet. Cache eviction must preserve active
transactions and recovery artifacts. Updates in a RAM-root live boot should initially
be refused with an explanation; disposable testing can use an explicit test root.

## 5. File ownership and configuration

Program/resource files are package-owned; mutable user data is never package-owned.
Preserve `/home`, account databases and local settings on upgrade. For `/etc` defaults,
record the last shipped digest: replace an unchanged default, retain a modified file
and offer a new-default companion/conflict decision. Do not merge account/password
files as generic text configuration.

Remove only files still owned by the package and covered by the removal plan; retain
changed configuration according to documented policy. Refuse collisions with unowned
files until the user chooses a safe conflict action. Keep an exclusive transaction
lock; concurrent GUI/CLI installs must not race. Run privileged mutations through one
access-controlled service when M2 credentials exist. A lock within pkg does not stop
other in-kernel code from modifying files, so define concurrent-editor handling too.

Do not run maintainer shell scripts in the first format. Needed actions should initially
be declarative and narrowly implemented (for example a launcher entry after M5), with
bounded inputs and tested rollback. Full dependency solving, delta updates and third-party
repositories are later features.

## 6. Transactions and ext2 recovery

Staging and rename are useful but **do not make ext2 multi-file updates crash-atomic**.
Rename itself has no journal, and current mount behavior refuses crash-dirty ext2 until
host repair. A transaction log on that root cannot alone guarantee recovery before a
writable mount. That is an explicit blocker for claiming unattended crash-safe upgrades.

Proposed executor state machine:

`planned → fetched → verified → staged → applying → committed → cleanup`

Recovery must also recognize failure at every transition. The plan includes a durable
transaction identity, expected old/new hashes, backups, per-step progress and intended
installed DB generation. All payloads are verified and space checked before mutation.
Preserve the previous state until new data and installed database are durably committed.
Cancellation before applying removes staging; cancellation during applying follows the
transaction recovery policy rather than stopping between arbitrary writes.

Experiment with immutable version directories and a small activation record, but test
torn records, directory durability, interrupted renames and out-of-space. A fallback
kernel using the same partially changed root is not a full-system rollback. Shared
state/schema migration may prevent an older release from booting safely.

Potential recovery strategies to evaluate:

- Initial manual recovery from a trusted live medium plus host e2fsck: acceptable for
  a clearly labeled developer preview, not an unattended production updater.
- Guest recovery environment and bounded repair/journal support: substantial new work;
  check-only fsck does not provide repair.
- Verified read-only release images with writable data kept separate, or A/B roots:
  larger install-layout changes but stronger generation separation.

Choose and prove the recovery model before advertising crash-safe system updates.
Versioned kernel-only installation can precede complete root rollback. The requested
snapshot/trial-boot/restore experience and backend alternatives are detailed in
[UPDATE-RECOVERY](UPDATE-RECOVERY.md).

## 7. Kernel, ESP and boot activation

System updates need a separate coordinator; ordinary payload extraction must not overwrite
the active kernel, Limine binary or boot configuration. Kernel and system data carry a
shared release identity. Keep at least one known-good compatible generation if space
allows. Validate ELF/build identity and expected platform before staging.

Proposed flow: fetch matched artifacts, verify them, stage an inactive boot/system
generation, flush, publish activation state, reboot, then confirm successful startup.
What constitutes successful startup must include root/config compatibility and update
service recovery, not merely a serial banner. If confirmation never arrives, expose a
fallback through a boot menu and, if supported, attempt accounting. Limine automatic
rollback/attempt counting is **not verified or implemented**; manual fallback may be
first. ESP FAT updates also need interruption tests; rename cannot be assumed durable.

New installations have a 128 MiB ESP and at most 512 MiB ext2 root (older ESPs may be 64 MiB).
The installed versioned layout is documented in [INSTALLATION](INSTALLATION.md);
P1 still rejects kernel/boot packages. Check actual
artifact/backup/staging sizes before choosing retention. Separate `/boot` files on root
from the ESP paths firmware/Limine use; updating the former alone may change nothing
at boot. M4 boot-artifact access and exact root selection now support the CLI installer.
Signed activation, retained-version ownership and interruption recovery still gate
the updater. Bootloader updates should initially be separate,
rare and explicit; avoid replacing firmware entry plumbing for every kernel update.
An old ISO/root rescue route must remain usable when activation fails.

## 8. Networking and trust

Networking is needed for online updates, **not for implementing the local package core**.
M3 should provide DHCP/static address, DNS, bounded TCP transfer, timeout/retry/cancel,
progress and diagnostics. Begin integration against a disposable local test mirror.
Do not assume host HTTP or crypto classes are fully plugged in Cosmos.

Transport security and package authenticity are separate. HTTPS is desirable; signed
metadata/artifacts are still needed to avoid trusting a mirror or cache with release
identity. Trusted keys should arrive through the trusted installation image or an
explicit authenticated bootstrap, never from the same untrusted index being verified.

Use [The Update Framework specification](https://theupdateframework.github.io/specification/latest/)
as a reference for signed target metadata, key rotation, expiry, rollback protection
and consistent repository snapshots. TUF addresses obtaining trusted artifacts; it
does not define our package extraction or disk-activation transaction. A simplified
custom protocol must document its weaker guarantees and must not be called TUF-compliant.

Before implementation choose a guest-compatible maintained crypto/verifier library,
validate standard positive/negative test vectors natively and pin algorithms/encodings.
Signing happens on the trusted publisher; the guest only needs verification keys.
Secure entropy is separately needed for passwords and TLS-related operations; digest
and signature verification should not depend on Cosmos generic random. M2 provides
fail-closed password/device entropy; maintained signature/TLS verification still needs
independent native feasibility and trust design.

Clock/expiry policy is unresolved: bad RTC time can undermine expiration checks; unauthenticated
NTP is not a trusted freshness source. Persist accepted metadata versions and define
behavior for untrusted time, replayed indexes, key rotation and explicit recovery/downgrade.
Do not silently disable verification to make a lab mirror work. HTTP-only testing must
be explicitly scoped; public deployment awaits agreed verification and transport policy.
This design does not provide secure boot against a physically replaced kernel/root.

## 9. Mirror and publishing pipeline

Recommended first mirror is a static repository served by a conventional host/web
server, separate from Zenith. The guest does not need to run the package mirror.
Initial hosting is a dedicated VM on the user's home infrastructure with its own IP,
LAN access and existing VPN/DNS remote access; VM OS/address/storage details are pending. Public exposure/domain remain later decisions; no mirror
is provisioned here. The [home repository plan](PACKAGE-SERVER.md) gives the staged setup.

Proposed published layout:

- Versioned signed metadata/indexes per channel and architecture.
- Immutable artifacts addressed by digest or immutable release/version identity.
- Kernel/system release manifests tying matching artifacts together.
- Package manifests, license/source information and build provenance.

Publishing workflow: build pinned sources/toolchain, run host and native tests, assemble
packages/releases, validate generated metadata, sign in a protected release step, upload
immutable artifacts, then publish their new signed metadata snapshot. Mirrors copy signed
bytes, not regenerate them. Protect signing keys separately from ordinary mirror hosting;
use offline root trust and documented rotation/revocation. Decide exact trust roles after
verifier feasibility and TUF evaluation. Keep previous releases until supported clients
can migrate/recover, and test fetching through stale or incompletely replicated mirrors.

Existing CI publishes ISO/ELF on `v*` tags. It must eventually produce package artifacts
and a coordinated release manifest, but changing GitHub release output alone is not a
package repository. Signing/publication requires explicit credentials and deployment work
later; none are assumed in this planning task.

## 10. Implementation stages and required tests

| Stage | Scope | Completion evidence |
|---|---|---|
| P0: format/recovery experiments | Host builder, parser, plan; candidate guest verifier; boot-layout design | Round-trip fixture, malicious archive/manifest corpus; decisions recorded |
| P1: local data packages | Offline CLI, ownership DB, staging/config policy, removal | Native installed-root install/upgrade/remove; reboot persistence; interrupted/out-of-space recovery |
| P2: dedicated home VM | Native static repository, own IP, LAN and existing VPN/DNS access | Provisioned endpoint and publish/fetch checks; signing separated from hosting |
| P3: system generations | Kernel plus matched data, inactive boot artifacts, confirmation/fallback | Disk-only UEFI upgrades and failed-boot fallback; fault injection at activation boundaries |
| P4: repository client | Signed metadata/mirror, bounded network fetching | Tampered/truncated/replayed/expired metadata rejected; key rotation and interrupted download tested |
| P5: independent userland packages | M5 ABI/process/IPC contract, desktop/app dependencies and launcher entries | Install/run/remove app or desktop component without compiling a new kernel |

P0 builder/reader/dry-run work began with S0. P1 now implements bounded signed
containers, native RSA-PSS verification and root-only installed script/data transactions.
The concrete scope and recovery limits are in [PACKAGES](PACKAGES.md); P3 needs verified boot-artifact/root handling and an explicit recovery policy. P4
depends on M3 and trust verification; P2 provisioning can happen before the guest
client is ready. The precise P3/P4 order can change after boot/crypto research; neither is a quick side effect of a `fetch` command.

Test dimensions: malformed metadata/container, extraction escapes, hash/signature failure,
wrong architecture/runtime/release, dependency cycles/conflicts, insufficient root/ESP
space, concurrent operations, config preservation, permission denial, cancellation,
short writes, device errors, reboot at each persistent phase and failed new kernel boot.
Independent e2fsck and content/ownership inspection accompany native disk mutation tests.
Host serialization tests alone do not establish safe installed updates.

## 11. Choices to settle together

1. **Update priority:** accepted scripts/data first, then native ABI/process foundations
   and compiled tool unbundling; system generations remain a separate gate.
2. **System layout:** coordinated file generations versus read-only/A-B root images;
   choose after interrupted-update recovery experiments and space measurements.
3. **Package scope:** P1 uses official-key signed regular files, numeric dependency
   ranges and declared conflicts, without hooks or compression. Broader formats,
   providers and configuration merges remain later decisions.
4. **Trust:** maintained TUF implementation/compatible verifier versus a deliberately
   limited protocol; choose libraries, clock policy and key management before public updates.
5. **Userland:** independently loaded desktop/apps are the accepted target. Investigate
   protected native processes/ABI and Bash requirements; WASM is optional, not the
   assumed final desktop architecture.
6. **Operations:** mirror hosting/channel policy, release retention and whether automatic
   downloads should later be optional. First apply remains user initiated with visible plan.

Implemented: P0 unsigned inspection/dry-run and P1 signed offline scripts/data
installation, ownership, upgrade/removal and recovery. M3 verified transport is available.
Next: native ABI/process foundations for compiled tools, dedicated VM provisioning
and signed online metadata. Kernel boot generations require their own validation.
