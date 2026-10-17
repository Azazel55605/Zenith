# Zenith repository instructions

This is Zenith, a Cosmos Gen 3 NativeAOT kernel with a Unix-style system layer and
small graphical desktop. Read this file before changing the repository.
[AGENTS.md](AGENTS.md) and [CLAUDE.md](CLAUDE.md) both point here; maintain shared
guidance here so the two entry points stay consistent.

## Source of truth

- [docs/README.md](docs/README.md): documentation hub and reading paths.
- [docs/STATUS.md](docs/STATUS.md): current component/release acceptance summary.
- [docs/PROGRESS.md](docs/PROGRESS.md): bounded active task, steps and validation.
- [docs/ROADMAP.md](docs/ROADMAP.md): ordered future slices and milestone exits.
- [docs/components](docs/README.md#components): implementation checklists, source
  owners, remaining steps and acceptance for each component.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): current technical architecture.
- [docs/SYSTEM-PLAN.md](docs/SYSTEM-PLAN.md): accepted product requirements.
- [docs/PACKAGE-MANAGER.md](docs/PACKAGE-MANAGER.md): current package design and open decisions.
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md): toolchain, validation and Cosmos builds.
- [docs/HISTORY.md](docs/HISTORY.md) and [docs/archive](docs/archive/README.md):
  historical narrative and completed execution evidence.
- [README.md](README.md): user commands, layout and installation overview.
- [docs/cosmos/index.md](docs/cosmos/index.md): pinned upstream documentation;
  [SOURCE.md](docs/cosmos/SOURCE.md) records its revision.

Follow [docs/MAINTAINING.md](docs/MAINTAINING.md) when documenting a slice. Update
PROGRESS and the component checklist as actual implementation/validation steps
happen. Keep PROGRESS short: archive completed detailed evidence in a dated record,
then update STATUS when delivery/acceptance changes. Separate implemented, locally
verified, server-published and operator-installed claims. Do not prepend a permanent
execution ledger or duplicate historical test totals into the roadmap.

## Architecture and conventions

- `Kernel.cs` composes shared `SystemRuntime` initialization and `SessionRuntime`;
  `DesktopSessionHost` owns fonts/canvas/windows; x64 `HeadlessSessionHost` owns a
  bounded COM1 shell. `OnBoot` bypasses Cosmos's graphical KernelConsole bootstrap.
  A missing supported display falls back to serial; WM remains unavailable.
  `Run()` ticks the selected host and halts until an interrupt.
- Settings persistence belongs to `SettingsService`, shared by CLI and future GUI.
  Configured installed accounts use the graphical greeter and local text/DE session
  lifecycle; no-GPU COM1 remains recovery. Preserve worker/unsaved/mutation teardown,
  hidden authentication and package-loaded appearance/legacy catalog checks
  (docs/LOGIN-SESSIONS.md). The GUI host remains trusted and kernel-linked.
  Keep local /etc state separate from shipped package defaults. See
  [settings/session guide](docs/SETTINGS-SESSIONS.md).
- P0 directory bundles remain unsigned inspect/dry-run experiments. P1 signed .zpk
  scripts/data packages have an ext2 installed-root transaction/ownership engine;
  see [PACKAGES](docs/PACKAGES.md). Preserve signature, dependency/conflict, local-edit,
  fault/cancel and installed-reboot recovery tests. 0.0.7 adds bounded signed private
  core/extra indexes and named plan/install/upgrade (docs/ONLINE-PACKAGES.md).
  Preserve freshness, persisted generation floors and native installed HTTPS checks.
  /etc merges, public trust-role/key-rotation hardening,
  matched root-data updates remain separate gates. 0.0.8 adds unified pkg update,
  independent manuals/fonts and the first signed protected x64 native text tool.
  0.0.13 adds managed filesystem service yield/resume and read-only Sunbeam
  cat/ls/pwd/stat. The native I/O extension adds finite shell stdin, guarded
  file writes and Sunbeam tee; new bundles declare zenith-native-io-abi 1.0.
  The stream extension adds live standard descriptors/canonical host input and
  8 KiB byte pipes (eight shell stages/workers, eight native contexts globally).
  Raw entry stays serialized on the BSP; managed service waits may overlap. New
  bundles require zenith-native-stream-abi 1.0. Keep stream/cancel/early-exit tests.
  Preserve full-range copyin/copyout checks, identity/cwd, handle cleanup,
  user FP/register state, request/interrupt budgets and native permission/cancel tests.
  See docs/NATIVE-PROGRAMS.md for foreground ABI limits; preserve fault/NX/W^X,
  credentials and same-kernel executable-upgrade acceptance. Compiled base/net/fs
  and GUI migration still need additional controlled APIs.
