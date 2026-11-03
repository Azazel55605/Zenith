# C3 — Pinned managed workers and stopped-root collection

Work owner: **C3** in the [master roadmap](../../ROADMAP.md).
[Execution and failure contract](../../MULTICORE.md#c3-pinned-managed-compute).
This records local qualification, not distributor or installed acceptance.

## Delivered boundary

Opt-in `smp=native` now supports fixed trusted managed compute jobs on permanent
AP owners. Each has its own TLS, TLAB, exception chain and 256 KiB work stack.
General scheduler dispatch, foreground application entry, devices and UI mutation
remain BSP-owned. AP owners do not enter policy queues or migrate.

Six object-header/length allocation entries and seven unmanaged heap/page entries
share a recursive native gate with IRQ preservation. Only the BSP collects. It
selects active mutators under that gate, sends a new stop epoch and requires every
selected CPU's current register/work-stack acknowledgement before entering the
old collector. Native IST3 and explicit publication-wait polls capture roots;
running APs are never scanned using the BSP's RSP. Pinned/interior normalization
and existing conservative/precise BSP roots remain in place.

Missing acknowledgements refuse collection before mark/sweep. A collector failure
after admission keeps its epoch/heap ownership, fail-stops the BSP and sends the
parked APs through their private native fault path. It never resumes mutators on
possibly changed mark/free metadata. Failed AP storage stays reserved; no allocator
repair or AP restart is attempted. AP collection requests are deferred to the BSP.
This is bounded fixed-job admission, not a general AP OOM/retry scheduler.

The copied runtime advertises a checked internal C3 contract only after the GC,
owner, exception, metadata and serial/GOP adapters are present. Missing pieces
refuse adaptation/admission instead of producing a partial managed SMP setup.
Installed packages and the sibling upstream source tree were not patched.

## Issues found during implementation

The pinned boot initializer creates physical-count idle records on the BSP before
any real AP managed entry. Array capacity was therefore not proof that C3 owners
had been registered. The general scheduler now boots one real BSP owner; a separate
completed-attachment witness records permanent AP capacity. Physical topology is
unchanged, and policy handover excludes those AP owners.

Exception links and dispatcher scratch were global. Links now belong to the
registered thread, and scratch is thread-local and prewarmed for BSP/AP entries.
The native slot accessor preserves caller registers/FP state and dynamically
aligns its temporary stack; exception sites are not all normal ABI call boundaries.
All twelve early UART/GOP writers are BSP-owned. Unsupported AP exception dispatch
traps into the native fault IST rather than entering BSP panic/UI services.

The cold controller run exposed a separate shared metadata race:
`StackTraceMetadata.PerModuleMethodNameResolverCache.GetOrCreate` stored `_keys[i]`
before constructing `_resolvers[i]`. Another exception handler could find the key
and return a null resolver. The retained failure is
`obj/c3-qualified-managed/2/serial.log`; its fault resolves to dereferencing that
null resolver. Metadata access now holds publication ownership, and a key is
published only after complete resolver construction. Host regressions force the
second reader and a throwing constructor, then verify retry and IRQ/gate cleanup.

Short jobs originally paid a scheduler quantum after each sleep. A guarded,
native-only BSP completion doorbell now wakes interrupt waits without invoking
managed AP scheduling. The original IRQ246 gate must match before installation.
AP-to-BSP notifications use the executing sender's APIC mode/base. Notification
kind is captured before terminal publication, so a reaped/reused descriptor is
never reread by the old completion path. Cancellation borrows a permanent atomic
word only until callback return; stop publishes cancellation before halt waits.

## Complete-frame consumer

Managed jobs compute private arrays, copy complete tiles into descriptor-owned
native buffers and publish terminal success. BSP copying requires exact dimensions
and successful ownership. Partial, cancelled, faulted and stale-token frames are
unavailable. `fps cpu` asks the UI tick to assemble and paint a completed 128×128
frame; APs never access a Window, Surface or input state.

The GUI gate compares every displayed pixel with an independent Python scalar
oracle, then verifies focused input, close, the public Terminal handoff and clean
shutdown. Its fresh-live prompt witness is separate from the installed Settings
fixture; hidden prompts cannot qualify. One declared button-free cursor move
keeps the cursor outside the inspected frame. No screenshot-based corrections,
click/command replay or relaxed pixel comparisons were used.

## Frozen local acceptance

Environment: Cosmos 3.0.89, .NET SDK 10.0.114, QEMU 11.1.3, TCG, 512 MiB per guest.
No local KVM device. Accepted x64 Debug artifacts:

- ELF SHA256 `d1d315e0c54045adf92eefa04e4d2864a4108816ca74fcb4a61911be81802008`.
- ISO SHA256 `8af51bf0c3239b2e67e254c4a1c0191b24b76c1a7b75a935beb57452f4a61016`.

The hashes stayed unchanged through the acceptance below:

- **413 controller regressions passed**, including native epoch/publication tests,
  real adapted CIL, constructor/publication races, BSP output ownership, partial
  contract refusal, source-drift rejection and exact-frame negative cases.
- **846 host tests passed**; existing runtime-adapter regressions additionally
  passed all **46** checks. The focused C3/CPU mechanism set passed **56** checks.
- **106 original CPU checks passed**, including default-disabled and 1/2/4-CPU
  opt-in boots, queues, FP/context, wake/idle, cancellation, faults and quiescence:
  `obj/c3-accepted-final-cpu`, 100.664 seconds.
- **Full controller-driven C3 gate passed**, 52.192 seconds:
  `obj/c3-accepted-managed`. Two/four-CPU cases reached 15 accepted GC epochs,
  including repeated real collections with retained AP chains, interior/POH roots,
  allocation, deferred requests, TLS/credential isolation, contained/reported
  exceptions, cancellation and live BSP FP state across completion interrupts.
  Separate cases proved failed-AP root refusal, healthy siblings, default-disabled
  admission and cancellation/quiescence of a parked managed batch during shutdown.
- The same gate rendered the exact reference frame at 1280×800 on 1/2/4 CPUs.
  Input and close were acknowledged within its approximately 51 ms polling window;
  this is a test observation, not a sub-frame input-latency certification.
- **Full installed AP-enabled process gate passed**, 733.096 seconds:
  `obj/c3-accepted-final-process`. Managed AP roots stayed live through package
  operations, 64 exact byte pipelines, register/FP and privilege/memory faults,
  cancellation and filesystem checks; release then verified those roots and owner
  reuse. Independent executable upgrade, UID/permissions, hidden authentication,
  GUI/local canonical/raw/EOF/cancel restoration, fonts/manuals, ext2 audits and
  clean poweroff also passed.
- ARM64 cross-build passed at `obj/c3-accepted-arm64`; architecture guards leave
  AP admission disabled. This is a build result, not an ARM64 boot/SMP claim.

## Equal-work managed frame pilot

Same final artifact, full-sized warm-up before timing, 12 tiles × 4,096 pixels ×
32 passes. Time includes dispatch, computation, completion waiting and BSP copies;
independent reference validation is outside the timed interval. The process and
CPU gates were also running, so these are contended local TCG observations.

| Guest CPUs | Elapsed | Relative to one CPU |
|---|---:|---:|
| 1 | 65,253 µs | 1.00× |
| 2 | 28,039 µs | 2.33× |
| 4 | 25,322 µs | 2.58× |

Every run produced hash `6c916f1c8531c1b9` and identical pixels. Guest Stopwatch/TSC,
emulation, observation cadence and BSP scheduling affect these values. This is
neither full-compositor scaling nor a general application/process speedup claim.

## Process latency comparison

A focused run copied the same clean pre-password installed baseline and signed
Sunbeam payload into fresh 1/2/4-CPU guests. The multi-CPU cases kept managed AP
roots parked throughout the pipelines, then verified and reaped the owners. No
configured login or password was bypassed. Each 160 KiB pipeline produced exactly
163,840 bytes and exited successfully; one warm-up preceded five samples.

| Guest CPUs | Managed AP roots held | Median CLI roundtrip |
|---|---:|---:|
| 1 | 0 | 3.468 s |
| 2 | 1 | 3.457 s |
| 4 | 3 | 3.437 s |

Evidence: `obj/c3-process-latency/latency.json`, same accepted ELF hash. This is
host-observed command/serial/pipeline/status latency, not a pure CPU or spawn-time
measurement. It stayed essentially flat, as expected while foreground application
entry remains BSP-owned; it establishes coexistence rather than an app speedup.
The focused helper does not replace the complete process or release gates.

C4 owns measured integration into real compositor/app workloads, broader service
concurrency, centralized interrupt leasing, migration and recovery. Full fresh
12-gate release qualification remains required before a new kernel tag.
Distributor, installed VMware, live x2APIC/nonzero BSP IDs and physical acceptance
remain separate; there is no new tag or publication in this implementation batch.
