# Native userland and unbundling

[Documentation](../README.md) · [Current status](../STATUS.md)

**State:** M5 in progress. Protected foreground execution and read-only tools are
locally verified. Bounded native input and file writes passed full local acceptance
at `2a515e8`; the operator reports the kernel/Sunbeam 0.3.0/manuals releases delivered
and a full system update successful, followed by kernel 0.0.17 installed and initially
working. Explicit native pipe/write/permission exercise on the installed VM remains
separate from package installation. Full process/service/GUI migration is unfinished.

## Implemented steps

- [x] Validate static x64 ELF/Zenith ABI and signed executable ownership/mode/digest.
- [x] Use private user address spaces, supervisor kernel mappings and NX/W^X rules.
- [x] Provide bounded arguments, output, identity, exit, fault/watchdog containment.
- [x] Preserve registers/FP state while yielding filesystem services to the managed
  caller's original kernel context; resume protected execution afterward.
- [x] Add caller-scoped read-only open/read/close/stat/cwd/directory iteration.
- [x] Validate full owned copyin/copyout ranges before I/O; reject stale/invalid handles.
- [x] Bound handles, requests, readback and CPU windows; clean up on exit/fault/cancel.
- [x] Build/package Sunbeam `cat`, `ls`, `pwd`, `stat` independently of the kernel.
- [x] Prove same-kernel executable upgrades, permissions and post-fault shell survival.
- [x] Implement finite shell stdin, bounded regular-file writes/create/truncate/append
  and flush, plus independent Sunbeam `tee` and stdin `cat`.
- [x] Complete expanded host/native and full pre-tag acceptance for native I/O.
- [x] Implement live standard streams, bounded pipelines and Sunbeam wc/head/tail.
- [x] Complete guest/common acceptance for streams at `cba1cbf`.
- [ ] Publish releases and complete operator-installed stream acceptance.

## Current boundary

The locally verified stream extension adds live stdout/stderr, canonical foreground
input and bounded byte pipelines with protected waiting contexts and serialized raw
BSP entry. Publication and operator acceptance are pending in PROGRESS. This is not a general
preemptive process scheduler. Raw TTY/jobs, seek, environment, sockets and GUI APIs
remain unimplemented. Managed/kernel code and existing builtins remain trusted.
The exact limits/wire layout belong to [Native programs](../NATIVE-PROGRAMS.md).
New native bundles require the filesystem and input/write capabilities:
upgrade and confirm a compatible kernel before updating Sunbeam.

## Next implementation steps

1. **Installed native acceptance:** publication and installed update are operator
   reported; explicitly verify boot confirmation and signed Sunbeam invocation as
   ordinary user and root.
2. **Installed input/write acceptance:** repeat representative pipe, write and
   permission checks on the operator VM after the server publishes the packages.
3. **Stream delivery, then raw TTY/process lifecycle:** finish operator acceptance
   before larger CLI ports; then controlled
   networking and graphics. [Migration plan](../USERLAND.md) owns the wider stages.

Each step needs its own active checklist and validation results in PROGRESS when
selected. These are planned steps, not silently started work.

## Source and acceptance

Owners: [managed process bridge](../../src/Core/Processes),
[x64 entry/return assembly](../../src/Native/x64/Process.s),
[Sunbeam](../../userland/sunbeam).
Run [native process acceptance](../../tools/native-process-smoke.py), host tests
and the relevant package/boot/login gates for a release. Keep ordinary-user,
symlink/ancestor denials, invalid copyout without offset advancement, stale handles,
FP state, timeout/fault/cancel and malicious-stack containment checks. Audit the
installed filesystem independently after clean poweroff.

`kernel/v0.0.20` at `431dfb9` passes clean-source full acceptance: 153 controller,
658 host, 71 login/session, 92 repository, full native-process coverage with all
64 byte pipelines plus desktop/local input, and 169 kernel-recovery checks.
The operator reports the distributor run completed successfully. Installed VMware
update/reboot/confirmation and representative stream/input checks remain unreported;
publication receipts were not independently inspected.

The server failure was runnable-ticket underflow: global pass froze and queued
producers starved. Real-dependency regressions cover admission/block/exit,
migration and priority accounting, alongside earlier wake/queue, GC-root, timed
condition and GUI EOF fixes. A separate stderr fixture correction tolerates known
Cosmos trace interruptions while still requiring exact error/output/status evidence.
Failed candidates, exact hashes and validation boundaries are archived in the
[server investigation](../archive/2026-10-18/native-pipeline-server-stall.md) and
[prior scheduler/GC record](../archive/2026-10-17/scheduler-late-wake.md).

Earlier full input/write acceptance at `2a515e8` is preserved in the
[input/write record](../archive/2026-10-16/native-input-and-writes.md); initial
stream implementation is in the [stream record](../archive/2026-10-17/native-streams.md).
