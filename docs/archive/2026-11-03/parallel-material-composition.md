# C4 — Production material tiles and centralized IRQ ownership

Work owner: **C4** in the [master roadmap](../../ROADMAP.md).
[Runtime contract](../../MULTICORE.md#c4-production-material-computation-and-irq-leases).
This records the first production stage, not completion of general migration,
concurrent services, default managed SMP or distributor/installed acceptance.

## Delivered boundary

The actual MaterialComposition path now distributes larger changed blur regions
across healthy idle native owners. Clients, trusted app backgrounds/chrome and
the taskbar share this compositor. The BSP remains the UI/scene owner and computes
one tile while APs compute others. A rotating selector skips busy, failed, stopped
and retained payload owners; it never waits behind a held diagnostic batch.

A payload owns a maximum 256×64 output and a four-pixel halo clamped against the
complete region. Its native input/horizontal/output storage is permanent and
private per AP, retained until terminal generation reaping. No scene/managed
pointer, managed reference, credential or delegate is retained. The filter has
exact scalar two-pass rounding. Only terminal success and the exact length permit
an output copy. A timeout, cancelled/failed result or allocation failure disables
further blur admission, cancels outstanding work without recycling a live slot,
and reconstructs the entire unpublished region through the old scalar path.
Small, one-tile, disabled, unsupported and busy paths keep scalar rendering.

The bridge uses reusable BSP buffers and a nonblocking producer gate. It does not
allocate a closure or create a thread per frame. Window mutation, sampling policy,
contrast, client foreground validation, native process execution and display
presentation stay on the BSP. Shutdown still requires every native worker's halt
acknowledgement. This is useful bounded work distribution; no Running managed
thread, pinned C3 owner or raw-process context migrates.

## Interrupt ownership

The copied pinned InterruptManager now checks ownership before SetHandler,
AllocateVector and FreeVector reach the central lock/registry. Only the BSP may
mutate that registry. F1/F3/F4/F5/F6 are static native CPU leases, including boots
where APs are disabled. Exact IL signatures verify the MSI allocator remains
inside 40–EE; timer EF and spurious FF retain their platform owners. The native
completion IDT install still requires its exact original handler/selector/flags/
IST. C3's runtime seal also requires the central IRQ adapter.

The tests execute the actual adapted prefix against the production native policy:
17 permitted operations reach the backend; 118 reserved/AP/unknown operations
throw before the registry/lock. All three source-body drift cases refuse adaptation,
and a second adaptation is idempotent. Installed Cosmos packages and the sibling
upstream checkout remain unchanged.

## Validation method and frozen artifact

Final x64 Debug artifact:

- ELF SHA256: `23c6ea7c055e43fad224f78d6619081bb749fc14bcaef16165c50d2ee320cc53`
- ISO SHA256: `23d1328b7a8b5da1f307dee132d9fefed35e723aca219f09eea61d3c18de55b2`
- Check file: `obj/c4-accepted.sha256`.
- Cosmos 3.0.89, .NET 10.0.114, QEMU 11.1.3, 512 MiB guests.

Native queue tests compare real tile pixels against a separate nine-sample Python
oracle, including one-pixel dimensions, odd sizes and tile seams. They cover
immutable snapshots, retained completion capacity, stale tokens, wrong lengths,
invalid dimensions, busy/failed owners and mid-compute cancellation with no partial
publication. C# tests compare both scalar/tiled code to a separate direct oracle.
A further fixture runs the actual production C# bridge against native host-thread
queues for 40 complete guarded regions, source immutability and busy/off/shutdown/
CPU/range fallback. Host queue emulation does not certify IRQ/HLT behavior.

The new mandatory compositor gate uses the same frozen kernel at 1/2/4 CPUs. It
checks every pixel of a 1021×509 region, including output guards, and a common hash
`8a2f0f7845d6a309`. Five warmed, paired samples include dispatch, compute, waits
and copying; the reported time is their median. It also compares complete real
material scenes byte for byte, both ordinary and fullscreen, then verifies public
Terminal input and clean poweroff. The diagnostic fullscreen resize runs on the
UI owner and restores the original window state. It does not depend on an
unacknowledged synthetic F11 key. Private/hidden prompts remain excluded.

`fps parallel on|off` invalidates capture revisions so its benchmark really uses
the selected executor. `fps parallel-bench [fullscreen]` includes cold material
captures and the rest of composition, but not presentation. The public command
roundtrip includes rendered-prompt observation, deliberately paced monitor typing,
shell execution and logging; it is not key-to-paint latency. The scalar/AP paths
use different implementations, so isolated filter speedup is not a pure CPU-count
scaling claim. Actual scene gains are smaller than the filter-only gains.

Both final explicit accelerator matrices passed at 1/2/4 CPUs, including live
managed-root hold, exact scalar fallback while all APs are busy, and accelerated
reuse after release. Each guest's HMP `info kvm` response is retained, rather than
inferring the accelerator from the requested command line. The same ELF/ISO was
used throughout and the frozen hashes remained unchanged after ARM64 cross-build.

A KVM reader failure was reproduced from the retained serial log: the test woke
on `parallel bench verify=` and parsed a record ending halfway through
`parallel-us`. It had not observed a completed result at all. The reader now waits
for the complete, tagged, newline-terminated record. Four regressions exercise the
exact truncated prefix, every delivery split, final hash boundaries and wrong tags.
The operation is never replayed and pixel/owner requirements remain unchanged.

### Final local measurements

The following retained samples ran alongside other validation work. KVM/TCG
requests were explicit and monitor-confirmed. Compare each scalar/distributed
pair; do not treat differences between boots as CPU scaling guarantees. The
large filter includes native implementation improvements as well as parallelism.
Two-core KVM filter dispatch was slower in this contended run; this is not hidden
by selecting a more favorable preliminary sample. Whole-scene improvements remain
small because capture/layout/text/painting/presentation are not all AP consumers.


| Accelerator | CPUs | Large filter scalar / distributed (ms) | Ordinary scene scalar / distributed (ms) | Fullscreen scene scalar / distributed (ms) |
| --- | ---: | ---: | ---: | ---: |
| KVM | 1 | 15.359 / disabled | 11.227 / 12.014 | 13.939 / 13.104 |
| KVM | 2 | 6.854 / 13.428 | 11.023 / 10.679 | 13.144 / 12.193 |
| KVM | 4 | 5.027 / 2.237 | 12.072 / 11.109 | 13.144 / 12.056 |
| TCG | 1 | 322.043 / disabled | 113.293 / 113.233 | 121.022 / 121.082 |
| TCG | 2 | 318.981 / 23.597 | 119.406 / 115.037 | 141.723 / 131.571 |
| TCG | 4 | 332.473 / 19.106 | 114.822 / 112.165 | 121.240 / 117.622 |

At four CPUs, the paired ordinary/fullscreen scene samples improved by about
8% on KVM and 2–3% on TCG. These are composition times, not whole-application or
presentation latency guarantees. One-CPU mode deliberately uses the original
scalar path; differences between its on/off scene samples are timing noise.

## Controller and release behavior

The common controller set now restores pinned kernel dependencies and builds the
current PatcherFixup before its IL/native regressions. Clean CI/pre-build checks
must not silently execute an earlier tag's adapter binary. Preparation failure
stops before testing. This prepares tools/dependencies; it does not compile an ISO.

New sources add `b/native-compositor-smoke.py` to the mandatory release plan,
bringing a fresh source checkout to thirteen native gates. Older immutable tags
without the consumer keep their own original gate plan. Independent gates retain
the distributor's bounded concurrency and exact-input success receipts.

## Remaining boundary

General scheduler queue transfer, per-CPU raw process state, service/driver
concurrency and real installed filesystem/network contention are still required
C4 exits, ordered once in the [component sequence](../../components/MULTICORE.md#c4--migration-and-broad-concurrency).
CredentialContext is thread-local and defaults to Nobody, but this does not make
shared services AP-safe. NativeProcess retains its single serialized raw context,
captured service identity and filesystem gate. The process assembly still rejects
non-BSP entry before touching global bridge storage. C3 permanent owners remain
pinned and outside scheduler policy queues. No process migration, default managed
SMP, restart of a faulted CPU or repair of interrupted allocator state is claimed.

## Final local qualification

- `obj/c4-accepted-dotnet.log`: 851 host tests passed.
- `obj/c4-accepted-controller.log`: 441 common controller/bridge/IRQ/record tests passed;
  includes current dependency/adapter preparation before running the tests.
- `obj/c4-accepted-adapters.log`: 72 pinned runtime/driver regressions passed.
- `obj/c4-accepted-cpu.log`: 106 native CPU checks passed.
- `obj/c4-accepted-managed.log`: complete C3 managed roots/GC/exception/fault/cancel,
  default-disabled/shutdown and rendered 1/2/4-CPU frame/input matrix passed.
- `obj/c4-accepted-process.log`: full AP-enabled installed process gate passed in
  720.430 seconds, including all 64 exact byte pipelines, held managed roots,
  account/permission and filesystem/fault checks, executable upgrade, fonts/manuals,
  GUI/local canonical/raw/EOF/cancel restoration and independent disk audits.
- `obj/c4-compositor-kvm-complete` and `obj/c4-compositor-tcg-complete`: complete
  exact filter, ordinary/fullscreen scene, live held-root fallback/reuse, public
  Terminal command and clean shutdown matrices passed; each contains JSON timing
  records and actual accelerator replies. The explicit TCG run complements the
  hardware-accelerated run; neither is VMware/physical qualification.
- `obj/c4-accepted-arm64.log`: ARM64 Debug cross-build passed; no ARM boot/SMP claim.
- `obj/c4-documents.log`: documentation links/anchors and single-tracker checks.

This is focused source-level local qualification of the frozen binary, not the
full clean-source thirteen-gate pre-tag acceptance or a distributor/installed-VM
release. No package was signed, published or tagged for this batch.
