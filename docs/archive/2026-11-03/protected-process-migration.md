# C4 protected process migration and broad concurrency

Work owner: **C4** in the [master roadmap](../../ROADMAP.md).
[Runtime contract](../../MULTICORE.md) · [component](../../components/MULTICORE.md).

## Execution decision

C4 schedules complete protected user contexts over owned native queues. The BSP
retains each invocation's managed service actor, captured UID/groups/cwd, handles
and maintenance lease. Arbitrary Cosmos managed `Thread` stacks stay on the BSP;
C3's permanent TLS/TLAB/exception owners stay pinned. This is the chosen execution
architecture for the independently packaged CLI and graphical applications.
Increasing the general managed scheduler CPU count would also move unqualified
driver/UI/credential code and is deliberately excluded from this admission.

Seven hash-derived AP bridges own separate register/FP/CR3/GS/GDT/TSS/IDT save
areas and interrupt stacks. Entry requires a currently leased context. Syscalls,
reschedule IPIs and local one-shot timer quanta return complete user frames for
ready requeueing. A Running frame never moves. Private fault and GC IST ownership
survives bridge installation/restoration, and native GS is restored before its
native IDT becomes active again. Device delivery remains BSP-owned.

The eight-descriptor pool transfers Ready contexts within affinity under the
pool-before-CPU lock order. Runnable tickets and retained storage are separate;
completion, repeated cancellation, target failure and orderly shutdown cannot
subtract a ticket twice. Terminal reaping retries transient contention. Queued
cancellation completes without relying on an IRQ arriving. Failed owners retain
their descriptors, while healthy siblings remain usable. Metadata damage and an
unacknowledged stop fail-stop before borrowed stack/context storage is freed.

The broker also drains an admitted operation before propagating callback or
collection exceptions. No AP performs managed service dispatch or retains a
managed reference. Raw user pages remain private unmanaged allocations, rooted by
their BSP invocation; C3 managed participants keep their existing stopped-root
epoch contract.

## Service ownership audit

| Operation | Ownership and concurrency |
|---|---|
| User instructions and private toolkit rasterization | AP execution, complete saved frames and invocation-local address spaces |
| File namespace/metadata, transport setup and mutations | BSP service actor with the existing service/VFS gates and captured credentials |
| Stream and peer reads | Owned invocation handles; waits may overlap without holding the filesystem service gate |
| Window/input/session mutation and frame publication | UI/BSP owner; clients publish validated complete private frames |
| Package activation, account/session defaults and power changes | Existing root/maintenance exclusion; no direct AP driver or configuration access |
| Managed allocation and collection | C3 publication and stopped-root epochs; fixed managed owners remain pinned |

`ZENITH_CPU_MASK` is decimal 1..255 or hex `0x01`..`0xff`. Default 255 permits
BSP fallback; an AP-only mask never silently expands. `ps --cpu` reports the last
owner, mask and migration count through the existing UID filter. The default BSP
bridge retains its older interrupt-budget behavior for CPU-bound loops. Prompt
CPU-loop cancellation is qualified on the AP path; default AP activation remains
an operator/distributor acceptance decision.

## Candidate and repeatable checks

Local environment: .NET SDK **10.0.114**, Cosmos **3.0.89**, QEMU **11.1.3**,
512 MiB disposable guests. Fresh release gates run with four bounded workers.

- ELF SHA256: `4b36a0a380e0099f47af0966241d30d14931dd16f6296bea372b143c04f1d973`.
- ISO SHA256: `8aa10e09d6e9067160a6662e9507263876ee73375529c411aa401ace3b100ac0`.
- Main evidence: `obj/c4-final-qualified/` and `obj/c4-final-qualified.log`.
- Final SMP requalification: `obj/c4-final-smp-matrix.log`;
  aggregate evidence map: `obj/c4-completed-qualification.json`.
- Additional installed contention: `obj/c4-final-contention.log`.
- TCG: `obj/c4-final-tcg-complete.log`.
- Eight CPUs: `obj/c4-final-eight-long.log`; expanded register replay:
  `obj/c4-final-registers-fixed.log`.
- Host/native broker/queue checks: `obj/c4-final-process-regressions.log`.

853 host tests, 469 common controller tests, eight copied-runtime adapter checks,
x64 build and ARM64 cross-build passed. All **14 mandatory native gates** passed
against the artifact hashes above. Thirteen passed in the fresh four-worker plan;
the last SMP fixture's sampling assertion was corrected and its complete 1/2/4-CPU
matrix rerun independently (**322.3 s**), preserving the failed original evidence.
The combined plan's original failure marker is retained rather than overwritten
with a fabricated success log. No release was signed or tagged.

`native-smp-smoke.py` is added to the mandatory fresh gate plan. Its installed
1/2/4-CPU matrix covers strict affinity/refusal, hardware execution witnesses,
saved register/FP state, simultaneous deterministic CPU work, concurrent stream/
filesystem/network operations, contained fault/NX/W^X, timeout, prompt AP cancel,
post-cancel reuse, disabled fallback, clean poweroff and independent ext2 audits.
Additional eight-CPU witnesses exercise APs 1–7. A no-build replay can copy a
retained disposable fixture with `--prepared`; it verifies the exact kernel and
upgrades test packages on the copy, preserving the original disk.

The full process gate now uses four CPUs, with one parked C3 managed-root owner
and other APs available to native programs. Its 64 exact byte pipelines precede
fault/cancel, filesystem permissions/handles, signed executable replacement and
GUI/local console stream/secret-input restoration. The graphics, CLI, network,
Settings and material gates also exercise AP-enabled installed clients.

The focused Settings contention fixture streams a full MiB while a real Meridian
client handles input, applies settings and presents host materials. A host event
releases the stream only after an acknowledged rendered material frame; no click
or application mutation is replayed. The committed download is compared byte for
byte after independent ext2 checking. Its complete save/reboot/UID/upgrade and
launcher checks passed in **264.8 s** on KVM.

## Measurement interpretation

The sustained payload repeats a known 20-million-step integer recurrence sixteen
times. All outputs match an independently calculated 64-bit reference. Three
pinned jobs execute on different APs, with timestamp intervals proving overlap.
Three four-CPU samples had a median summed-job-duration/elapsed-span ratio of
**2.57**; eight-CPU samples gave **2.57**, with three jobs in both cases. These are
concurrent execution witnesses, not whole-system speedup claims. UART delivery,
program loading and service overhead are included in separate
wall timings. The initial short payload finished before later jobs had loaded on
fast KVM and was lengthened rather than weakening the overlap requirement.

CPU witnesses are sampled after individual broker boundaries, before printing.
The first probe printed three pieces between samples and could alias a three-AP
rotation. Stealing may also legitimately keep a short task on a warm CPU: every
observed owner must satisfy affinity, but a fast invocation need not visit multiple
CPUs. Ready transfer is forced in the production native queue tests, and the local
eight-CPU guest additionally observed one invocation on APs 1–7. Failed fixture
logs remain retained; only the corrected SMP gate is repeated on the same kernel.

The compositor gate separately checks identical ordinary/fullscreen scene pixels,
scalar fallback over held managed roots, released-owner reuse, focused public input
and presentation. Pure filter scaling and end-to-end composition remain different
measurements. Full application gains depend on how much work stays in BSP services.

## Acceptance boundary

Local KVM/TCG results and cross-builds do not establish distributor, VMware,
installed operator-VM or physical-machine acceptance. Those remain **Q2/H1**.
No AP work is enabled solely because firmware reports more CPUs. This batch does
not publish a kernel tag or change the default boot policy.
