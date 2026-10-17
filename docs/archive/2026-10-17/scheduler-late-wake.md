# Duplicate scheduler wakes and native pipeline stalls

Recorded **2026-10-17**. Final guest stress and clean-source acceptance are pending;
this record does not establish server delivery or an installed replacement kernel.

## Failure and evidence

The distributor's kernel 0.0.18 native gate timed out at 120 seconds waiting for
`sunbeam --stream-bytes | sunbeam cat | sunbeam wc -c | logger` to report 163840.
The server recorded 3,678,601 serial bytes and 31.34 seconds of QEMU CPU time,
mostly monitor/condition/scheduler success traces. The initial unchanged local
native gate passed in 381.646 seconds; 40 cancel-then-pipeline repetitions also
passed in a focused run. One successful schedule did not establish stability.

Repeated local completion checks subsequently stalled. A paused disposable VM
provided a coherent queue snapshot: worker 78 appeared twice ahead of worker 82,
which remained Created. The latter's pass was lower than both duplicate entries,
so the ready queue was no longer correctly ordered. The saved local diagnostic
is `obj/distributor/stream-stall-snapshot.json` (generated, not source).

The pinned condition-variable implementation can retain an expired waiter until
that waiter resumes. A signal during that interval calls `ReadyThread` again.
`ReadyThread` unconditionally marks/enqueues it, and stride admission changes its
pass and increments tickets again. Mutating an already queued thread's pass also
invalidates the queue's ordering. The host regression exercises the actual pinned
timer-wake then `ConditionVariable.Signal` sequence, with only privileged IRQ/UART
operations stubbed in a separate host copy of the dependency. The original queues
the thread twice; the adaptation queues it once and preserves its pass/tickets.

Idle handoff and accumulated-quantum candidates did not resolve the guest stall
and were removed. The actual VM timer was verified as 10,000,000 ns, so the
sub-quantum hypothesis did not explain this failure. A temporary per-syscall
verbose tracing build faulted after heavy tracing/GC; it was discarded and is
not a qualified artifact.

## Correction

- Reject late Ready, Running and Dead wakeups inside the existing IRQ-masked
  `ReadyThread` scope. Early exits still execute its original IRQ restoration.
- Scan stride's existing queue by reference before admitting a Created thread
  again. Preserve its Created state, pass and ticket accounting. Do not use the
  pinned runtime's problematic generic equality helpers.
- Quiet only known successful monitor/condition/wake traces in build copies.
  Adapt the transplanted `Interop/Sys` methods in patched CoreLib as well as the
  source plug copy: changing the latter after SDK patching alone is ineffective.
  Retain synchronization, error paths, signature checks and drift refusal.
- Require both pipeline completion and an exact output line, repeat the byte
  pipeline 64 times, and report numeric outcomes plus the retained serial path
  on failure. Do not repeat operations or infer success from a prompt/substring.
- Retain a local QEMU monitor socket in native fixtures for subsequent diagnosis.

No upstream checkout, installed dependency package or shared NuGet cache is edited.
Kernel 0.0.18 and its existing tag remain immutable.

## Validation and delivery boundary

- 27 pinned dependency regressions passed: wake/queue behavior, real patched
  CoreLib trace adaptation, idempotence/drift refusal, TCP and IDT adapters.
- 121 controller checks and 654 Release host tests passed during investigation.
- Final-artifact guest stress and full clean-source acceptance remain pending.
- No replacement kernel tag, distributor publication or operator installation
  has been established by this investigation.

## Collector regression and remaining acceptance

The uninstrumented `f2f8a42` sixty-pipeline stress produced exact counts through
iteration 50, then faulted immediately after collection #1 freed 1,039,959 objects.
The fault was in `LowLevelMonitor_Release`: its handle entry pointer had been
incremented by one, so the dereferenced monitor pointer was shifted by eight bits.
A saved register held an interior reference to `ThreadWaitInfo._waitMonitor`.
The conservative scanner sent that address directly to `TryMarkRoot`, which
accepted the opaque handle as a candidate MethodTable and set its mark bit in the
field itself. A synthetic host heap executing the pinned collector reproduces
`ownerMarked=False;handleChanged=True` before adaptation.

The correction resolves register/stack candidates to owning object starts before
calling the existing mark routine. It walks from a containing segment's Start to
Bump, using sweep-compatible aligned object/free/filler strides; it does not use
stale brick-table hints. Both ordinary and pinned heaps are covered. Precise
roots, actual GC handles and reference enumeration retain their existing paths.
The same synthetic case becomes `ownerMarked=True;handleChanged=False`.
No GC disabling, handle masking, reduced fixture size or relaxed success criterion
is used. The older pinned `GetParentObject` is not used: its signature/brick-table
algorithm differs from the current upstream checkout.

32 dependency adapter tests pass, including five actual-collector regressions.
The scheduler-only common run passed login/session and repository gates, but
stalled on repeated native pipeline 2 after printing 163840 without completion.
That evidence remains a blocker; a combined-fix stress/full run is required.

A second deterministic queue regression covers a short sleep expiring while the
sleeper is still CurrentThread. The wake queues it; ScheduleFromInterrupt may
pick another worker and then yield the already queued current thread. Guarding
ReadyThread alone does not cover that insertion path. InsertByPass now removes
existing identity-matching entries before sorted reinsertion, without changing
ticket totals. The original/f2 path queues it twice; the combined adapter retains
one entry. Eight wake regressions pass, including this case and sorted reinsertion ahead
of an existing worker. In total 34 dependency adapters and 121 controller tests
pass; the combined Release ISO builds. Final exact-commit acceptance is pending.

The collector-corrected guest stress completed 80/80 byte pipelines with exact
163840-byte output, status zero and clean poweroff. Collection #1 freed 1,039,997
objects and pipelines continued afterward. This run predated the additional
wake-before-switch/requeue correction, so final combined common acceptance is
still required. The first combined acceptance was deliberately stopped after
host/build success to retain ARM64 compatibility in the new collector adapter;
that interrupted run is not a pass. The pinned ARM64 33-register scanner is
separately adapted/tested on a host-only dependency copy as well as x64's 15
registers. This is not a claim of ARM64 guest acceptance.