- Opt-in x64 `smp=native` starts isolated native integer workers (docs/MULTICORE.md).
  The x64 IDT loader must preserve IF: Cosmos 3.0.89's premature STI is redirected
  in signature-checked build copies. Keep installed UEFI startup/IRQ-state checks.
  Managed scheduling/GC, drivers and the foreground process bridge stay on the BSP.
  APs own static stacks/tables, never managed objects or UART, and currently poll
  when idle. Preserve default-disabled boots and 1/2/4-CPU acceptance; do not enable
  managed AP execution before per-CPU context and GC synchronization are implemented.
- `src/Core` holds system behavior; `src/Gui` rendering and window management;
  `src/Apps` desktop windows. Keep UI concerns out of the core layer.
- M3 has a primary IPv4 service and diagnostic/NTP/HTTP/verified HTTPS commands; see
  [NETWORKING](docs/NETWORKING.md). `Zenith.Network` contains the pure bounded TCP
  buffer injected into build copies of Cosmos by the signature-checked PatcherFixup.
  Keep native fault/cancel/link-loss tests: Cosmos 3.0.89 skips unwind-finally and
  misclassifies typed catches. TLS uses the checksum-pinned offline Mbed TLS source archive and a fixed-memory
  freestanding bridge. Keep certificate/record/cancel/missing-entropy guest checks.
  Home DNS is 10.0.10.3; the Debian package VM is 10.0.20.7. Manual builds/distribution
  and a local-relay installed upgrade are user-confirmed; guest direct VPN stalls
  remain under investigation. Actual VM/VPN acceptance
  remains separate from local test proof.
- Shell sources must remain host-testable without Cosmos. Cosmos-dependent
  commands belong in `src/Core/Shell/Commands/SystemCommands.cs` and register at boot.
- The CLI x64 UEFI installer supplies its own boot artifacts through local Limine
  requests. Preserve pre-erase asset validation, exact GPT root identity, versioned
  kernel layout and RAM rescue ([INSTALLATION](docs/INSTALLATION.md)). P1 scripts/data
  packages must not gain boot-file writes; kernel activation belongs to P3.
- Storage uses ext2 for new installed roots (up to 512 MiB), recognizes older FAT
  installations, and uses RAM-backed FAT for live
  root and RAM-backed ext2 (1777) for `/tmp`; virtual `/proc` and `/dev` are mounted at boot. Experimental
  secondary ext2 volumes require explicit `mount -t ext2` and a supported-profile
  check. `mkfs.ext2 PARTITION --yes` explicitly formats an unmounted secondary
  partition (1..512 MiB); it refuses mounted volumes and the running root disk.
  The locally adapted driver, upstream revision and allocator fix are
  documented in `src/Core/Storage/Ext2/SOURCE.md`; its BSD notice is retained in
  LICENSE and `man licenses`. Keep complete ext2 operations under the per-volume
  reentrant I/O gate, with explicit success/catch release for Cosmos; inode-table
  RMW buffers must remain private to the operation. Preserve interrupted/concurrent
  metadata regressions and remount audits when changing scratch-buffer reuse.
  `fsck.ext2` checks unmounted supported volumes without repairs. Installed ext2 accounts and permission enforcement are documented in
  [ACCOUNTS.md](docs/ACCOUNTS.md); kernel code remains trusted.
- Follow existing C# conventions: file-scoped namespaces, explicit visibility,
  four-space indentation, and braces on separate lines.
- Cosmos packages default to 3.0.89 (`global.json`, `Directory.Build.props`).
  `tools/use-cosmos.sh` switches local/released packages. Inspect actual installed
  APIs before building drivers; do not silently change package versions.
- `../Cosmos` is a separate upstream checkout. Treat it as reference unless the
  task needs an explicit upstream change. Do not run its bootstrap casually:
  it clears Cosmos NuGet caches and replaces global tools.
