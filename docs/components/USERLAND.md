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

`cba1cbf`: full common TCG stream acceptance passed across serial/desktop/local
hosts. [Stream implementation and acceptance](../archive/2026-10-17/native-streams.md).
The distributor's 0.0.18 stream gate exposed a pinned scheduler duplicate-wake
fault and an interior-root GC corruption. Expanded native acceptance also exposed
a GUI EOF handoff race; ordered line/EOF delivery and hidden-input checks are
implemented. The combined correction passes full clean-source TCG acceptance at `6544ffd`,
including 64 pipelines across GC, hidden GUI input/EOF and kernel recovery.
Source fixes and `kernel/v0.0.19` are pushed at tested `6544ffd`; distributor
native acceptance failed on the first repeated byte pipeline on 2026-10-18.
Exact server artifacts pass the full native gate locally. The distributor replay
now exposes ticket underflow and producer starvation; the real pinned policy
reproduces the accounting mechanism. An untagged runnable-admission fix is under
validation; its full local native gate passes in 656.737s. The operator reports
the rebuilt `b11f8f8` candidate also passes the focused native gate on the
distributor. Full clean-source release qualification remains pending. Failure-only controller CPU/thread snapshots are tested and
deployed; operator captures identify timed-condition halt call sites. Delivery and
operator installation of a working stream fix remain unverified.
[Server investigation](../archive/2026-10-18/native-pipeline-server-stall.md). See [investigation](../archive/2026-10-17/scheduler-late-wake.md).

`2a515e8`: full common TCG acceptance passed, including expanded native input/writes,
independent persistent contents/audit, repository and kernel recovery gates.
[Current slice evidence](../archive/2026-10-16/native-input-and-writes.md).
Earlier read-only foundation evidence remains in the
[2026-10-15 record](../archive/2026-10-15/PROGRESS.md#2026-10-15--native-filesystem-tool-unbundling-locally-verified).

Current scheduler candidate: the server snapshot has Ready workers but no Running
entry. A real-dependency fixture reproduces self-selection retaining Ready; the
copied dependency now restores Running without replacing its live stack. Thirteen
scheduler regressions pass. Candidate `a63ce7c` also passes the full native gate
locally (648.864s), 658 host, 134 controllers and all 40 adapter checks. Actual
distributor test still fails the first pipeline, with verified timed-condition
halt call sites. The state correction alone is insufficient. Condition waits that
resume after an unrelated interrupt and discarded timed-wait results are now
reproduced in dependency fixtures. Their fixes pass 48 adapters, 658 host and 134
controller checks plus the full local native gate (686.101s, Debian QEMU 10.0.13 /
TCG). The rebuilt server candidate still fails. Exact server kernel/disk replay,
including the earlier fault/cancel sequence, passes locally. A bounded typed-queue
observer and no-build disposable-fixture replay are implemented for the next server
capture. Root cause, distributor acceptance and full release qualification remain
pending; this is not a delivered kernel fix.

Release qualification on 2026-10-18: `431dfb9` passes clean-source full acceptance
(71 login/session, 92 repository, all 64 native pipelines plus GUI/local input and
169 kernel-recovery checks). `kernel/v0.0.20` targets that exact source and includes
the server-confirmed runnable-ticket fix and stricter trace-tolerant stderr fixture.
Distributor publication and installed stream acceptance remain pending.
