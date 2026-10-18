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

The failure observer also records a halt call-site candidate. It reads exactly one
ABI return-address word only for `_native_cpu_halt+1`, then validates the preceding
five bytes against a direct CALL to that symbol in the exact ELF. Unverified words,
other stack locals and general registers are not retained. The guest is running,
so this does not claim a frozen stack trace. Unit tests cover valid/invalid code
addresses and rejection of non-CALL instructions. A live QEMU probe resolves the
idle halt to `Cosmos.Kernel.System.Power.Halt`; the actual server wait-site capture
remains pending if the candidate still fails.

Candidate kernel source: `a63ce7cedc4704a5aa62c5c6b57bb7bb9eedd5c2`.
Release build applies the signature-checked correction and succeeds; 658 host,
40 dependency adapter and 134 current controller checks pass. Candidate hashes:

- ISO: `de5040481e324675fd3e6dbe86338c523faa35ddc48ccbe47919a49c772c11bd`.
- ELF: `2ffb2d14a0ff4f1292613e45defde76faf399be550f0a0f539392a95eb4f4255`.

Focused native evidence: `/tmp/zn-self-dispatch-native`, with run output in
`/tmp/zn-self-dispatch-native.log`. The full native gate passes in **648.864s**: all
64 byte pipelines across GC, floating-point streaming, fault/cancel/early-exit
cleanup, permissions, independent persisted binary/ext2 audits, hidden GUI password
input, normal/immediate EOF and local-console cancellation. Transcripts pass the
secret-free assertions. ISO/ELF hashes remain unchanged after the run.
The test uses Debian QEMU 10.0.13 and explicit TCG on the laptop. Docker-client CPU
accounting cannot be used as QEMU CPU time. Full clean-source release acceptance
and the actual distributor's focused candidate test remain required before a tag.


## Rebuilt candidate still fails; condition-wait contracts

The operator rebuilt current source on the distributor. ELF
`5abf685c6ae24bf64744ab5af4f5f8fd5b4ddec4d222ec399b00e29fcb49522c`
still fails `N_STREAM_BYTES_0` in 120.727s (12.17s QEMU CPU; 324.483s harness).
No count or completion is observed. Two sampled halt returns independently
validate as `ConditionVariable.WaitTimeout+270`; another sample is in
`PageAllocator.GetPageCountsByType`. The snapshot has 78 Sleeping and
0/79/80/81 Ready. These observations do not prove a lock owner, queue membership,
or a frozen backtrace. The earlier self-dispatch fix is insufficient to resolve
the real failure. This server-built candidate is distinct from the old copied
0.0.19 ELF and from the locally passing candidate.

Inspection of actual pinned and transplanted IL identifies two further defects:

- `ConditionVariable.Wait` / `WaitTimeout` use one guarded HLT. An unrelated IRQ
  can return to that stack while the thread remains Blocked/Sleeping; the methods
  then remove waiters/reacquire the mutex and report a result without a real wake.
- `Interop.Sys.LowLevelMonitor_TimedWait` discards `Monitor.Wait(int)`'s Boolean
  and returns true. CoreLib's wait subsystem treats that as a signal/spurious wake,
  instead of a timeout. The source plug and already transplanted body both do it.

The candidate changes the guarded HLT into a state-checked park loop and forwards
only the timed wait's actual Boolean. Infinite waits retain their original true
return after completion. Signature checks fail closed on unknown IL; adaptations
apply only to copied dependency assemblies. Kernel/interop trace suppression and
existing IRQ/state/GC fixes are preserved. There is no command replay, relaxed
expected output, timeout increase, or replacement tag.

A real-dependency fixture injects one unrelated interrupt and then an actual wake.
Original methods return after one halt with state still Sleeping/Blocked; adapted
methods halt twice and return only after Ready. One hundred timed-wait/self-dispatch
cycles preserve the 200-ticket total and restored IRQ flags. Two consecutive
original premature waits subtract the same thread's tickets twice (200 -> 0);
the corrected equivalent stays at 200. A second fixture
executes the actual source and transplanted timeout branch/return IL with controlled
monitor results: false remains false, true remains true, and the infinite branch
still waits once and returns true. Fixtures stub hardware/opaque-handle access,
not scheduler queue/accounting or the branch/return behavior being asserted.

These are independently demonstrated runtime defects, not yet proof that they
explain the distributor's stalled pipeline. Guest validation and actual server
acceptance are required before a replacement release.


Local wait-correction validation:

- Release ISO/ELF build succeeds; 658 host, 134 controller and all 48 dependency
  adapter tests pass (17 scheduler/condition cases, including ticket double debit).
- Full native gate passes in **686.101s** under the same Debian QEMU 10.0.13
  package and explicit TCG: 64 exact byte pipelines across collection pressure,
  floating-point streams, fault/cancel/early exit, permission and installed data
  audits, hidden authentication, graphical and local-console EOF/cancellation.
- Evidence: `/tmp/zn-condition-native`, `/tmp/zn-condition-native.log`,
  `/tmp/zn-condition-build.log`, `/tmp/zn-condition-host.log`,
  `/tmp/zn-condition-controllers.log`, `/tmp/zn-condition-adapters-final.log`.
- ISO SHA256: `1ab5070a1651c118b1277be7de6a4ff7c869903ea7d1cb36f2da9075d95cc280`.
- ELF SHA256: `a952d4f6f2f665c55144e3f293ddb2884f1fbda5f842fd3d75a1f7819a69733d`.
  Both hashes remain unchanged after the gate. Native disassembly confirms the
  state-check/back-edge around HLT and that timed interop returns the wait result
  without overwriting EAX with true. Docker-client CPU figures are not QEMU CPU.

The actual distributor path remains unverified. Local success did not prove that
path for prior candidates and does not do so here. This untagged correction has
not passed a new full clean-source four-gate release qualification; no new version
is published and 0.0.19 remains immutable.
