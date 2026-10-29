# Zenith repository instructions

This is Zenith, a Cosmos Gen 3 NativeAOT kernel with a Unix-style system layer and
small graphical desktop. Read this file before changing the repository.
[AGENTS.md](AGENTS.md) and [CLAUDE.md](CLAUDE.md) both point here; maintain shared
guidance here so the two entry points stay consistent.

## Source of truth

- [docs/ROADMAP.md](docs/ROADMAP.md) is the **single work tracker**: whole-product
  requirements, current focus, work IDs, state, dependencies, exits and release/
  installed acceptance. Every accepted open task needs an owner there.
- [docs/README.md](docs/README.md) is the documentation map.
- [docs/components](docs/README.md#implementation-details) expand the same roadmap
  IDs with component implementation steps and source/contract links.
- Technical guides document current behavior, APIs and requirements; they must not
  acquire independent progress ledgers or unowned future task lists.
- [docs/PROGRESS.md](docs/PROGRESS.md) remains a compatibility pointer to the master
  active section. Do not append a second execution checklist there.
- [docs/MAINTAINING.md](docs/MAINTAINING.md) defines consolidation and validation.
  Detailed completed evidence belongs once in [docs/archive](docs/archive/README.md).
- [docs/cosmos](docs/cosmos/index.md) is pinned upstream reference, not the Zenith
  roadmap. Leave vendored documentation unchanged.

Update the master row and relevant component detail together. Separate source,
local tests, publication and installed observations. Remove superseded blockers;
never infer current failures from old release logs or claim a menu stub is a backend.

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
  Preserve ordered GUI type-ahead line/EOF delivery, including EOF while a line
  reply is pending; clear public input before and after hidden authentication.
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
  The CLI extension adds exported envp, an owner-filtered process table,
  cooperative PID cancellation, foreground raw-input leases and four bounded shell
  jobs. Preserve per-invocation stop callbacks and terminal restoration across all
  three hosts; background jobs own no terminal and block account/session changes.
  Native namespace/metadata/lstat/seek/copy calls retain caller VFS authorization:
  refuse same-inode copies before truncate, symlink recursion and mount-root removal.
  Independent zenith-base-tools/zenith-fs-tools dispatch keeps explicit recovery
  builtins; a present invalid executable must fail closed. Preserve native-cli-smoke
  and GUI/local raw keyboard checks; --terminal-only is a focused check, never a
  substitute for the default full native gate before release.
  The network-client extension owns invocation-local TCP/verified-TLS handles and
  atomic download sinks. Keep CLI/HTTP decoding outside the kernel and certificate,
  DNS/driver and VFS commit policy inside typed services; no shell-command callback.
  Preserve full-range request/buffer validation, captured-credential cleanup, strict
  framing/hostname checks and busy/fault/cancel/upgrade tests. Peer reads must not
  hold the native filesystem service gate; setup and mutations retain serialization.
  Graphical clients own fixed private surfaces and bounded events. Preserve
  session/UID admission, invocation handles, full pointer validation, opaque copies,
  UI-thread-only window mutation and exit/fault/cancel/session cleanup. The opt-in
  material extension keeps ABI 1 forced opaque and uses explicit format 2 plus
  606/607 negotiation for premultiplied foreground/host material regions. Preserve
  full-frame atomic validation, RGB<=alpha, opacity outside declared rectangles,
  8 nonoverlapping regions / legacy 131072 pixels / 24px corners, bounded host blur and
  no client backdrop readback. Keep native-materials-smoke and opaque client gates.
  System-wide material preferences use atomic revisioned 608/609 calls; legacy
  610/611 appearance saves preserve them. Version-2 616/617 adds whole-background
  preference bit 4; keep 608/609 snapshots/validation at their published masks and
  preserve the newer bit on legacy saves. Version-3 618/619 adds a contrast-clamped
  translucency level; schema 4 and legacy saves preserve it. Typed 620/621 exports
  bounded real keyboard/time-zone catalogues and root-only revisioned choices
  through SettingsService; keep CLI/native parity, read-only users, stale/error
  drafts and reboot tests. 625/626 extend those controls with registered default sessions without changing
  620/621. Captured-UID 622/623 owns private appearance/inheritance and root-only
  system defaults; 624 exports bounded read-only network/account information. Personal
  storage uses protected root-owned ancestry, never caller paths or USER/HOME. Keep
  two-account inheritance/reset, denied raw reads, malformed/stale/reboot tests.
  Chrome, taskbar and opted-in client regions
  share contrast/cache/work fallback without rewriting saved choices. Protected
  credentials remain solid. Explicit 613/614/615 viewport negotiation owns bounded
  resize/maximize; require exact dimensions/generation before changing the private
  frame stride. Resize-enabled surfaces may negotiate Content/background regions;
  legacy forced-XRGB is unchanged. Shared workspace clients require viewport ABI.
  Keep rapid wheel, drag-release keyboard, resize/restore/draft and large-frame
  pressure checks. Never expose
  framebuffer addresses or global input. Native-graphics-smoke joins all six
  existing pre-tag gates; Settings and compositor migration remain separate.
  Native-network-smoke remains a mandatory pre-tag gate. Configuration,
  trust import and clock changes remain trusted services, not arbitrary user calls.
  Preserve full-range copyin/copyout checks, identity/cwd, handle cleanup,
  user FP/register state, request/interrupt budgets and native permission/cancel tests.
  See docs/NATIVE-PROGRAMS.md for foreground ABI limits; preserve fault/NX/W^X,
  credentials and same-kernel executable-upgrade acceptance. Selected CLI packages are independent; full GUI/service migration still needs
  additional controlled APIs.
- Opt-in x64 `smp=native` starts isolated native integer workers (docs/MULTICORE.md).
  The x64 IDT loader must preserve IF: Cosmos 3.0.89's premature STI is redirected
  in signature-checked build copies. Keep installed UEFI startup/IRQ-state checks.
  Managed scheduling/GC, drivers and the foreground process bridge stay on the BSP.
  APs own static stacks/tables, never managed objects or UART, and currently poll
  when idle. Preserve default-disabled boots and 1/2/4-CPU acceptance; do not enable
  managed AP execution before per-CPU context and GC synchronization are implemented.
- GUI input must periodically drain registered mice before sampling coordinates,
  even if a virtio notification was missed. Keep polling IRQ-scoped on the BSP,
  restore prior flags on success/exception, bound virtio drains to 64 events and
  retain partial SYN reports across polls. The mouse bridge must fail closed if
  its copied-runtime adapter was omitted; keep real-dependency IRQ/budget tests.
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
  Preserve scheduler late-wake idempotence inside the IRQ scope: timeout followed
  by a signal must not duplicate ready entries or change an already queued pass
  or ticket total. A wake before switch-out followed by yield must reorder one
  existing entry, never duplicate it. Self-selection of a woken current thread
  must restore Ready to Running without replacing its live stack or requesting
  a native switch; preserve other-thread/Created entry flags. Conservative stack/register GC candidates
  must resolve to owning object starts before marking; preserve opaque monitor
  fields and ordinary/pinned object boundaries. Allocation entry points must retain
  IRQ state until their method table and array length are published; protecting
  only heap reservation exposes invalid objects to pressure collection. Preserve
  all six entry contracts, export ABI, exception restoration and drift rejection
  in the copied-runtime adapter. Keep real-dependency wake/GC regressions and repeated native
  pipeline completion checks. Condition waits must remain parked after unrelated
  interrupts until a signal or timeout actually readies the thread; repeated parks
  must preserve tickets and IRQ flags. Timed monitor interop must forward the actual
  timeout/signal Boolean. Monitor adaptations must cover transplanted CoreLib
  bodies, not only the copied source plug assembly. Stride ticket totals must track
  runnable admission independently of thread state: manager callbacks may already
  mark a thread Blocked/Dead. Block then exit, unadmitted exit, repeated park,
  policy handover, migration and sleeping priority changes must preserve exact
  accounting; an unsigned deficit freezes global pass and starves queued workers.
- Preserve unrelated local changes. Keep generated output, scratch disks and
  machine-specific package settings out of commits. Never format a real disk
  during validation; the smoke harness creates its own disposable disk.

Kernel updates must preserve signatures, exact root/artifact identity, previous
retention, monotonic versions, confirmation and interruption/fallback tests. Manual
previous-kernel and external-ISO recovery remain required; neither automatic rollback
nor arbitrary physical power-loss recovery is established.

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
for unsigned full release acceptance (always fresh, though independent gates run
concurrently within the host budget); `--existing` is only for known prebuilt
artifacts. CI uses this same native gate plan. Keep controller regressions in the
common `--controller-only` set so server pre-build checks and CI stay aligned.
Do not correct relative input from screenshot feedback: stale frames accumulate
deltas. Never replay clicks/application mutations or relax exact destinations.
Capture read-only running-guest diagnostics on acknowledgement failure. Tagged
helper imports use current controller policy; preserve stale-frame/frozen tests.
GUI fixture scripts crossing a native-client/Terminal boundary must observe
surface retirement and a focused public prompt; a fixed delay or client exit
status is not a shell-readiness acknowledgement.
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
see [USERLAND](docs/components/USERLAND.md). WASM is optional, not the assumed final architecture.
Customization/usability belong in proper desktop work. Home package hosting uses a dedicated VM with its own IP and existing
VPN/DNS remote access ([server plan](docs/PACKAGE-SERVER.md)); snapshot/trial boot/restore is a
requested investigation ([recovery plan](docs/UPDATE-RECOVERY.md)), not a Btrfs decision.

User direction after M2: retain the simple DE for testing, replace it during proper
userland/desktop work, and start packaging early. Shared settings/session and offline
package foundations and S1 serial headless are implemented. Keep state accurate in the master roadmap. Preserve no-GPU live/installed validation when changing boot or shell I/O;
the test DE remains the default when supported display hardware is present.

Deployment settings such as the home HTTPS proxy and signing trust are local
configuration, not universal defaults. Follow the package-server runbook. Validation
artifacts may be pruned only after their tests stop; preserve active/pinned evidence
and actual operator recovery copies. Current work/acceptance is in the master roadmap.
