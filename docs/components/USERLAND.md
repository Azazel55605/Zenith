# Native userland and unbundling

[Documentation](../README.md) · [Current status](../STATUS.md)

**State:** M5 in progress. The operator confirms kernel 0.0.21 installed, boot-confirmed and its CLI checklist passed. The qualified 0.0.21 CLI release adds environment/process lifecycle, raw
input, bounded jobs, namespace/seek operations and independently packaged base/
filesystem commands. Local evidence and remaining extraction are tracked in
[Progress](../PROGRESS.md#cli-unbundling-and-application-foundations). Selected CLI extraction is fully qualified; installed network acceptance and
service/GUI migration remain unfinished.

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
- [x] Operator confirms kernel 0.0.20 installed and running; explicit individual
  stream/input exercises remain separate from installation.

- [x] Add exported environment, owner-filtered native processes/cancellation,
  raw terminal leases and bounded shell jobs/wait.
- [x] Add guarded namespace/metadata/seek/copy and independently packaged
  base/filesystem tools with signed normal-command dispatch and recovery builtins.
- [x] Qualify exact `5852867` through all common gates; distributor and installed
  operator acceptance is now confirmed by the operator. [VM handoff](../CLI-VM-TEST.md).

## Current boundary

The locally verified stream extension adds live stdout/stderr, canonical foreground
input and bounded byte pipelines with protected waiting contexts and serialized raw
BSP entry. Kernel 0.0.21 is operator-installed and its CLI checklist passed.
The text/traversal slice passes full qualification at `6615ecb`; the operator's
installed graphical-terminal checklist matches expected results. Exact versions
and boot confirmation were not shown in that screenshot.
This is not a general preemptive process scheduler. The qualified CLI release adds
raw input leases, bounded shell jobs, exported environment, native PID cancellation
and private seek/namespace operations. Owned TCP/verified-TLS handles and atomic download sinks now support independent
net-tools clients. Full qualification at `faecc72` passes all six native gates;
distributor delivery and installed network acceptance remain pending. Protected
graphics remains unimplemented. [Active CLI work](../PROGRESS.md#cli-unbundling-and-application-foundations)
owns current validation. Managed/kernel code and existing builtins remain trusted.
The exact limits/wire layout belong to [Native programs](../NATIVE-PROGRAMS.md).
New native bundles require the filesystem and input/write capabilities:
upgrade and confirm a compatible kernel before updating Sunbeam.

## Next implementation steps

1. **Accept the network clients:** selected command extraction is implemented and
   qualified. Run the [network VM checklist](../NETWORK-VM-TEST.md) after compatible
   kernel-first installation; retain explicit recovery commands.
2. **Preserve the service boundary:** shell evaluation/xargs, accounts/packages,
   network configuration, trust import and clock changes stay trusted. This slice
   is not POSIX compatibility or complete service migration.
3. **Graphical client and Settings:** only after the selected CLI milestone;
   define ownership/lifetime/service APIs and shared UI tokens/widgets before
   extracting the test desktop.

[Migration plan](../USERLAND.md) owns the wider stages. Each new slice needs its
own validation record; local tests are separate from distributor/installed evidence.

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
The operator reports the distributor run completed successfully and confirms
0.0.20 installed and running; explicit boot confirmation and representative
stream/input checks remain separately unreported;
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
