# Zenith repository instructions

This is Zenith, a Cosmos Gen 3 NativeAOT kernel with a Unix-style system layer and
small graphical desktop. Read this file before changing the repository.
[AGENTS.md](AGENTS.md) and [CLAUDE.md](CLAUDE.md) both point here; maintain shared
guidance here so the two entry points stay consistent.

## Source of truth

- [README.md](README.md): current behavior, commands, layout and installation.
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md): toolchain, validation and Cosmos builds.
- [docs/ROADMAP.md](docs/ROADMAP.md): milestone scope and exit criteria.
- [docs/TECHNICAL-NOTES.md](docs/TECHNICAL-NOTES.md): architecture and design background.
- [docs/PROGRESS.md](docs/PROGRESS.md): active work, evidence and next steps. Update
  it with each implementation slice; distinguish implemented from runtime verified.
- [docs/cosmos/index.md](docs/cosmos/index.md): vendored upstream documentation;
  [SOURCE.md](docs/cosmos/SOURCE.md) records its source revision.

## Architecture and conventions

- `Kernel.cs` initializes storage, settings, commands, fonts and the desktop;
  `Run()` ticks the desktop and halts until an interrupt.
- `src/Core` holds system behavior; `src/Gui` rendering and window management;
  `src/Apps` desktop windows. Keep UI concerns out of the core layer.
- Shell sources must remain host-testable without Cosmos. Cosmos-dependent
  commands belong in `src/Core/Shell/Commands/SystemCommands.cs` and register at boot.
- Storage currently uses FAT for installed roots and RAM-backed FAT for live
  root and `/tmp`; virtual `/proc` and `/dev` are mounted at boot. Experimental
  secondary ext2 volumes require explicit `mount -t ext2` and a supported-profile
  check. The locally adapted driver, upstream revision and allocator fix are
  documented in `src/Core/Storage/Ext2/SOURCE.md`; its BSD notice is retained in
  LICENSE and `man licenses`. Root migration remains pending.
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

M0 and M1 are complete per the roadmap. M2 (v0.3) adds Unix filesystem semantics:
virtual `/proc` and `/dev`, read-write ext2, an ext2 installer root, users and
permission enforcement, and check-only `fsck.ext2`. ext4 read-only is optional.
Implement reviewable slices in dependency order. Keep M2 open until its full
exit criterion is met: installed ext2, real users/permissions and truthful `ls -l`.
