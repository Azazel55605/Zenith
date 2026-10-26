# Native programs and filesystem ABI

> Current protected foreground ABI, including read-only services and the native stream extension. Work steps and release boundary: [Native userland](components/USERLAND.md).

## On this page

- [Install and run](#install-and-run)
- [Executable contract](#executable-contract)
- [Privilege, lifecycle and limits](#privilege-lifecycle-and-limits)
- [Filesystem services](#filesystem-services)
- [Input and writable descriptors](#input-and-writable-descriptors)
- [Live streams and pipelines](#live-streams-and-pipelines)

Execution began in 0.0.8; 0.0.13 adds read-only filesystem services. Kernel 0.0.16
adds finite input and file writes, used by Sunbeam 0.3.0 through an explicit package
capability. The locally verified stream extension adds live standard descriptors and canonical
input through `zenith-native-stream-abi 1.0`; publication/operator acceptance are
tracked separately. Current
release/acceptance is in [Status](STATUS.md), detailed runs in the
[execution archive](archive/README.md). This begins M5; it does not complete the process, service
or desktop migration. Sunbeam is a freestanding executable built separately from
the kernel, installed through a signed package and executed in ring 3.

## Install and run

After upgrading to the new kernel, reboot and `boot confirm` before installing:

```text
pkg install sunbeam-tools
native run sunbeam
native run sunbeam echo hello Zenith
native run sunbeam basename /usr/share/doc/example.txt .txt
native run sunbeam dirname /usr/share/doc/example.txt
native run sunbeam id
```

The package lives in `extra`; current bundles require `zenith-native-abi 1.0`
and `zenith-native-fs-abi 1.0`; newly built bundles also require
`zenith-native-io-abi 1.0` and `zenith-native-stream-abi 1.0`. They declare `x86_64`,
and owns `/usr/share/zenith/native/sunbeam.elf` at 0755. Older kernels cannot satisfy
the capability dependency. Run verifies ownership, installed mode and SHA256 every
time. `native check FILE` only inspects ELF; it cannot grant execution permission.
Package upgrade replaces the ELF. The next invocation uses its new bytes without
replacing the loaded kernel. The existing shell commands remain recovery builtins.

## Executable contract

The loader accepts bounded, static ELF64 little-endian ET_EXEC for machine 62:
64-byte ELF header, 56-byte program headers, 1..16 entries, at most 256 KiB on disk.
PT_LOAD virtual addresses occupy 0x400000..0x800000, with 4096-byte aligned offsets
and addresses. Read-only, read/execute or read/write pages are accepted; writable
executable pages and overlapping mappings are refused. BSS starts zeroed. Entry
must lie in file-backed executable bytes. Dynamic/interpreter/TLS segments and
executable stacks are refused. PT_NOTE must contain one ZENITH NUL-terminated
name, type 1, four-byte little-endian ABI version 1. This is not the Linux ABI.

A private four-level page table maps new user frames, a 64 KiB non-executable user
stack ending at 0x7fff0000, and supervisor-only upper-half kernel/HHDM mappings.
No lower-half kernel mapping is inherited. At entry RSP addresses argc, argv,
argv terminator, exported envp and an empty auxiliary vector. Up to 32 arguments
including argv[0], each at most 1024 UTF-8 bytes and 8192 bytes in total, are allowed.
The filesystem services below add inherited cwd, canonical or piped stdin and private
descriptors. The unreleased process extension copies at most 64 exported `NAME=value`
entries, at most 8192 UTF-8 bytes including terminators. Non-exported shell locals
are excluded. Names use `[A-Za-z_][A-Za-z0-9_]*`; credentials never come from envp.

`int 0xfe` is the DPL3 entry. RAX selects the call; RDI/RSI/RDX carry arguments.
RAX holds the result; other general registers are preserved. Calls:

| Number | Operation | Bound |
|---|---|---|
| 1 | write(fd, address, bytes) | fd=1/2 live stdout/stderr; fd>=3 writable file service; readable user region required |
| 39 | process ID | Unique positive ID for this boot's invocations |
| 60 | exit(status) | Low eight bits become shell status |
| 102 | user ID | Snapshot of invoking credentials |
| 104 | group ID | Snapshot of invoking credentials |
| 400 | isatty(fd) | Standard descriptor: 1 terminal, 0 pipe/redirection, -9 closed/invalid |

Unknown calls return -38, invalid output pointers -14, invalid descriptor -9 and
output-limit errors -28. Standard output/error yield to the managed caller and become visible before exit. The freestanding startup/C wrappers are in
[userland/sunbeam](../userland/sunbeam). GCC is preferred for cross-building;
Clang/LLD can build the same source. No libc, .NET runtime or Linux loader is used. This ABI permits x87/SSE with a
clean initial environment; OSXSAVE/AVX are disabled during execution until extended
register-state isolation is implemented.

## Privilege, lifecycle and limits

The x64 assembly bridge restores segment selectors/bases and the caller's x87/SSE
floating-point environment, preserves original kernel selectors in a kernel-owned GDT,
adds user segments and a TSS with a permanent interrupt stack, and temporarily
installs raw interrupt handlers. Kernel-origin interrupts forward to original
handlers. User faults terminate with status 139 and a CPU vector; the shell resumes.
Code pages are read-only, data/stack pages are NX and kernel mappings are supervisor
only. CPU setup without NX, with LA57, incompatible IDT/GDT/IST layout, or migration
to another CPU is refused with status 95. ARM64 execution is not implemented.

Up to eight bounded invocation contexts can wait on managed streams on the BSP.
A shared entry gate serializes every raw user window on its original CPU. During its raw user
window normal managed scheduler/device handlers do not run; IRQs are acknowledged
and the timer supplies a budget of 1000 interrupts. A loop terminates with status
124. This is an interrupt budget, not a portable wall-clock duration. Managed
owners remain explicitly rooted across each user window and managed service return; private frames are released on normal
exit, contained fault, timeout or managed setup failure. Installation, package/boot
mutation and shutdown are excluded by the shared maintenance gate. Cancellation
is checked before and after each bounded window and between filesystem service
requests; this is cooperative cancellation, not a general signal mechanism.

The GDT/TSS intentionally remain kernel-owned after the first invocation. This is
needed because the firmware task descriptor is not a restorable application task.
The unreleased CLI extension adds bounded shell jobs/wait and owned cooperative
process cancellation. There is no fork/exec family or general preemptive process scheduler,
network syscall, IPC, graphical surface protocol, dynamic linking,
C# app runtime or on-system GCC/Python/Bash port. Before migrating net-tools,
fs-tools or desktop clients, add those interfaces and repeat permission/resource,
concurrency and recovery acceptance. This foreground bridge must not become a
shared-address-space plugin shortcut.

The architecture reference is the [Intel Software Developer Manuals](https://www.intel.com/content/www/us/en/developer/articles/technical/intel-sdm.html),
particularly privilege transitions, paging, interrupt frames and TSS descriptors.
Acceptance deliberately tests kernel-address writes, privileged CLI, write-to-code,
execute-from-data, invalid output pointers and infinite loops, then uses the shell
again. It upgrades the signed executable under the same kernel, checks ordinary
user identity, verifies the package DB and independently audits the ext2 volume.

The [multicore foundation](MULTICORE.md) adds opt-in kernel-owned integer workers
without scheduling this foreground process bridge on secondary CPUs.

## Filesystem services

Sunbeam 0.2.0 adds independently compiled `cat`, directory `ls`, `pwd` and `stat`:

```text
sunbeam cat /etc/hostname
sunbeam ls -l /home/user
cd /home/user
sunbeam pwd
sunbeam ls -a .
native run sunbeam cat /etc/hostname
sunbeam stat /etc/hostname
```

The `sunbeam` command only launches the signed executable through the existing
protected loader. File operations and formatting execute in the package; the
existing `cat`/`ls` shell builtins remain recovery commands and are not replaced.
This is a multicall native tool package, not a separately executing desktop.

Upgrade and confirm the new kernel first (`pkg kernel update`, reboot,
`boot confirm`), then `pkg refresh` and `pkg upgrade` (or
`pkg install sunbeam-tools` if absent). Newly built native packages require
`zenith-native-fs-abi 1.0` in addition to the original execution capability;
older installed ABI-1 executables remain supported. This explicit dependency
prevents installing the new tools on kernels lacking their filesystem interface.

### Service transition and wire contract

Filesystem requests return from the raw user interrupt window to the original
managed kernel stack, page table, interrupt table, segments and FP state. VFS/driver
code runs there under a captured invoking credential scope. Re-entry restores the
saved user interrupt frame, all general registers (except the result in RAX), and
x87/SSE state. User pointers are never dereferenced under the kernel's original
page table: copyin/copyout walks only owned user frames and validates the complete
range before I/O, including writable mappings for copyout. No kernel pointer or
managed object is exported. Noncanonical/high-half user stack pointers terminate
the process before IRET can fault in kernel mode; timer IRQs are acknowledged on
that exit path. Read-only code, unmapped gaps, kernel addresses and
wrapped ranges fail with -14 before descriptor offsets or directory cursors change.

Additional `int 0xfe` calls use the existing RAX/RDI/RSI/RDX convention. This is a
Zenith interface, not Linux syscall compatibility:

| Number | Operation | Arguments/result |
|---|---|---|
| 0 | read | fd, writable buffer, length; bytes or 0 at EOF |
| 2 | open | UTF-8 path, byte length, Zenith flags below; private fd >= 3 |
| 3 | close | fd; 0 or -9 for invalid/stale fd |
| 4 | stat | UTF-8 path, byte length, writable 24-byte metadata record; 0 |
| 74 | flush | writable file fd; 0 on success |
| 79 | cwd | writable buffer, capacity; bytes including trailing NUL |
| 217 | next directory entry | fd, writable buffer, capacity >= 280; 280 or 0 at end |

Paths contain 1..1024 UTF-8 bytes without NUL. Relative paths use a snapshot of the
invoking shell's cwd. This does not change the global working directory. The current
VFS rejects `..` components; native paths preserve that restriction rather than
lexically bypassing inaccessible ancestors or changing symlink semantics. VFS
symlink traversal and ancestor search permissions apply. Regular reads reject
special/device files; directory iteration requires read/search permissions, checked
again on each entry. Metadata follows the existing VFS stat contract.

Metadata uses little-endian size (u64), mode/uid/gid (three u32s) and reserved-zero
u32. A directory record adds a 256-byte UTF-8/NUL name. Directory handles expose a
bounded snapshot of at most 4096 entries; `ls` prints one entry per line in filesystem
order, supports `-a`/`-l`, and currently expects directory operands. Native pipe output preserves bytes; terminal output uses incremental UTF-8 decoding.
The unreleased terminal extension below adds a bounded foreground raw-input ABI. `cat` reads files or stdin in 4 KiB chunks and reports failures. Tools use the freestanding GCC/Clang startup/header, without libc.

Each invocation permits 16 live handles, 4 KiB per I/O request, 1 MiB accumulated file/directory readback,
4096 managed requests and the existing 1000-interrupt user execution budget.
Exhausted execution/request budgets return 124. Every 16 managed requests include a
normal scheduler sleep so console/cancellation workers can run; Cosmos 3.0.89's
`Thread.Yield` is a stub. Invalid descriptors return -9, denied operations -13,
missing paths -2, unsupported flags/encoding -22, handle exhaustion -24, readback
or write limit -28, cwd capacity -34 and oversized paths/names -36. Other VFS errors
return -5. The unreleased extension adds private regular-file seeks and bounded
shell jobs; process spawning from user code and managed AP scheduling remain later slices. Handles close on exit, fault, timeout and cancellation.

The expanded native executable gate checks real reads/listing/cwd/stat, ordinary
user and ancestor/symlink denial, copyout faults without offset advancement, FP
state across service returns, stale descriptors, resource/request limits, fault/
timeout/cancellation cleanup, same-kernel signed upgrades and independent ext2
integrity. Exact results and release boundaries are recorded in PROGRESS.

## Input and writable descriptors

Sunbeam 0.3.0 adds independently compiled `tee [-a] FILE`, `cat` without operands
and `cat -`. Install and confirm the kernel providing `zenith-native-io-abi 1.0`
before upgrading the tools. Existing ABI-1 archives remain usable; rebuilding a
native bundle adds the new dependency automatically.

```text
echo hello | sunbeam tee /home/user/note
echo next | sunbeam tee -a /home/user/note
sunbeam cat < /home/user/note
```

The original input/write extension consumed finite UTF-8 input. The stream
extension below replaces it with live byte descriptors. Closing a standard
descriptor invalidates it for that invocation. `tee` can leave a partially written
file if its output limit or another operation fails.

Open flags are Zenith-specific, not POSIX/Linux constants:

| Flag | Value | Behavior |
|---|---|---|
| Regular read | 0 | Existing read-only file descriptor |
| Directory | 1 | Existing directory snapshot |
| Write only | 2 | Required for all mutation flags |
| Create | 4 | Create a missing regular file with requested mode 0644, caller ownership and existing umask policy |
| Truncate | 8 | Clear an existing authorized regular file at open |
| Append | 16 | Seek to EOF before each write |

Combine mutation flags with write-only. Directory combinations, unknown bits and
append plus truncate return -22 before opening. Read on a write-only descriptor,
write on a read-only descriptor, or flush on a non-writable descriptor returns -9.
Writable opens reject final symlinks, devices and directories; ancestor traversal
uses guarded VFS permissions. Identity is pinned and checked before truncation.
Permission is checked at open and again for writes and explicit flushes.

Full source/destination memory ranges are validated before file effects or offset
changes. Copyin accepts owned read-only memory; copyout requires writable memory.
Each invocation permits 1 MiB of actual file writes, separate from the stdin and
regular/directory readback budgets. File writes cannot extend past 1 MiB, and
writable opens refuse larger files. Requests remain limited to 4 KiB. Short writes
return the actual byte count and consume only those bytes of budget.

Flush uses syscall 74. Closing a writable handle also flushes it. Teardown attempts
to flush and close every handle on exit, fault, timeout and cancellation, continuing
if one handle fails. This is resource cleanup, not a file transaction: completed
writes are not rolled back and errors during teardown cannot promise durability.
Append is not atomic against concurrent trusted kernel writers. The existing
filesystem and clean-shutdown guarantees still apply.

The native gate covers pipe/redirection EOF, create/truncate/append, invalid ranges
without offset advancement, size limits, final-symlink/device refusals, caller
ownership/permissions, fault/cancellation cleanup and independent on-disk contents.
Release and operator acceptance are tracked separately in PROGRESS and STATUS.

## Live streams and pipelines

New native bundles declare `zenith-native-stream-abi 1.0`. Install and confirm the
compatible kernel before upgrading Sunbeam. Previous ABI-1 binaries remain usable.
This slice adds independently compiled `wc [-lwc] [FILE]`, `head [-n N] [FILE]`
and `tail [-n N] [FILE]`; omit FILE or use `-` for stdin. Each accepts one operand.
`wc` counts newlines, ASCII-whitespace words and bytes (not Unicode characters).
Head/tail default to 10 lines and accept N=0..1024. Tail retains at most 64 KiB;
an oversized retained window fails explicitly instead of silently truncating it.

```text
sunbeam cat
# Type a line and Enter; Ctrl+D on an empty line ends input; Ctrl+C cancels.
printf 'one two\nthree\n' | sunbeam wc
sunbeam cat /home/user/note | sunbeam head -n 5
sunbeam tail -n 10 /home/user/note
```

- fd 0 reads a pipe/redirection or the invoking host's canonical line input.
  Enter supplies UTF-8 bytes plus LF; empty-line Ctrl+D gives sticky EOF.
  Local console, desktop terminal and serial fallback use their existing input
  routing. The existing serial transport remains ASCII; UTF-8 byte preservation
  is verified through pipes/files and terminal decoding separately. The unreleased extension below adds raw keys and cooperative Ctrl+C handling;
  POSIX termios, PTYs and Unix signal delivery remain absent.
- fd 1 streams live to the selected stdout; fd 2 goes directly to the terminal,
  outside the stdout pipe/redirection. Invalid UTF-8 at a text terminal uses the
  decoder's replacement policy; native byte pipes/redirections preserve all bytes.
- A shell pipe holds at most 8192 bytes and applies backpressure. Producer stages
  run in child shells; the last stage retains the parent's state (`read NAME`).
  The shell joins all stages, returning the last status. An early reader exit
  closes its input; the native producer receives -32 and Sunbeam exits 141 quietly.
  Ctrl+C cancels the entire foreground pipeline and returns 130. Native stream
  cancellation uses internal -125/normal cleanup, avoiding simultaneous managed
  exceptions in Cosmos; pipe/redirect text output is discarded after cancellation.
- One shell tree admits at most eight total stage workers including the caller;
  nested pipelines share that budget. At most eight native contexts exist globally.
  Credentials, private mappings/registers/FP state, request budgets and roots belong
  to each invocation; raw assembly entry remains serialized. Expected setup/VFS
  errors are converted under a separate managed-service gate; standard-stream waits
  never hold it. This is cooperative
  BSP stream scheduling, not a general process scheduler or managed multicore.
- Each invocation allows 4 KiB requests, 1 MiB stdin and 1 MiB combined stdout/stderr,
  separate from file read/write budgets. At the exact input cap a one-byte probe
  distinguishes EOF from overflow; overflow returns -28 without exposing the extra
  byte. A closed/invalid standard fd returns -9; zero-byte reads do not prompt.
- Buffered shell builtins still collect text up to 1 MiB. File redirections commit
  after the command with a 1 MiB buffer, while native `tee` writes incrementally.
  These compatibility paths are bounded, not fully streaming file transactions.

The host stream tests cover wraparound, bytes/UTF-8, backpressure, EOF, closure,
canonical input and cancellation. The native gate checks live pre-exit output,
interactive EOF/cancel, multiple protected pipeline stages, early reader exit,
stderr separation, file redirection, FP/fault/permission cleanup and installed
same-kernel upgrades. Host utility tests validate algorithms, not the guest ABI.


## CLI process, terminal and namespace extension

Kernel 0.0.20 does not provide these calls. Packages declaring
`zenith-native-process-abi`, `zenith-native-terminal-abi` or
`zenith-native-path-abi` 1.0 require kernel 0.0.21 or a later compatible kernel.
The exact 0.0.21 candidate passes full local qualification; distributor and
installed acceptance remain pending. See the [VM handoff](CLI-VM-TEST.md).

| Call | Arguments (RDI, RSI, RDX) | Result |
|---|---|---|
| 401 | standard fd, unused, unused | Terminal input mode or negative error |
| 402 | standard fd, flags, unused | Set owned foreground fd 0 mode; zero on success |
| 403 | writable buffer, capacity 0..4096, offset 0..8192 | Chunk of per-invocation process snapshot; own UID only, root sees all |
| 404 | positive PID, 0, 0 | Request cooperative cancellation; owner/root only |
| 405 | request pointer, 56, 0 | Controlled namespace/metadata operation |
| 406 | private file fd, signed offset, origin 0/1/2 | New position; invalid seek fails without advancing |
| 407 | readable source fd, destination path pointer, path bytes | Open/create copy target after same-inode check, before truncate |
| 408 | path pointer, path bytes, writable 24-byte stat | Metadata without following the final symlink |

401/402 require an owned terminal input lease on fd 0. Pipes/redirections and
background jobs return -25; closed/invalid descriptors return -9. Default mode 7
is canonical+echo+Ctrl+C. Raw modes 0, 2, 4, 6 select echo bit 2 and Ctrl+C bit 4.
Other canonical combinations are refused. In raw mode Ctrl+D is byte 4, not EOF;
Ctrl+C is byte 3 unless signal handling is selected. Arrows/Home/End/Delete use
VT key sequences. The 4096-byte raw queue reports overflow (-75); closing stdin,
exit, fault, watchdog and cancellation restore mode 7 and discard raw type-ahead.
This does not provide full VT screen rendering or a PTY.

405's little-endian request has operation u32, zero flags u32, then six u64 fields:
path pointer, path byte count, target pointer, target byte count, value, extra.
Operations: 1 mkdir (value=mode), 2 unlink, 3 rmdir, 4 rename, 5 chmod (value=mode),
6 chown (value=UID, extra=GID or UINT64_MAX to preserve), 7 symlink (literal target),
8 check recursive-copy destination, 9 check recursive removal root, 10 touch mtime.
Modes are 0..0xfff, IDs fit uint32, paths are 1..1024 non-NUL UTF-8 bytes. Only
4/7/8 use target fields; all other unused fields must be zero. Complete ranges and
headers are validated before side effects. Cwd/identity are captured at invocation;
VFS search, parent/sticky-bit, ownership and readonly/mount policy remain enforced.

The process table admits at most 8 active invocations and retains 32 exit records.
PIDs do not repeat within a boot. Process snapshots contain name/UID/state/status,
not arguments, environment or credentials. Stop is idempotent and cooperative;
callbacks retire with the invocation, so a stale PID cannot cancel the next command.

Shell `COMMAND &` copies cwd/variables, uses EOF stdin, and owns no terminal.
At most four background jobs and eight total shell pipeline workers are admitted.
Use `jobs`, `wait [%JOB]`, `kill %JOB` or owner-authorized `kill PID`. `$!` is the
last job ID, not a PID. Nested background jobs are refused. Account/session changes
and interactive exit refuse active jobs; closing a terminal cancels its jobs.
`ps` lists protected native invocations; `jobs` also covers trusted shell workloads.

[Implementation and validation record](archive/2026-10-26/cli-process-foundation.md).

## Native text and traversal packages

The qualified text/traversal slice adds nine base filters and three filesystem
traversal commands using the existing CLI ABI. [Supported syntax and limits](archive/2026-10-26/native-text-traversal.md#supported-utility-profile)
and the [VM checklist](CLI-TEXT-VM-TEST.md) distinguish its native profile from
GNU tools and the managed recovery implementations. Owned network clients remain
a later service-interface slice.
