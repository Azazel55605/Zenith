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
- [ ] Identify the server-specific failure from filtered guest output and timing/backend evidence.
- [x] Controller/status/profile regressions: 50 tests pass; release/poller: 38 pass.
- [x] Real guest: reject status 1, record diagnostic, accept subsequent status 0,
  then cleanly power off. [Evidence and changes](archive/2026-10-15/repository-stage-investigation.md).

**Finding:** compilation succeeded. The existing binary passes the exact full
repository scenario locally. The server tail supplied so far contains scheduler/GC
traces only, which do not identify the update stage or exit status. Filtered server
output is requested. The underlying server failure is not claimed fixed.
No product binary, release tag or validation deadline changed.

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
