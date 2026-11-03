# C1 — Isolated interrupt-driven native queues

Work owner: **C1** in [ROADMAP](../../ROADMAP.md).
[Current contract](../../MULTICORE.md) · [next stages](../../components/MULTICORE.md).

## Implementation

The initial lifecycle commit `e7dcec0` established bounded cancellation and AP halt
acknowledgement before disk teardown. This follow-up completes the native worker
foundation: eight-slot per-AP FIFO, generation tokens/terminal ownership, private
GDT/TSS and work/IST1/IST2 stacks, targeted APIC wake/EOI and race-free HLT idle,
permanent failed-queue quarantine and cooperative stop across queued/held/running
jobs. Jobs remain typed native work; there is no arbitrary callback or managed AP.

BSP mutations preserve IF; lock and xAPIC delivery waits are bounded. Publication
uses MFENCE before the IPI and LFENCE before x2APIC WRMSR, as required by
[Intel SDM 11.12.3](https://cdrdv2-public.intel.com/819710/252046-sdm-change-document.pdf#page=315). Workers rely on queue/state predicates,
not interrupt counts. Spurious delivery has no EOI. AP fault and halt acknowledgement
never frees kernel-owned storage. The build rejects unexpected native imports and
uses general registers only; host disassembly checks reject FP/SIMD instructions.

The new native pixel pilot owns fixed private buffers, generates bounded input and
performs repeated RGB/opaque-alpha blends. BSP reads only completed pixels. A separate
managed/Python scalar path verifies every sampled pilot pixel; full timed output is
also compared to sequential native work. This is a tested offload foundation, not
integration of the existing GUI renderer with the pool.

## Local artifact and environment

Untagged Release, Cosmos 3.0.89, SDK 10.0.114, QEMU **11.1.3**, forced **TCG**.
ELF SHA256: `f8d63b0c3682e2a65c5743f5c92b9921fe6ccbeb6566e590790f6c607ae84796`.
ISO SHA256: `0659ad91adb866deea221c16ad5eac0ab1dfea7f803473b5ae8469c23b0e8aff`.
Exact artifacts and frozen source-input hashes: `obj/c1-complete-artifacts`.
The normal publish command rebuilt them and its subsequent native-object/link/ISO
cache pass retained the same artifact hashes.
No KVM device was available locally. No distributor/installed/VMware or physical
acceptance is inferred. Ordinary boots and managed work still use the BSP.

## Queue, interrupt and failure acceptance

`obj/c1-owned-status-cpu`: **94 checks passed**, 94.315 seconds, 1/2/4-vCPU opt-in,
four-vCPU default-disabled, plus fresh two/four-vCPU fault boots. The gate verifies:

- Eight retained descriptors, full refusal, FIFO results and stale-token rejection.
- Refusal of unready workers; reaped tokens stay invalid after later pool failure.
- Partial batch admission followed by failed-AP refusal stops the abandoned owner
  work on healthy APs, leaving the BSP responsive and shutdown acknowledged.
- Queued and executing cancellation, held-job release, reusable terminal slots.
- 200 publish/park races per AP; every submitted job completed once, without retries.
- Interrupt-stack bounds, actual CPU identities, checksum correctness and IRQ IF
  preservation at initial IDT load and under both IDT reload states.
- A real AP `#UD` on private IST2, quarantine of seven queued followers,
  failed-queue refusal, healthy sibling completion
  and acknowledged poweroff with a fault-contained AP.
- Stop while jobs are executing/held and queued, including a separate managed
  BSP thread stopping the pool while its producer retains the command gate;
  permanently closed admission,
  responsive BSP shell and clean poweroff after AP acknowledgement.
- Idle park counters remain unchanged without work; actual QEMU CPU use is low.

The earlier compatibility run, `obj/c1-ipi-first`, passed the previous 61-check CPU
matrix against the initial queue candidate. Preliminary expanded matrices also
passed at `obj/c1-queues-full` and `obj/c1-final-cpu`; the final acceptance evidence
is `obj/c1-owned-status-cpu` above. Live APIC mode in that run is xAPIC (`apic-mode=0`).
The x2APIC fence sequence is checked in emitted code, but live x2APIC acceptance is
not established locally.

The final audit tightened atomic publication of live metadata, masked the local
error LVT first with a valid vector, required terminal queue cleanup before halt
acknowledgement, and removed managed producer-lock dependency from stop. No native
source or compiled artifact changed during the final gate runs.

## Equal-work native pixel timings

Final-artifact run, identical 32 tiles × 4,096 pixels × 128 passes, while the
independent process gate was running. Timed dispatch/computation
excludes reference validation. Same final hash `5895a376b9426285` at each CPU count.

| Guest CPUs | Elapsed | Relative to one CPU |
|---|---:|---:|
| 1 | 339,839 µs | 1.00× |
| 2 | 203,788 µs | 1.67× |
| 4 | 159,099 µs | 2.14× |

These are local TCG pilot timings, not a GUI/process benchmark or a universal
scaling guarantee. The compositor, GC and application services remain BSP-only.

## Idle before/after

A separate copy of the original clean installed lifecycle fixture was booted with
four CPUs and `smp=native`, preserving the original disk:
`obj/c1-process-workers/installed/disk.img`, kernel SHA256
`8e8778f7303fa6602680ceedc8ab72c6583f2ba5a27ea9a06fca88c047ee35ce`.
After headless readiness, a one-second sample consumed **3.01 QEMU CPU-seconds**.
Evidence: `obj/c1-idle-baseline/idle-baseline.json`; no guest operation was repeated,
and only the disposable measurement copy was stopped afterward.

The final pool used **0.05 CPU-seconds** at two vCPUs and **0.04 CPU-seconds** at four
vCPUs over one second, with unchanged AP park counters. This measures total emulator
CPU use, including the BSP. It establishes removal of the three busy idle workers
in this setup, not sustained whole-system energy use.

## Host/controller/process evidence

- **846 host tests passed**, including PowerControl's stop-before-disk refusal.
- **371 controller tests passed** with disposable `/var/tmp` access.
- Fifteen native queue/build tests execute production queue/math code on host threads:
  bounds, capacity including retained results, generations, held/queued/running
  cancellation, ring wrap with parallel consumers, pixel reference equivalence,
  global stop during execution/queued work, contended-stop acknowledgement,
  interrupted-compile retention/retry, unready admission, reaped-token invalidity
  after later AP fault, and BSP/AP equal work; freestanding imports and no-FP code generation.
- **Full AP-enabled native-process gate passed**, 808.759 seconds,
  `obj/c1-complete-process`, same final ELF. Includes all 64 exact byte pipelines,
  FP/register/memory/privilege/fault/timeout containment, worker checks after faults,
  signed executable upgrade, account/file permissions, hidden authentication,
  GUI/local canonical/raw/EOF/cancel restoration, clean poweroff and ext2 audit.
- Documentation links/tracker checks and edited CI YAML parsing passed.

`inputs.json` captures the frozen native/kernel/process inputs.
`inputs-accepted.json` also records the final CPU-harness-only status correction.
All native code, process-gate inputs and ELF/ISO hashes stayed unchanged during
these final runs. This is local working-tree validation, not a clean-source
full eleven-gate release qualification. Fresh full acceptance is still required
before a new kernel tag.

For C1 source trees, the full release plan adds `native-cpu-smoke.py` and the normal
native-process gate enables AP workers by default. The existing ten independent
native gates remain, for **eleven** total. Historical immutable tags retain their
original harness behavior; focused `--bsp-only` does not replace SMP acceptance.
No release/tag is produced by these local tests.

## Superseded runs

`obj/c1-queues-process` passed the full AP-enabled process gate in 802.500 seconds
on the preliminary `b22802e…` candidate. It is superseded by final-artifact evidence.
`obj/c1-final-process` on `887ae50…` was intentionally stopped at its owned QEMU
monitor after the last audit required another candidate. That interrupted run is
not a pass and its disk is not a clean baseline. Its exact ELF was preserved there.
No guest operation was retried and no acceptance receipt was manufactured.

The fifteen queue/build host regressions and the 371 common controller tests
include the stop-lock failure and emitted Intel fence checks. CI runs the CPU and
AP-process gates once through the common release plan; the former standalone
duplicate CI steps were removed, with retained evidence under `smoke-release`.

The new negative owner-abort case first exposed a harness mistake at
`obj/c1-complete-cpu`: `cpu test | logger` returns the logger's exit code. The
kernel correctly refused the batch, but the gate expected status 1 and observed
pipeline status 0. That run is not passing evidence. The CPU gate now saves the
producer status and captures complete output in separate files before logging
both; all success and refusal assertions use the CPU command's own result.
`obj/c1-status-owned-cpu` verified raw producer statuses, then the final
`obj/c1-owned-status-cpu` run above verified complete output capture as well.
No kernel rebuild or guest operation replay was used to fix that harness assertion.
