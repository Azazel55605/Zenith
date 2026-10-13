# Native text programs: first protected x86_64 slice

Implemented in the 0.0.8 source, with disposable-guest acceptance tracked in
[PROGRESS](PROGRESS.md). This begins M5; it does not complete the process, service
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

The package lives in `extra`, requires `zenith-native-abi 1.0`, declares `x86_64`,
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
There is no environment/cwd/file-descriptor ABI yet.

`int 0xfe` is the DPL3 entry. RAX selects the call; RDI/RSI/RDX carry arguments.
RAX holds the result; other general registers are preserved. Calls:

| Number | Operation | Bound |
|---|---|---|
| 1 | write(fd, address, bytes) | fd=1; at most 4096/call and 64 KiB total; readable user region required |
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
owners remain explicitly rooted until return; private frames are released on normal
exit, contained fault, timeout or managed setup failure. Installation, package/boot
mutation and shutdown are excluded by the shared maintenance gate. Cancellation
is checked before and after the bounded window, not a general signal mechanism.

The GDT/TSS intentionally remain kernel-owned after the first invocation. This is
needed because the firmware task descriptor is not a restorable application task.
There is no fork/exec family, wait/job control, general preemptive process scheduler,
filesystem/network syscall, IPC, graphical surface protocol, dynamic linking,
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
