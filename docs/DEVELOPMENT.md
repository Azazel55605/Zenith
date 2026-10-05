# Developing Zenith

## Toolchain

| Tool | Used for |
|---|---|
| .NET SDK 10 (system `dotnet`) | Building Zenith and running the unit tests |
| `Cosmos.Tools` + `Cosmos.Patcher` global tools, `cosmos install -y --tools` | The Cosmos build pipeline (ILC, patcher, clang/lld, xorriso) |
| `qemu-system-x86_64`, OVMF (`edk2-ovmf`), mtools, parted | Running, installing, smoke testing |
| Python 3 | `tools/smoke-test.py`, `tools/gen-fonts.py` (needs `fonttools`) |

## Everyday loop

```sh
dotnet publish -c Debug -r linux-x64 -o output-x64   # → output-x64/Zenith.iso   (VS Code: Build x64)
tools/run.sh live                                    # boot it                    (Run QEMU x64)
dotnet test tests/Zenith.Tests                       # host unit tests            (Unit Tests)
tools/smoke-test.py                                  # boot test in QEMU          (Smoke Test (QEMU))
```

If the build says `cosmos.patcher: command not found`, add `~/.dotnet/tools` to `PATH`.

**What to test where:**
- **Unit tests** (`tests/Zenith.Tests`) compile the kernel's plain-.NET sources directly: the
  shell, parser, commands and terminal buffer. Anything under `src/Core/Shell` must stay free of
  Cosmos types for this to work; Cosmos-dependent commands live in `SystemCommands.cs`, which
  the kernel registers at boot.
- **The smoke test** boots the real ISO headless, types into the Terminal through the QEMU
  monitor, and checks the kernel log on the serial port. Commands report back with `logger`.
  Add a `(command, expected log text)` pair to `STEPS` in `tools/smoke-test.py` for new
  end-to-end behavior. The serial port is ASCII-only, so avoid box-drawing characters in
  expected text.

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
