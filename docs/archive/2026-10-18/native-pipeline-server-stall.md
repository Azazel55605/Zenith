# Server-only native pipeline stall

Recorded **2026-10-18**. Investigation remains open. Local success does not establish
server reliability. Kernel tag `kernel/v0.0.19` stays immutable at `6544ffd`;
no replacement kernel release is qualified by this investigation.

## Actual failure

The operator's distributor native gate fails at its first repeated four-stage
pipeline, `N_STREAM_BYTES_0`, after 120.702 seconds with 13.5 QEMU CPU seconds.
Neither the exact output `163840` nor a completion status is observed. The serial
extract shows the complete pipeline command, four scheduled threads (78..81),
and signals to threads 80 and 81. Subsequent timer/stride records repeatedly show
current thread 78 and run queue 5. Timer interrupts continue; no fatal fault is
reported. These facts do not yet identify which instruction or synchronization
object is preventing progress. A timeout increase is not a demonstrated fix.

Server environment supplied by the operator:

- Debian QEMU `10.0.13` (`1:10.0.13+ds-0+deb13u1`), explicit TCG.
- .NET SDK `10.0.402`.
- ELF SHA256: `05ddf84df1741719e9e9c1998e70c392c3c4b8be8e2fe90e09aafbff3fea54e5`.
- Copied ISO SHA256: `d5a1b78840eda7162bdddb8f2a80c5e3cb57b3b604c682fd13d6eabc933a7d81`.
- Retained fixture: `release-uh4gyent/native/u/installed` beneath the distributor's
  `build-kernel-v0.0.19/obj/distributor` directory.

## Controlled comparisons

The previously accepted local ISO/ELF remain unchanged:

- ISO: `22cdb35e06452c7a1ee9a356a5adb60ab4b40acab4d12348ef7a9d63da889693`.
- ELF: `28e58f37ce754e1357274aebf2b6cf8689a140e39c1c03f1713aefd94b181304`.

| Binary / emulator | Observation |
| --- | --- |
| Accepted ISO / local QEMU 11.1.2 TCG | Full native gate passes in 675.571s, including all 64 byte pipelines, GC, installed audit and GUI/local input |
| Accepted ISO / isolated Debian QEMU 10.0.13 TCG | Full native gate passes in 654.205s, including all 64 repetitions |
| Exact copied server ISO / Debian QEMU 10.0.13 TCG on laptop | Full native gate passes in 761.966s, including all 64 pipelines and installed/GUI/local audits. CPU quota was temporarily limited to 0.25 CPU after repetition 11, restored after the repetitions |

The Docker image contains Debian's exact QEMU package. It uses the laptop's
firmware files and host networking for the disposable fixture server. This is a
controlled emulator comparison (Docker-client CPU accounting is not QEMU CPU
accounting), not a recreation of the distributor's TrueNAS VM,
host scheduling, timer calibration or firmware environment. It does not prove that
SDK, emulator or host timing is individually responsible or excluded.

Copied binaries live in ignored `obj/server-0.0.19`; the server-artifact run uses
an isolated source archive of tested `6544ffd` and separate output directory.
The release artifacts in `output-x64` are not replaced.

Local evidence:

- `/tmp/zn019-recheck`, `/tmp/zn-019-recheck-20261018.log`.
- `/tmp/zn019-debian2`, `/tmp/zn019-debian2.log`.
- `/tmp/zn019-server-src`, `/tmp/zn019-server-fixture`, `/tmp/zn019-server-fixture.log`.

## Failure snapshot, before QEMU cleanup

The current distributor controller's profile wrapper now observes failed numeric
byte-pipeline waits before their tagged harness raises and stops QEMU. It keeps the
original failure, deadline and command. It sends only read-only HMP requests:

- Three bounded `info registers` samples, retaining only RIP, RSP and flags.
- Instruction symbols and offsets from the tagged checkout's exact ELF, plus its
  SHA256. It does not symbolize against the controller's own output directory.
- The pinned Cosmos version-1 numeric scheduler debug buffer: thread id, CPU,
  state and flags. Reads validate magic/version, entry bounds and seqlock; packed
  four-byte alignment is supported. Missing/unstable buffers are reported as such.

It retains `pipeline-stall.json` in the VM directory and prints its bounded numeric
report into the phase log. It excludes general registers, arbitrary memory,
passwords and raw serial text. Snapshot errors cannot mask the original failure.
Successful waits and unrelated/authentication waits do not invoke the capture.
No guest command is replayed and the guest is not paused or resumed by the capture.

Validation: 132 common controller tests pass, including fragmented HMP replies,
selected-register filtering, bounds, torn/unsupported scheduler buffers, exact
ELF symbols and failure-only integration. A synthetic failed wait attached to a
running disposable QEMU verifies real register reads/symbol mapping while returning
False. A live scheduler debug-buffer read verifies the packed schema. These are
observation-path tests, not reproduction of the distributor failure.

## Next evidence required

Controller diagnostics were pushed as `384d9fc`; the kernel tag still points to
`6544ffd`. Pull the updated controller checkout on the distributor, then rerun only `native-process-smoke.py`
against the retained 0.0.19 checkout. Read the emitted instruction/thread snapshot
from the actual server failure before deciding on a runtime correction. Do not
move the release tag or bypass any release gate. Distributor checkout update and actual server failure capture remain pending.
The three local native gates all passed, so no underlying runtime fix or new kernel
release is claimed.

## Actual server snapshot and candidate correction

The next focused operator run still fails `N_STREAM_BYTES_0` after 120.715s
(13.22 QEMU CPU seconds), with the same ELF hash. Three observations alternate
between two worker stack pointers in `_native_cpu_halt+1`, with IF enabled.
The stable version-1 scheduler buffer reports 0, 78, 79 and 81 Ready (state 1),
80 Sleeping (state 4), and no Running entry (state 2). This is a live observation
of scheduling state, not proof that a particular lock owns the stall.

A deterministic host fixture now reproduces a missing state transition in the
actual pinned `SchedulerManager`, with only IRQ/UART/context-switch hardware
stubbed. Let the current thread's short sleep expire before it switches away:
`ReadyThread` enqueues it, then `ScheduleFromInterrupt` selects that same thread
and consumes its queue entry. The original same-thread branch skips all of the
different-thread body, including setting Running. It returns with the current
thread Ready and absent from the queue. The voluntary `Schedule` path has the
same omission. Both the pristine dependency and the existing 0.0.19 adapters
reproduce `state=1;queue=0;tickets=200;stack=4096;switchSp=0`.

`SchedulerDispatchFixup` signature-checks both dispatcher shapes and changes only
the self-selection branch: Ready becomes Running, then control continues through
the original same-thread exit. It does not save a live stack, request a context
switch to that stack, change admission/tickets, promote Created/Blocked/Sleeping/
Dead states, or change the different-thread entry flag. The result is
`state=2;queue=0;tickets=200;stack=4096;switchSp=0;new=0`.

All 13 real-dependency scheduler regressions pass: original failure, IRQ/voluntary
self-selection, 100 short-sleep/wake/self-selection cycles with exact ticket and
stack preservation, existing/deduplicated queue cases, real different-thread and
Created entry flags, inactive-state preservation, signature drift and byte-idempotence.
All 40 pinned dependency adapter tests pass. The installed NuGet assembly and
upstream checkout remain unchanged.

This correction repairs a demonstrated invariant violation consistent with the
operator's missing Running state. It is not yet established as the complete cause
of the server's stalled pipeline. Full candidate release acceptance and a focused
run on the actual distributor remain required; no new kernel tag is created here.
