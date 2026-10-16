# Active work

Updated **2026-10-16**. This is a short handoff, not an append-only changelog.
[Status](STATUS.md) summarizes the system; [Roadmap](ROADMAP.md) orders future work.
Completed evidence lives in the [archive](archive/README.md).

## Current handoff: common release acceptance locally verified

Release pipeline stability work is implemented. CI and pre-tag validation now use
the distributor's native gate plan; current controller regressions and native
prerequisites run before server kernel compilation. Default candidate validation
requires a clean source commit and refuses source changes; existing-artifact runs
record hashes and reject changed files. No product tag/version changed.

- [x] Audit recurring failures and CI/distributor coverage gaps.
- [x] Add unsigned candidate validation and early controller/environment checks.
- [x] Align CI, including login/session coverage and host/controller dependency.
- [x] Pass 99 controller regressions, 18 release fixtures and 620 Release host tests.
- [x] Pass the full common TCG run: 69 login checks, 92 repository checks,
  native-program acceptance and all 169 kernel recovery checks; unchanged ISO/ELF.
- [x] Document pre-tag use, evidence retention and source/artifact boundaries.
- [ ] Pull the controller on the server when idle and observe the next complete
  build; hosted CI and installed acceptance remain separate external steps.

See [completed evidence](archive/2026-10-16/release-validation-stability.md) and the
[pre-tag workflow](../deploy/distributor/AUTOMATION.md#before-the-next-kernel-tag).
The operator is independently retrying 0.0.13 using the previous
[authentication fix](archive/2026-10-16/authentication-validation.md) (`758d7af`);
no server result has been reported yet. Avoid changing a running controller.

## Waiting for external acceptance

Kernel **0.0.13** and Sunbeam **0.2.0** are committed/tagged and locally verified
(`f502085`). Full server gates/publication and installed VM acceptance have not
been reported. Kernel **0.0.12** and the login/resource packages are operator
confirmed. See [Status](STATUS.md#release-and-installation-boundary).

After resolving the release failure, resume [native userland](components/USERLAND.md):
first finish release acceptance, then select the next bounded input/write slice.
Do not mark M5 complete or start unrelated desktop/multicore implementation from
this handoff alone.
