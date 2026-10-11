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
  `DesktopSessionHost` owns fonts/canvas/windows. The current floating test DE is the
  only available host. `Run()` ticks the host and halts until an interrupt.
- Settings persistence belongs to `SettingsService`, shared by CLI and future GUI.
  Keep local /etc state separate from shipped package defaults. See
  [settings/session guide](docs/SETTINGS-SESSIONS.md).
- P0 packages are experimental data bundles with inspect/dry-run only; use the
  shared bounded parser/planner and [prototype guide](docs/PACKAGE-PROTOTYPE.md).
  Do not infer installation, trust or independent app loading from bundle inventory.
- `src/Core` holds system behavior; `src/Gui` rendering and window management;
  `src/Apps` desktop windows. Keep UI concerns out of the core layer.
- Shell sources must remain host-testable without Cosmos. Cosmos-dependent
  commands belong in `src/Core/Shell/Commands/SystemCommands.cs` and register at boot.
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
package foundations precede S1 serial headless. Keep their status accurate in PROGRESS.
