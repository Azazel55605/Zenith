# Native pipeline scheduler, collector and EOF fixes

Recorded **2026-10-17**. Full local acceptance passes at `6544ffd`.
Source/replacement-tag publication, distributor delivery and operator installation
remain pending. Earlier attempts below are historical and superseded.

## Final combined acceptance

Exact clean source: `6544ffdb54250de5f511a50f2d1f1903c37416c1`.
Command (normal SDK/tool PATH, TCG on the local x64 host):

```sh
ZENITH_VALIDATION_ACCELERATOR=tcg python3 -B deploy/distributor/validate.py \
  --keep /var/tmp/zn-qualified-runtime-20261017
```

| Gate | Result | Wall time |
| --- | --- | --- |
| Controller regressions | 121 passed | recorded in controller log |
| Host tests | 658 passed | 8.609s |
| Release ISO/ELF build | passed | 24.015s |
| Package tool | passed | 1.201s |
| Installed login/session/styling | 71 passed | 199.641s |
| Signed online repositories | 92 passed | 321.410s |
| Native programs and all terminal hosts | passed | 675.231s |
| Kernel activation, cuts, rollback/fallback, audits | 169/169 passed | 473.820s |
| Actual pinned dependency adapters | 35 passed | 18.391s |

All 64 repeated pipelines produce exactly 163840 bytes and exit zero. GC
collection #1 frees 1,155,347 objects and execution continues. Floating-point
streaming, independent binary/file-content and ext2 audits, GUI hidden password
change, normal/immediate GUI EOF, cancellation and local-console checks pass.
Transcripts are secret-free. The validator finishes with `Unsigned acceptance
passed` and confirms the artifact hashes and clean source did not change.

- `Zenith.elf` SHA256: `28e58f37ce754e1357274aebf2b6cf8689a140e39c1c03f1713aefd94b181304`
- `Zenith.iso` SHA256: `22cdb35e06452c7a1ee9a356a5adb60ab4b40acab4d12348ef7a9d63da889693`
- Retained evidence: `/var/tmp/zn-qualified-runtime-20261017` (phase logs,
  `artifacts.json`, `phase-times.json`, native disks/transcripts and wait metrics).
- Additional focused evidence: 80 pipelines across GC before the requeue
  refinement, and 20 fresh installed GUI immediate-EOF/cancel cycles plus hidden
  password and local input after the queue correction. These supplement the full
  exact-commit run; they do not replace it.

Only copied dependency assemblies are adapted. Cosmos stays pinned at 3.0.89;
upstream and installed NuGet packages are unchanged. ARM64 collector layout/scanner
adaptation is host-tested, not a claim of ARM64 guest acceptance. Managed execution
still stays on the BSP. The proposed kernel 0.0.19 tag must point to the tested
commit above; later documentation-only commits do not change that target.
No replacement tag is signed/pushed and no operator VM is updated by these checks.

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

## Initial wake-only validation (superseded)

- 27 pinned dependency regressions passed: wake/queue behavior, real patched
  CoreLib trace adaptation, idempotence/drift refusal, TCP and IDT adapters.
- 121 controller checks and 654 Release host tests passed during investigation.
- Final-artifact guest stress and full clean-source acceptance remain pending.
- No replacement kernel tag, distributor publication or operator installation
  has been established by this investigation.

## Collector regression before final acceptance

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

## GUI blocker on a superseded candidate

At `773a59c`, 35 dependency adapters, 121 controllers and 654 host tests pass.
The clean-source common run passes login/session (197.8s) and all 92 repository
checks (306.4s). All 64 exact-output/completion pipelines pass, including collection
#1 freeing 1,154,782 objects, followed by FP, fault, permission and independent
write/ext2 audits. It fails later at desktop `N_GUI_INPUT`; this is not a pass for
the native-process gate. The separate unchanged-artifact kernel recovery gate
passes 169/169 in 482.282s.

Two isolated desktop/local cases pass. Repeated desktop fixtures reproduce the
missing canonical-line delivery (fresh-root cycle 3 and a clone of the original
installed root later in its run). Screenshots show the complete entered line and
newline in the terminal, while the native pipeline remains waiting. The original
failure evidence remains intact; the clone uses a replacement fixture-only root
credential, retaining normal hashing and authentication. No actual VM disk or
operator credentials are changed. Pending-read/scheduler snapshots are being
captured to distinguish queue handoff from scheduling; no replacement release is
qualified or pushed.

The GUI failure label was misleading: `logger` consumes complete stdin before
logging its lines, so a lost EOF also prevents the expected input marker. The
old Ctrl+D handler called `AnswerRead(null)` once and ignored a false result.
When the previous line reply is still pending (or the next read has not started),
that discards EOF and leaves cat/logger waiting. Submitted type-ahead could also
be bypassed by a directly answered EOF. An ordered GUI-owned `TerminalTypeAhead`
now retains lines and EOF until the worker accepts each item. EOF is discarded at
the next command prompt, never interpreted as a command. Cancel and both secret
preparation/teardown clear queued input; hidden input is delivered only after the
GUI's secret-preparation handshake. The four host regressions pass, and all 658
host tests plus a Release build pass. A fresh GUI installation is exercising
hidden password changes and repeated immediate EOF. Final acceptance is pending.

The fresh installed queue-corrected fixture passes its graphical password change,
20 repeated desktop line/EOF/cancel cycles with no delay before EOF, local-console
input/EOF/cancel, and secret-free transcripts. All 658 host and 121 controller
tests pass. The 56585f2 common run passes host/build and login/session, then is
stopped deliberately to align the newly added graphical password-change result
with `guest_auth.COMPLETION_SECONDS`, rather than adding another unrelated short
hash/save timeout on slow distributor VMs. That partial run is not a full pass;
the final clean candidate will run every common gate again.
