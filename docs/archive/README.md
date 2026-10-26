# Documentation archive

Archived material preserves decisions and test evidence. It is **not current
status**; “pending”, “active” and “next” apply to the original recording date.
Start with [current status](../STATUS.md) or [active work](../PROGRESS.md).

## Investigation records

- [2026-10-26 CLI process/filesystem foundation](2026-10-26/cli-process-foundation.md): environment, foreground raw input, owned processes/background jobs, namespace services and signed base/filesystem package dispatch; validation boundaries.

- [2026-10-17 scheduler late wakes](2026-10-17/scheduler-late-wake.md): scheduler, conservative GC and GUI EOF faults behind pipeline failures; final acceptance and release boundary.

- [2026-10-17 stale TCP flow recurrence](2026-10-17/tcp-stale-flow.md): failed SYN/ACK evidence, recovery after waiting and fixed per-boot port allocation; cause and further fix remain under investigation.

- [2026-10-16 intermittent TCP connections](2026-10-16/tcp-connect-reliability.md): connect timeout retirement, bounded retry and guest HTTPS regression; VMware cause still under investigation.

- [2026-10-16 distributor delivery diagnostics](2026-10-16/distributor-delivery-diagnostics.md): 0.0.14 built/published; final DNS failure, dashboard visibility fix and recovery without rebuilding.

- [2026-10-16 kernel update ancestor failure](2026-10-16/kernel-update-ancestor-investigation.md): confirmed ext2 inode corruption, serialized operations/private buffers, observer fixes and full local acceptance.

- [2026-10-16 authentication validation](2026-10-16/authentication-validation.md): shared prompt/completion policy and immutable-tag compatibility.

- [2026-10-15 bounded staging budget](2026-10-15/repository-stage-budget.md): server TCG/deadline confirmed; scoped inactivity/overall budget and retry.

- [2026-10-15 repository staging](2026-10-15/repository-stage-investigation.md): existing 0.0.13 passes locally; server cause pending, diagnostic/controller changes validated.

## Completed slices

- [2026-10-17 native streams](2026-10-17/native-streams.md): live standard descriptors, canonical input, bounded byte pipelines, independent wc/head/tail and full local acceptance; publication pending.

- [2026-10-17 dashboard retries](2026-10-17/dashboard-retry.md): failed/interrupted build controls, restricted publisher queue helper and background service execution.

- [2026-10-16 native input and writes](2026-10-16/native-input-and-writes.md): bounded stdin/file services, independent tee, ISO build repair, full acceptance and keyboard-profile limitations.

- [2026-10-16 release validation stability](2026-10-16/release-validation-stability.md): common candidate/CI gate path and early controller/environment checks.

- [2026-10-15 documentation overhaul](2026-10-15/documentation-overhaul.md): structure, preservation and link checks.

## Baseline preserved during the 2026-10-15 overhaul

| Record | Contents |
|---|---|
| [Execution ledger](2026-10-15/PROGRESS.md) | Full original 2,504-line slice history, validation results, failures and corrections through native filesystem unbundling |
| [Old roadmap](2026-10-15/ROADMAP.md) | M0–M7/S/P/R tables, estimates and original sequencing |
| [System planning baseline](2026-10-15/SYSTEM-PLAN.md) | Detailed original session, desktop, Settings and app requirements |
| [Package design baseline](2026-10-15/PACKAGE-MANAGER.md) | Original format, trust, transaction and client design discussion |
| [Server planning baseline](2026-10-15/PACKAGE-SERVER.md) | Provisioning sequence and historical connectivity/HTTPS investigation |
| [Userland planning baseline](2026-10-15/USERLAND.md) | Original migration and runtime investigation |

The [project history](../HISTORY.md) is a narrative map. The
[technical notes](../TECHNICAL-NOTES.md) retain earlier measurements and context.
For new completed slices, add a small dated record here instead of growing the
active progress page. Snapshot source is the documentation at `f502085`; banners
and relative-link rebasing are the only archival edits in this reorganization.

- [2026-10-18: Server-only native pipeline stall](2026-10-18/native-pipeline-server-stall.md) — unresolved distributor failure, exact-artifact comparisons and bounded failure snapshots.
