# Native userland and unbundling

[Documentation](../README.md) · [Current status](../STATUS.md)

**State:** M5 in progress. Protected foreground execution and read-only tools are
locally verified; the operator has Sunbeam 0.2.0 installed. Bounded native input and
file writes are implemented in the current candidate, with expanded acceptance
running. Full process/service/GUI migration is unfinished.

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
- [ ] Complete expanded host/native and full pre-tag acceptance for native I/O.

## Current boundary

This is a single foreground process bridge on the BSP, not a general preemptive
process scheduler. Input/output are bounded buffered UTF-8 text. There is no
interactive TTY, seek, streaming byte pipeline, environment, sockets, GUI or Linux ABI.
Managed/kernel code and existing builtins remain trusted. The ordinary builtins
stay available for recovery; `sunbeam` launches the signed independent package.

The exact limits/wire layout belong to [Native programs](../NATIVE-PROGRAMS.md).
New native bundles require the filesystem and input/write capabilities:
upgrade and confirm a compatible kernel before updating Sunbeam.

## Next implementation steps

1. **Release acceptance:** all distributor gates, installed kernel confirmation and
   signed Sunbeam invocation as ordinary user and root.
2. **Input/write acceptance:** verify caller permissions, complete range checks,
   limits, fault/cancel cleanup and persistent changes through independent tools.
3. **Streams/TTY/process lifecycle:** define before larger CLI ports; then controlled
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

`f502085`: 620 host tests, Release build and final expanded opt-in two-CPU native
gate passed (297.571 s). [Detailed evidence](../archive/2026-10-15/PROGRESS.md#2026-10-15--native-filesystem-tool-unbundling-locally-verified).
Full kernel-update/repository suites were not rerun locally for this slice;
mandatory server gates and operator acceptance remain open.
