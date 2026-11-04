# Installed CPU diagnostics: native return selectors and suspended UI roots

## Operator evidence

The 0.0.44 distributor build succeeded. On a four-vCPU VMware installation,
`cpu test 10000000` and `cpu managed-test` initially halted the VM. The other
suggested CPU/image/blur checks worked. VMware logged CLI/HLT on vCPU 0, not a
nearby triple fault. The operator enabled COM1 capture in
`obj/vmware-cpu-044.log` and repeated the tests.

The native-return failure is a #GP at `0xffffffff8001d0a5`. The served 0.0.44 ELF
has SHA256 `341730572a8c758f3149002977d955c84b34d723091b6a02a18b0360b1e7c58f`;
that address is `z_resume_user`'s final IRETQ. The package was downloaded for
read-only symbolication over the home self-signed HTTPS endpoint; TLS chain
verification was unavailable, so this is not independent release-trust evidence.

A copy of the earlier disposable installed graphics fixture reproduces the same
#GP under QEMU TCG with Polaris active: `obj/polaris-cpu-repro/desktop/serial.log`.
Read-only monitor capture (`return-frame.txt`) records user RIP `0x40de7f`, CS
`0x2b`, RFLAGS `0x246`, RSP `0x7ffefde8`, SS `0x33`. In the rebuilt GDT, slots
0x28/0x30 are kernel code/data; user code/data have moved to 0x48/0x50. The
suspended frame still contains the old RPL-3 selectors. The private bridge copies
the enlarged kernel GDT but previously failed to rebind its saved CS/SS.

With Polaris closed, the operator reports the ordinary CPU test passes in the
built-in recovery terminal. `managed-test` still fails. Its new serial capture
shows twelve collections, followed by a terminal drawing exception and #PF at
`ExceptionHelper.ThrowExceptionWithContext+0x6f` (CR2=0x30). The stack goes through
`StringBuilder.ToString`, `TerminalWindow.DrawCells` and the desktop owner. An
invalid/cleared exception method table causes the secondary reporting fault.

## Corrections

The resume path stamps only its copied frame's CS/SS with the current private
bridge user descriptors before IRET. It preserves invocation RIP, flags, stack,
registers and FP state. All seven generated AP bridges inherit the same audited
change; their source hash is updated. A host regression executes the actual
copy/rebind instructions for all eight bridges with deliberately stale selectors.

Separately, pinned `ScanThreadStack` omits a suspended BSP boot/idle stack when
StackBase/StackSize are zero. The desktop runs on that stack; a collecting worker
therefore loses UI-only stack roots. A copied-runtime adapter now scans the saved
boot-thread SP through the recorded BootStack.Top, bounded by BootStack.Size.
It applies only to suspended CPU-0/thread-0 with no allocated stack, keeps the
existing saved-register scan, and fail-stops on invalid bounds before reading.
Running/current, other BSP and AP threads retain their existing paths. The x64
C3 adapter does not modify the separate ARM64 runtime or installed packages.

Host tests execute the adapted helper against the actual pinned assembly,
covering valid bounds, other/current/AP owners, out-of-range and unaligned SP,
idempotence and rejection of a missing call on reapplication. The mandatory
four-core graphical gate now runs both CPU diagnostics with Polaris alive and
requires another native round trip afterward. It exposed one further diagnostic
admission defect: a live protected application can temporarily reserve an AP,
so an enqueue returns zero. The cancellation-checkpoint loop had not checked
those refused tokens. Native/managed diagnostic batches now retry only token-zero
admission within the existing bounded wait; accepted operations are never
replayed. Production queues keep their nonblocking busy/fallback behavior. An
executable host test uses the actual helper to verify zero/zero/success, immediate
success and bounded refusal, including one acceptance and unchanged arguments.

## Acceptance boundary

The selector-only candidate passed 106 native CPU checks and all 72 graphical
checks, including Polaris/native+managed CPU commands and independent ext2 audit.
The combined candidate ELF is
`20dfb502d87d6d23733f217563c070f111ff6831ab588998fd7affd49713bedb`.
All 498 controller/runtime tests pass (`obj/vmware-cpu-controller-final.log`),
including real copied-runtime scanner tests and executable bridge/admission tests.
The full C3 suite passes at 1/2/4 CPUs, including three successive command-worker
collections with live recovery Terminal at 2/4 CPUs
(`obj/vmware-cpu-managed-final.log`). All 106 native CPU checks pass on the
combined candidate (`obj/vmware-cpu-native-final.log`). All 72 graphical checks
pass in 242.454 seconds, including both Polaris CPU diagnostics, subsequent
native round trips, clean shutdown and independent ext2 audit
(`obj/vmware-cpu-graphics-final.log`). Release x64 publication succeeds. The full
clean-source release matrix remains required before creating a new kernel tag.
The VMware managed failure remains an installed acceptance gap until the operator
repeats it on a qualified combined candidate. Do not equate local QEMU evidence
with VMware acceptance. Q2/H1/C4 in the master roadmap own this follow-up.
