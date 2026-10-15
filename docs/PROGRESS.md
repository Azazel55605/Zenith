# Active work

Updated **2026-10-15**. This is a short handoff, not an append-only changelog.
[Status](STATUS.md) summarizes the system; [Roadmap](ROADMAP.md) orders future work.
Completed evidence lives in the [archive](archive/README.md).

## Current slice: repository kernel-staging release failure

**Scope:** investigate kernel 0.0.13's `P4_KERNEL_STAGE=0` failure; preserve full
signed delivery, actual staging and reboot checks. The reported failure is in
native acceptance after compilation, not a compiler error.

- [x] Inspect tagged repository harness and controller status/timing adapters.
- [x] Request the retained guest serial/timing evidence from the failed server run.
- [x] Run the full repository gate with the existing 0.0.13 binary: 92 checks pass
  under TCG in 450.243 s; kernel-staging step passes in 216.511 s.
- [x] Improve failure diagnostics and detect actual unexpected statuses promptly,
  without replaying updates or loosening acceptance.
- [x] Run repository/native checks before the long kernel recovery matrix; retain all gates.
- [x] Confirm server TCG policy and exact 600.034-second timeout; copy reached
  100% before the harness terminated QEMU. No guest staging error was shown.
- [x] Implement a scoped advancing-progress budget: 600 seconds idle, 1800 hard;
  duplicate/regressing progress and scheduler noise cannot renew it.
- [x] Real guest: progressing operation completes after its nominal budget; idle
  operation still fails. Subsequent command and clean poweroff pass.
- [x] Full repository acceptance: 92 checks pass in 424.057 s, including reboot,
  confirmation and independent ext2 audit (combined staging: 191.696 s).
- [x] 61 controller/budget/profile tests pass, including stalled/noisy/duplicate
  progress, hard cutoff, wrong statuses and exact-operation scoping.
- [x] Prepare the validated controller fix and documented same-tag background retry.
  [Budget evidence and remaining operator action](archive/2026-10-15/repository-stage-budget.md).
- [x] Controller/status/profile regressions: 50 tests pass; release/poller: 38 pass.
- [x] Real guest: reject status 1, record diagnostic, accept subsequent status 0,
  then cleanly power off. [Evidence and changes](archive/2026-10-15/repository-stage-investigation.md).

**Finding:** the server used TCG (406.81 QEMU CPU seconds in 600.034 wall
seconds), completed download/signature checks and all kernel-copy writes, then
hit the single aggregate deadline before the verified staging result. Local
acceptance finishes the same operation in 216.511 seconds. A progress-aware test
budget addresses premature termination without treating progress as success;
the server's eventual completion still needs a retry. No kernel binary or tag changes.

Previous completed work: [documentation overhaul](archive/2026-10-15/documentation-overhaul.md).

## Waiting for external acceptance

Kernel **0.0.13** and Sunbeam **0.2.0** are committed/tagged and locally verified
(`f502085`). Full server gates/publication and installed VM acceptance have not
been reported. Kernel **0.0.12** and the login/resource packages are operator
confirmed. See [Status](STATUS.md#release-and-installation-boundary).

After resolving the release failure, resume [native userland](components/USERLAND.md):
first finish release acceptance, then select the next bounded input/write slice.
Do not mark M5 complete or start unrelated desktop/multicore implementation from
this handoff alone.
