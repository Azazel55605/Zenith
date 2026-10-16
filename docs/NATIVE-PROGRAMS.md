# Native programs and filesystem ABI

> Current protected foreground ABI, including read-only services and the native input/write extension. Work steps and release boundary: [Native userland](components/USERLAND.md).

## On this page

- [Install and run](#install-and-run)
- [Executable contract](#executable-contract)
- [Privilege, lifecycle and limits](#privilege-lifecycle-and-limits)
- [Filesystem services](#filesystem-services)
- [Input and writable descriptors](#input-and-writable-descriptors)

Execution began in 0.0.8; 0.0.13 adds read-only filesystem services. Kernel 0.0.16
adds finite input and file writes, used by Sunbeam 0.3.0 through an explicit package
capability. Current
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
`zenith-native-io-abi 1.0`. They declare `x86_64`,
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
argv terminator, empty envp and an empty auxiliary vector. Up to 32 arguments
including argv[0], each at most 1024 UTF-8 bytes and 8192 bytes in total, are allowed.
The filesystem services below add inherited cwd, a finite stdin snapshot and private
descriptors. Environment variables are not exposed.

`int 0xfe` is the DPL3 entry. RAX selects the call; RDI/RSI/RDX carry arguments.
RAX holds the result; other general registers are preserved. Calls:

| Number | Operation | Bound |
|---|---|---|
| 1 | write(fd, address, bytes) | fd=1 buffered output; fd>=3 writable file service; readable user region required |
| 39 | process ID | Unique positive ID for this boot's invocations |
| 60 | exit(status) | Low eight bits become shell status |
| 102 | user ID | Snapshot of invoking credentials |
| 104 | group ID | Snapshot of invoking credentials |

Unknown calls return -38, invalid output pointers -14, invalid descriptor -9 and
output-limit errors -28. Output is buffered and returned to the invoking shell
when the process exits. The freestanding startup/C wrappers are in
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

This is one bounded foreground process on its original CPU. During its raw user
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
There is no fork/exec family, wait/job control, general preemptive process scheduler,
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
order, supports `-a`/`-l`, and currently expects directory operands. Output remains
buffered UTF-8 text, capped at 64 KiB per invocation; this is not a binary stream or
raw-terminal ABI. `cat` reads files or stdin in 4 KiB chunks and reports failures. Tools use the freestanding GCC/Clang startup/header, without libc.

Each invocation permits 16 live handles, 4 KiB per I/O request, 1 MiB accumulated file/directory readback,
4096 managed requests and the existing 1000-interrupt user execution budget.
Exhausted execution/request budgets return 124. Every 16 managed requests include a
normal scheduler sleep so console/cancellation workers can run; Cosmos 3.0.89's
`Thread.Yield` is a stub. Invalid descriptors return -9, denied operations -13,
missing paths -2, unsupported flags/encoding -22, handle exhaustion -24, readback
or write limit -28, cwd capacity -34 and oversized paths/names -36. Other VFS errors
return -5. Seek, process spawning, streaming byte pipes and scheduling on managed
APs remain later slices. Handles close on exit, fault, timeout and cancellation.

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

Stdin (fd 0) is a private, finite UTF-8 snapshot of the shell's buffered pipe or
redirected input, limited to 1 MiB. Without input, reads return EOF. Closing fd 0
invalidates it for that invocation. This does not introduce interactive keyboard
reads, a raw TTY or streaming binary pipes. Stdout remains buffered text with a
64 KiB limit and 4 KiB per call; stderr is not exposed. `tee` can therefore leave a
partially written file if its output limit or another operation fails.

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
