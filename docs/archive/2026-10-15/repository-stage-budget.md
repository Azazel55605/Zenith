# Repository staging: bounded progress budget

## Confirmed server evidence

The operator reports effective `ZENITH_VALIDATION_ACCELERATOR=tcg` and a failed
`P4_KERNEL_STAGE` wait of **600.034 seconds**, with **406.81 QEMU CPU seconds**,
103,282 serial bytes and 38,436,864 host-written bytes. The guest completed HTTPS
download, verified the signed kernel and reached **100% kernel copy**. QEMU was
then terminated by the harness. No explicit guest staging error was shown.

This rules out accidental KVM selection for this run. The confirmed validation
failure is expiry of the single 600-second aggregate window. Copy percentage
excludes flush, readback, final verification and boot publication; the exact
post-copy step/duration cannot be recovered from that old log. The server still
needs to prove eventual completion on retry. A progress budget is not a storage
throughput improvement or proof that every possible stall is resolved.

## Controller change

For the exact `pkg update --yes` / `P4_KERNEL_STAGE` status operation:

- Advancing recognized milestones refresh a **600-second inactivity limit**.
- An independent **1800-second overall limit** always wins.
- Duplicate/regressing percentages, scheduler/GC traces and growing logs do not
  extend either bound; expired deadlines cannot be revived.
- Distinguish the kernel download from preceding small data downloads so their
  100% progress does not hide subsequent kernel transfer progress.
- Retain actual exit-status checking, wrong-status rejection, process-exit failure,
  read-only status recovery and every reboot/hash/confirmation/filesystem gate.
- Show minute heartbeats with phase and remaining idle/overall budgets, explicitly
  distinguishing 100% copied from a verified staging result. Save budget policy
  alongside the nominal timeout in wait metrics.

All other operation deadlines remain fixed. The controller applies to the existing
immutable 0.0.13 tag; no product binary, signing identity or release tag changes.
See [operator retry steps](../../../deploy/distributor/AUTOMATION.md#bounded-progress-budget-for-combined-kernel-staging).

## Validation

**61** status/budget/profile/accelerator/validation-controller tests pass. Coverage
includes completion beyond the original aggregate window, idle failure, repeated
and regressing progress, scheduler noise, absolute cutoff, lost-echo readback,
wrong real statuses and exact-operation scoping.

A disposable live guest exercised the policy on controlled progress/sleep commands
with deliberately short budgets: actual completion at **8.169 seconds** passed a
6-second nominal budget, while a command without progress failed at **3.017
seconds** against a 3-second idle budget. Subsequent real status 0 and clean
poweroff passed. This probe explicitly enabled the policy for its harmless test
commands; production operation scoping remains covered by regressions. Evidence:
`/tmp/zenith-stagebudget-probe.log`, `/tmp/zn-stagebudget-probe`.

Full unchanged-product repository acceptance uses:

```sh
ZENITH_VALIDATION_ACCELERATOR=tcg python3 -B deploy/distributor/validation-runner.py \
  tools/repository-smoke.py /tmp/zn-repo13-budget
```

All **92 repository checks pass in 424.057 seconds**, including actual reboot,
confirmation, package recovery/permissions and independent ext2 audit. The combined
update passes in **191.696 seconds** and records the advancing-stage budget, actual
TCG and genuine successful exit. This local run does not establish server publication
or installed operator acceptance. See [active progress](../../ROADMAP.md#current-focus-and-execution-order). Evidence:
`/tmp/zenith-repo13-budget.log`, `/tmp/zn-repo13-budget`.