- Native release diagnostics include per-VM wait-times.jsonl and actual accelerator
  observations. Keep full-sized healthy kernel delivery/activation; the small
  signed startup-failure fixture must prove entry before testing manual fallback.
  Pinned per-tick/per-allocation success traces are quieted in build copies only.
- Preserve unrelated local changes. Keep generated output, scratch disks and
  machine-specific package settings out of commits. Never format a real disk
  during validation; the smoke harness creates its own disposable disk.

## Validation

```sh
dotnet test tests/Zenith.Tests
dotnet publish -c Debug -r linux-x64 -o output-x64
python3 tools/smoke-test.py
```

The host tests link plain kernel sources; new testable core logic should follow
that pattern. Use meaningful regression tests for filesystem behavior. For
Cosmos integration, build the ISO and extend the QEMU smoke harness (ASCII log
assertions). A successful host test or build does not establish guest behavior.
Record exact failures and validation limits in the progress log.
Before a new kernel tag, use `deploy/distributor/validate.py --keep NEW_DIRECTORY`
for unsigned full release acceptance; `--existing` is only for known prebuilt
artifacts. CI uses this same native gate plan. Keep controller regressions in the
common `--controller-only` set so server pre-build checks and CI stay aligned.
Authentication waits share `tools/guest_auth.py` in direct harnesses and the current
distributor controller, including unchanged release tags. Keep prompt and hash/save
completion phases distinct; never replay credentials or weaken hashing to fit a
test deadline. Bump the policy revision when behavior changes so current controllers
can adapt older helpers.

## Current direction

M0, M1 and M2 are complete per the roadmap. M2 (v0.3) provides Unix filesystem semantics:
virtual `/proc` and `/dev`, read-write ext2, an ext2 installer root, users and
permission enforcement, and check-only `fsck.ext2`. ext4 read-only is optional.
Its exit criterion is verified: installed ext2, real users/permissions and truthful
`ls -l`, within the bounded ext2 and trusted-kernel limits in ACCOUNTS. Continue
with explicitly selected roadmap slices and record implementation/validation evidence.

The expanded product requirements include headless, DE and automatic-tiling WM
sessions, shared Settings, future Files rework and installed updates. Their designs
are proposals until discussed; do not treat them as implemented or freeze package
format/runtime/boot-recovery choices without recording a decision. The existing compiled base/net/fs tools and test DE are kernel-linked; Sunbeam
starts separately packaged protected native execution. Offline package research can precede networking;
public mirror updates need verified trust and recovery, not just HTTP fetching.

User clarification (2026-10-11): the future desktop/apps must be outside the kernel;
see [USERLAND](docs/USERLAND.md). WASM is optional, not the assumed final architecture.
Customization/usability belong in proper desktop work. Home package hosting uses a dedicated VM with its own IP and existing
VPN/DNS remote access ([server plan](docs/PACKAGE-SERVER.md)); snapshot/trial boot/restore is a
requested investigation ([recovery plan](docs/UPDATE-RECOVERY.md)), not a Btrfs decision.

User direction after M2: retain the simple DE for testing, replace it during proper
userland/desktop work, and start packaging early. Shared settings/session and offline
package foundations and S1 serial headless are implemented. Keep status accurate in
PROGRESS. Preserve no-GPU live/installed validation when changing boot or shell I/O;
the test DE remains the default when supported display hardware is present.

User deployment prerequisite (2026-10-11): finish guided installation, then implement
and verify signed kernel package upgrades before the user actually installs Zenith.
P3 has a dedicated signed kernel coordinator ([kernel guide](docs/KERNEL-UPDATES.md));
preserve signature, UUID/artifact binding, previous retention, monotonic versions,
confirmation and crash/fallback tests. P1 remains scripts/data without boot-file writes.
Manual fallback and external ISO recovery are required; no automatic rollback or
arbitrary FAT/ext2 power-loss guarantee is provided. Native validation stays in PROGRESS.

User priority (2026-10-12): proper installed package delivery and progress usability
come before automatic distributor builds. The user named the HTTPS proxy
`https://zenith-stable.net.local`; it is a deployment setting, not a universal mirror
default. A matching self-signed certificate is now loaded in Zoraxy; the user
verified an installed Zenith HTTPS receipt and full signed package download. Keep
certificate/hostname checks and separate package-signing trust. See PACKAGE-SERVER.
Validation disks may be pruned with tools/prune-validation.py after tests stop;
preserve VMware recovery copies and the in-use local update relay.
