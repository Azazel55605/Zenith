# Zenith repository instructions

This is Zenith, a Cosmos Gen 3 NativeAOT kernel with a Unix-style system layer and
small graphical desktop. Read this file before changing the repository.
[AGENTS.md](AGENTS.md) and [CLAUDE.md](CLAUDE.md) both point here; maintain shared
guidance here so the two entry points stay consistent.

## Source of truth

- [docs/README.md](docs/README.md): documentation hub and status vocabulary.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): current architecture and limits.
- [docs/SYSTEM-PLAN.md](docs/SYSTEM-PLAN.md): requested system/session/app direction and proposed slices.
- [docs/PACKAGE-MANAGER.md](docs/PACKAGE-MANAGER.md): update design proposals and unresolved decisions.
- [docs/HISTORY.md](docs/HISTORY.md): chronological change map; detailed evidence stays in PROGRESS.

- [README.md](README.md): current behavior, commands, layout and installation.
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md): toolchain, validation and Cosmos builds.
- [docs/ROADMAP.md](docs/ROADMAP.md): milestone scope and exit criteria.
- [docs/TECHNICAL-NOTES.md](docs/TECHNICAL-NOTES.md): historical measurements and engineering background.
- [docs/PROGRESS.md](docs/PROGRESS.md): active work, evidence and next steps. Update
  it with each implementation slice; distinguish implemented from runtime verified.
- [docs/cosmos/index.md](docs/cosmos/index.md): vendored upstream documentation;
  [SOURCE.md](docs/cosmos/SOURCE.md) records its source revision.

## Architecture and conventions

- `Kernel.cs` composes shared `SystemRuntime` initialization and `SessionRuntime`;
  `DesktopSessionHost` owns fonts/canvas/windows; x64 `HeadlessSessionHost` owns a
  bounded COM1 shell. `OnBoot` bypasses Cosmos's graphical KernelConsole bootstrap.
  A missing supported display falls back to serial; WM remains unavailable.
  `Run()` ticks the selected host and halts until an interrupt.
- Settings persistence belongs to `SettingsService`, shared by CLI and future GUI.
  Keep local /etc state separate from shipped package defaults. See
  [settings/session guide](docs/SETTINGS-SESSIONS.md).
- P0 directory bundles remain unsigned inspect/dry-run experiments. P1 signed .zpk
  scripts/data packages have an ext2 installed-root transaction/ownership engine;
  see [PACKAGES](docs/PACKAGES.md). Preserve signature, dependency/conflict, local-edit,
  fault/cancel and installed-reboot recovery tests. /etc merges, signed online indexes,
  native execution and boot updates are separate gates. User selected scripts/data
  first, then unbundle compiled tools; grouped inventory is not independent executables.
- `src/Core` holds system behavior; `src/Gui` rendering and window management;
  `src/Apps` desktop windows. Keep UI concerns out of the core layer.
- M3 has a primary IPv4 service and diagnostic/NTP/HTTP/verified HTTPS commands; see
  [NETWORKING](docs/NETWORKING.md). `Zenith.Network` contains the pure bounded TCP
  buffer injected into build copies of Cosmos by the signature-checked PatcherFixup.
  Keep native fault/cancel/link-loss tests: Cosmos 3.0.89 skips unwind-finally and
  misclassifies typed catches. TLS uses the checksum-pinned offline Mbed TLS source archive and a fixed-memory
  freestanding bridge. Keep certificate/record/cancel/missing-entropy guest checks.
  Home DNS is 10.0.10.3; the package VM is not provisioned. Actual VM/VPN acceptance
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
  LICENSE and `man licenses`. `fsck.ext2` checks unmounted supported volumes without repairs. Installed ext2 accounts and permission enforcement are documented in
  [ACCOUNTS.md](docs/ACCOUNTS.md); kernel code remains trusted.
- Follow existing C# conventions: file-scoped namespaces, explicit visibility,
  four-space indentation, and braces on separate lines.
- Cosmos packages default to 3.0.89 (`global.json`, `Directory.Build.props`).
  `tools/use-cosmos.sh` switches local/released packages. Inspect actual installed
  APIs before building drivers; do not silently change package versions.
- `../Cosmos` is a separate upstream checkout. Treat it as reference unless the
  task needs an explicit upstream change. Do not run its bootstrap casually:
  it clears Cosmos NuGet caches and replaces global tools.
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
format/runtime/boot-recovery choices without recording a decision. All current
compiled apps are kernel-linked. Offline package research can precede networking;
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
