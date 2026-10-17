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
  pipeline three times, and report numeric outcomes plus the retained serial path
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
