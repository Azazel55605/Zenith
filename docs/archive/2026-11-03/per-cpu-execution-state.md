# C2 — Per-CPU execution state

Work owner: **C2** in [ROADMAP](../../ROADMAP.md).
[Runtime contract](../../MULTICORE.md) · [component](../../components/MULTICORE.md).

## Changes and admission boundary

C2 preserves the C1 native-only AP boundary. It does not increase the managed
scheduler CPU count, enable managed AP entry or run ordinary apps on APs.

- The copied Cosmos scheduler reads hardware-derived logical CPU identity instead
  of returning zero. BSP identity works before the native pool starts. Registered
  APs map to C1 logical indices; an unknown CPU returns `uint.MaxValue`, never BSP.
- Timer and device-IRQ scheduler callbacks use logical IDs, rather than indexing
  scheduler state with physical APIC IDs. Existing per-CPU scheduler/current-thread
  containers stay single-CPU until C3 can safely admit managed execution.
- Signature-checked copies of the exact Cosmos 3.0.89 interrupt source use
  cache-line-separated CPU slots for switch target/new-thread/error state. The
  return-path temporary stays on the interrupted thread's stack. No old shared
  switch/error scratch symbols remain in the adapted object.
- The x64 interrupt frame saves/restores baseline x87/MMX/SSE state using a
  16-byte-aligned, 512-byte FXSAVE image. Managed ThreadContext layout (704 bytes),
  initial x87 control/MXCSR and both IRQ scheduling stack calculations change
  together. AVX/XSAVE ownership is not enabled by this baseline contract. See
  [Intel instruction reference](https://cdrdv2-public.intel.com/812383/253666-sdm-vol-2a.pdf).
- Each native AP initializes its own FP environment. Its private GDT/TSS, work,
  interrupt and fault stacks remain the C1 owners. Typed state probes exercise
  logical identity, switch slots, FP registers/control and local timer delivery.
- AP-local one-shot timers and targeted native reschedule IPIs have separate
  native IST1 handlers/counters. A reschedule interrupt acknowledges a native
  request; it does not invoke the managed scheduler. Timers are diagnostic only,
  masked again before completion; idle APs still halt without periodic polling.
- The raw process bridge rejects a non-BSP entry *before* its first shared
  context/selector/stack write. Its first-call path can no longer establish a
  different CPU owner after corrupting those globals. The existing raw gate and
  BSP-only user/kernel stack, GDT/TSS and FP handoff remain mandatory.

## Lock, credential and runtime audit

| Owner/state | C2 disposition | Required before broader concurrency |
|---|---|---|
| Scheduler/current thread | Hardware logical identity and existing per-CPU containers; only BSP container is admitted | C3 admission/roots, per-CPU policy initialization and pinned runnable queues |
| IRQ switch/error scratch | CPU-local native slots; return temporary is stack-local | Preserve CPU identity through entry/return; migration remains C4 |
| GDT/TSS/IST | Static private native AP tables/stacks; protected process tables owned only by BSP | Separate protected process contexts before any AP user execution |
| FP state | Full baseline IRQ/thread save image; independent AP initial state and private probe saves | Deliberate XSAVE/AVX support needs its own negotiated state contract |
| AP timer/reschedule IPI | Native-only IST handlers; no managed callback; one-shot diagnostic timer | Per-CPU calibrated scheduler timer/admission after GC readiness |
| Scheduler locks/registry | Existing IRQ scopes and global/CPU locks protect BSP operations; no concurrent AP mutation | Global thread registry/policy replacement and cross-CPU ready/exit ordering must join C3 admission |
| Credentials | Captured caller identity; managed `[ThreadStatic]` credentials; no native AP service invocation | Validate per-thread TLS and identity on each admitted CPU, then C4 service concurrency |
| Raw process bridge | Early hardware BSP refusal plus existing `s_rawGate`; globals cannot be used by APs | Allocate per-CPU process state before removing either restriction |
| Service/root gates | `NativeProcess` raw/root/service gates, ext2 volume gate and trusted UI owner remain BSP-managed | C3 root publication; C4 service/IO contention audit |
| Managed EH/GC/runtime globals | Native AP faults stay entirely outside managed exception/heap paths | C3 must cover global exception chain, allocation publication, all CPU roots and TLS before managed AP entry |

Local IRQ masking does not exclude another CPU. Existing IRQ-only allocator and
GC scopes are not accepted as SMP locks. Scheduler thread migration remains off;
C2 does not claim these audited managed paths have become concurrently safe.

## Validation

Local paired qualification passed against a frozen Release kernel:

- ELF SHA256 `acc42596e53ed1c9432753e9d0024e2e5caa38a145270dda85231f4744617d2f`.
- ISO SHA256 `b3ef3ec9d2f703763e624991c70b6980f26d23db0d7ba6d624858f65d55f3466`.
- Artifacts and frozen source hashes: `obj/c2-accepted-artifacts/inputs.json`.
- Local Cosmos 3.0.89, SDK 10.0.114, QEMU 11.1.3, forced TCG; no local KVM.

Completed checks on this implementation:

- **106 native CPU checks passed**, 101.754 seconds, `obj/c2-accepted-cpu`.
  Includes 1/2/4-vCPU opt-in, four-vCPU default-disabled, full FP-state interrupt
  checks, per-AP first-entry process refusal and timer/IPI probes, 200 publish/park
  cycles, queue/cancel/fault quarantine and acknowledged shutdown. Idle samples
  remain 0.05/0.04 QEMU CPU-seconds per second at two/four CPUs.
- **846 host tests passed**, 49 seconds.
- **382 common controller tests passed**, 73.136 seconds. These include ten
  native/managed C2 regressions: actual host CPU slot isolation, early rejected
  bridge entry, source drift refusal, real adapted IL identity propagation,
  initialized full FP layout, both IRQ scheduling consumers and idempotency.
- **46 runtime-adapter regressions passed**, including the real pinned scheduler,
  conservative roots, allocation publication, monitor timeout and IDT adapters.
- The updated read-only scheduler observer supports verified old 256-byte and new
  512-byte FP frames. Its eleven tests pass; actual C2 ELF metadata resolves
  2,985 types and verifies the logical CPU-array getter. Unknown layouts remain
  refused rather than guessed.
- The frozen ELF has no emitted YMM/ZMM references; no AVX/XSAVE preservation
  claim is made. Live nonzero BSP APIC IDs and x2APIC were not exercised locally.
- ARM64's BSP-only identity assembly compiles; this is not an ARM64 boot claim.
- Documentation/tracker checks pass. Installed packages/upstream remain unchanged.

The **full AP-enabled native-process gate passed**, 793.548 seconds,
`obj/c2-accepted-process`, on the same frozen ELF. It includes 64 exact byte
pipelines, register/FP and privilege/fault/timeout containment, post-fault CPU work,
signed executable upgrade with unchanged kernel identity, permissions, persistent
writes/ext2 audit, hidden authentication and GUI/local canonical/raw/EOF/cancel
restoration, independent fonts/manuals and clean poweroff.

These are local working-tree checks, not fresh clean-source full eleven-gate
release qualification. Distributor, installed VM, live x2APIC and physical-machine
acceptance remain separate. There is no new release/tag.

Preliminary CPU matrices preceded the full FP-context audit. An earlier process
run at `obj/c2-state-process` reached its upgrade identity check after the output
ELF had been rebuilt; that check correctly refused the artifact mismatch. It is
not final acceptance. Final runs freeze the kernel/output hashes through completion;
no failed guest operation is replayed and no success receipt is manufactured.
