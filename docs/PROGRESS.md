# Active work

Updated **2026-10-16**. This is a short handoff, not an append-only changelog.
[Status](STATUS.md) summarizes the system; [Roadmap](ROADMAP.md) orders future work.
Completed evidence lives in the [archive](archive/README.md).

## Current handoff: authentication validation ready for server retry

The shared authentication policy is implemented and locally verified. It separates
secret prompts from hash/save completion, applies to unchanged tagged harnesses,
and preserves real results, rejection checks and hash strength. Credentials are
never replayed. See [completed evidence](archive/2026-10-16/authentication-validation.md)
for diagnosis, policy, commands and limits.

- [x] Trace the server's 30-second password-update failure and input lifecycle.
- [x] Implement shared bounded waits and secret-free phase diagnostics.
- [x] Pass 85 policy/controller/status/profile/accelerator/recovery regressions,
  including unchanged-tag compatibility.
- [x] Pass final-code TCG login/session/styling acceptance: 69 checks, 242.241 s.
- [x] Pass final-code TCG native-program acceptance: 246.195 s, unchanged ISO.
- [ ] Pull the controller on the server and retry `kernel/v0.0.13`; confirm all
  gates, signing and publication, then installed upgrade acceptance.

Previous controller work: [bounded repository staging](archive/2026-10-15/repository-stage-budget.md)
and [failure diagnostics](archive/2026-10-15/repository-stage-investigation.md).

## Waiting for external acceptance

Kernel **0.0.13** and Sunbeam **0.2.0** are committed/tagged and locally verified
(`f502085`). Full server gates/publication and installed VM acceptance have not
been reported. Kernel **0.0.12** and the login/resource packages are operator
confirmed. See [Status](STATUS.md#release-and-installation-boundary).

After resolving the release failure, resume [native userland](components/USERLAND.md):
first finish release acceptance, then select the next bounded input/write slice.
Do not mark M5 complete or start unrelated desktop/multicore implementation from
this handoff alone.
