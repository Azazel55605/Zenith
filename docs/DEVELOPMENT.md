# Developing Zenith

## Toolchain

| Tool | Used for |
|---|---|
| .NET SDK 10 (system `dotnet`) | Building Zenith and running the unit tests |
| `Cosmos.Tools` + `Cosmos.Patcher` global tools, `cosmos install -y --tools` | The Cosmos build pipeline (ILC, patcher, clang/lld, xorriso) |
| `qemu-system-x86_64`, OVMF (`edk2-ovmf`), mtools, parted, e2fsprogs | Running, installing, smoke testing and host formatter validation |
| Python 3.10+ | `tools/smoke-test.py`, `tools/gen-fonts.py` (needs `fonttools`) |

## Everyday loop

```sh
dotnet publish -c Debug -r linux-x64 -o output-x64   # → output-x64/Zenith.iso   (VS Code: Build x64)
tools/run.sh live                                    # boot it                    (Run QEMU x64)
dotnet test tests/Zenith.Tests                       # host unit tests            (Unit Tests)
python3 -B tools/test_smoke_test.py                  # serial matcher regressions
tools/smoke-test.py                                  # boot test in QEMU          (Smoke Test (QEMU))
```

If the build says `cosmos.patcher: command not found`, add `~/.dotnet/tools` to `PATH`.

**What to test where:**
- **Unit tests** (`tests/Zenith.Tests`) compile the kernel's plain-.NET sources directly: the
  shell, parser, commands and terminal buffer. Anything under `src/Core/Shell` must stay free of
  Cosmos types for this to work; Cosmos-dependent commands live in `SystemCommands.cs`, which
  the kernel registers at boot.
- **Filesystem contract tests** link the proc/dev drivers, including the block
  byte-stream adapter and ext2 superblock profile gate, and download only the Cosmos HAL contract assemblies via `PackageDownload`. This exercises the actual driver
  interfaces on the host without importing Cosmos build targets or calling hardware.
  Formatter tests also require `e2fsck` (e2fsprogs) and check independently readable
  sparse scratch volumes from 1 MiB through 33 groups, including partial final
  groups, 512/1024-byte sectors and a descriptor table spanning two blocks.
- **The smoke test** boots the real ISO headless, types into the Terminal through the QEMU
  monitor, and checks the kernel log on the serial port. Commands report back with `logger`.
  Add a `(command, expected log text)` pair to `STEPS` in `tools/smoke-test.py` for new
  end-to-end behavior. The serial port is ASCII-only, so avoid box-drawing characters in
  expected text.

## Ext2 secondary volumes

`mount -t ext2 PARTITION DIRECTORY` explicitly selects Zenith's adaptation of the experimental Cosmos
ext2 driver. The local source and BSD license are recorded in
[src/Core/Storage/Ext2/SOURCE.md](../src/Core/Storage/Ext2/SOURCE.md).
The bitmap allocator fixes account for the first data block; `man licenses`
also carries the upstream notice in live and installed systems. `mount PARTITION DIRECTORY` still selects FAT. The mount
point must exist; already-mounted partitions and unsupported filesystem types
are rejected. Root discovery and installation still use FAT32.

The first supported profile is deliberately narrow: revision 1, 1 KiB blocks,
128-byte inodes, filetype-only features, and a clean superblock. The gate checks
these fields plus basic geometry before invoking the driver. It is **not fsck**
and does not establish consistency of arbitrary images. Field definitions follow
the [Linux filesystem superblock documentation](https://docs.kernel.org/filesystems/ext4/super.html).
Resize/deletion reclaim data and empty indirect tables, including sparse files;
freed inodes retain deletion time with zero links, and
partial-block tails are zeroed before later growth can expose them. Failed or
short writes clean unfinished pointer tables. Writes and size changes are limited
to 67,383,296 bytes at the supported block size (direct, single and double indirect
mapping); triple-indirect file I/O remains unsupported. Size changes to symlink
inodes are rejected, and deletion treats fast symlink targets as inline bytes.
Symlink resolution, ownership/mode changes, permission enforcement, directory
parent-link accounting and crash recovery still require validation before root migration.

The smoke harness creates a second disposable SATA disk with an MBR Linux
partition. `tools/ext2_fixture.py` formats only that scratch image using
`mke2fs -t ext2 -b 1024 -I 128 -O none,filetype`, with host-created seed files.
The 16 MiB volume has two block groups;
an 8 MiB filler exhausts the first group before guest allocation.
Guest checks cover reads, writes, case-sensitive names, rename/delete, indirect
blocks, truncation, sparse and double-indirect deletion, and unmount/remount.
Files window checks create a directory and file, edit/save, cancel deletion,
rename, reject overwrite, copy, move, and delete a file and empty folder.
Host `debugfs` also checks that truncated files have exactly one allocated data
block and no indirect tables, that the retained guest-created file has a data
block in the second group, and that the file manager's saved text persists.
After QEMU exits, the harness extracts the partition,
runs **check-only** `e2fsck -f -n`, and uses `debugfs` to verify persisted content.
Both guest checks and the independent host check must pass. `--keep DIR` retains
`ext2-check.log` and `ext2-disk.img` along with the serial log, final screenshot
and Files window screenshot (`files.ppm`).
The fixture requires `mke2fs`, `e2fsck` and `debugfs` from e2fsprogs; CI installs it.

A third disposable disk holds an initially blank ext2-target partition. Only the
guest formats it, via `mkfs.ext2`. The harness checks refusal without confirmation,
invalid labels, whole disks and mounted volumes; creates nested files; allocates
small files and checks contents after remount. The optional
`--large-ext2-stress --memory 1024 --step-timeout 180` profile additionally
allocates/deletes 9 MiB and requires a retained data block in group two.
The baseline retains its 512 MiB RAM and 60-second local command timeout;
CI uses its existing 180-second command timeout.
Host `e2fsck -f -n`, `debugfs` and `dumpe2fs` check the new volume, its label,
saved data (including second-group placement in the large profile) and untouched MBR/guard sectors outside the partition.
`--keep DIR` also retains `format-check.log` and `format-disk.img`.
The second boot uses a copy of the installed FAT scratch disk: it must select its
installed root, refuse formatting that disk's unmounted ESP and power off cleanly.
It retains `installed-serial.log` when requested. No real disks are formatted.
The large workload exhausted the Cosmos runtime at 512 MiB after completing the
write/delete cycle: only two pages remained, then a 65-page thread-stack allocation
faulted in `ThreadContext.Initialize`. A read-only snapshot was clean in e2fsck.
Allocator scratch reuse improves the workload but does not eliminate this runtime
heap limitation; its cause/low-memory failure handling still need investigation.
The formatter is currently limited to the profile above and 1..512 MiB partitions;
preflight rejects a final group too short for metadata. UUID remains unassigned;
this is not a secure wipe or a journal. Installer/root migration remains pending.

## Debugging a crash

Uncaught exceptions end on the panic screen. The same report goes to the serial port (`-serial
stdio` in `tools/run.sh`) and, on installed systems, to `/var/log/boot.log`. The previous
boot's log survives as `boot.log.1`, so after pressing R you can `cat /var/log/boot.log.1`.
`crash` triggers the panic path on purpose. For step debugging, use the "Debug x64 Kernel"
launch configuration (GDB via QEMU), described in `docs/cosmos/articles/user/debugging.md`.

## CI and releases

`.github/workflows/ci.yml` runs on every push and pull request:
1. host unit tests
2. ISO build with the Cosmos toolchain
3. the QEMU smoke test (KVM on GitHub's runners); logs and the final screenshot are uploaded
   as the `smoke-test-logs` artifact

Pushing a tag `v*` additionally creates a GitHub release with `Zenith.iso` and `Zenith.elf`:

```sh
git tag v0.2.0 && git push origin v0.2.0
```

## Working on Cosmos itself

Some roadmap items need changes inside Cosmos (its HAL and boot code are internal). The Cosmos
`gen3` source lives next to this project in `../Cosmos`.

**One-time setup** (already done on this machine):

```sh
git clone -b gen3 https://github.com/CosmosOS/Cosmos.git ../Cosmos
cd ../Cosmos && git submodule update --init --depth 1 --recursive   # dotnet/runtime (~1 GB) + lai
```

Cosmos pins a newer .NET SDK than Zenith uses (`global.json`: 10.0.201+). It's installed
user-locally in `~/.dotnet` and used only for Cosmos builds; the system `dotnet` stays the default.

**Build Cosmos packages** after changing Cosmos:

```sh
cd ../Cosmos
git checkout global.json   # the script rewrites the Cosmos.Sdk version in it
DOTNET_ROOT=~/.dotnet PATH=~/.dotnet:~/.dotnet/tools:$PATH ./.devcontainer/postCreateCommand.sh
```

The script packs every Cosmos package as `3.0.89.<yyyymmdd>` into `../Cosmos/artifacts/package/release`.
It also has machine-wide side effects:
- it clears `~/.nuget/packages/cosmos.*`
- it registers that folder as a user-level NuGet source named `local-packages`
- it replaces the global `Cosmos.Tools`/`Cosmos.Patcher` tools with the local builds

**Point Zenith at the local packages:**

```sh
tools/use-cosmos.sh local     # writes cosmos.local.props (git-ignored) with the local version
tools/use-cosmos.sh status
tools/use-cosmos.sh release   # back to nuget.org; CI always uses the release
```

**Contributing back:** fork `CosmosOS/Cosmos` on GitHub, add it as a remote in `../Cosmos`,
and open pull requests from topic branches. The roadmap's "Upstream work" section lists the
planned changes.
