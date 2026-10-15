# Packages and distribution

[Documentation](../README.md) · [Current status](../STATUS.md)

**State:** signed installed updates, core/extra catalogs and dedicated home-server
automation work. Operator has installed packages by name. New kernel/native file
tool releases still need the acceptance recorded in [Status](../STATUS.md).

## Implemented steps

- [x] Build bounded signed `.zpk` containers and separate kernel containers.
- [x] Resolve dependencies/version constraints/conflicts and file ownership.
- [x] Stage installed ext2 transactions, protect local edits, undo/recover and verify.
- [x] Authenticate private catalogs, freshness and persistent generation floors.
- [x] Support named search/plan/install, installed data upgrade, remove and verify.
- [x] Provide `pkg update` for installed data plus kernel staging, with confirmation.
- [x] Package independent manuals, fonts, docs and protected Sunbeam executables.
- [x] Support branding/theme resources and a true dependency-only styling metapackage.
- [x] Build immutable package-specific tags; gate, sign and publish on the server.
- [x] Expose timer/run-now/build logs, expandable package versions and storage reporting.
- [x] Clean bounded stale build/scratch files; hide superseded finished/failed activity.

## What the package boundaries mean

| Package / recipe | Delivered responsibility |
|---|---|
| `zenith-kernel` | Kernel and remaining linked system/GUI code; reboot activation |
| `sunbeam-tools` | Protected freestanding native text tools; new runs use updated ELF |
| `zenith-docs`, `zenith-man-pages` | Documentation/manual data |
| `zenith-fonts`, `zenith-theme-default` | Verified runtime appearance resources |
| `zenith-session` | Greeter branding resources; session host still kernel-linked |
| `zenith-style` | Zero-payload dependencies selecting the theme/fonts |

Current recipes live in [recipes.json](../../deploy/distributor/recipes.json).
A future `base-tools`, `net-tools` or `fs-tools` identity is not an extracted
executable merely because the package planner knows its name.

`pkg update` coordinates planning but data and kernel have separate commit
boundaries. It is not an atomic whole-system snapshot. Kernel changes activate
after reboot and confirmation; appearance reloads at login. See the
[client workflow](../ONLINE-PACKAGES.md) before choosing a command.

## Remaining steps

- [ ] Retry the reported 0.0.13 server staging timeout with bounded progress
  budgets and verify completion. Actual TCG/deadline evidence is recorded in
  [the budget investigation](../archive/2026-10-15/repository-stage-budget.md).
- [ ] Complete server/installed acceptance for kernel 0.0.13 and Sunbeam 0.2.0.
- [ ] Unbundle more compiled tools as the [native ABI](USERLAND.md) grows.
- [ ] Specify configuration-file merges and restart/compatibility handling.
- [ ] Add trust rotation/revocation and broader public-mirror hardening.
- [ ] Define coordinated data/kernel recovery before promising atomic system rollback.
- [ ] Record an actual server backup restore test; automated TrueNAS backups are
  operator reported, restore acceptance is not.

## Source, operations and acceptance

Owners: [guest packages](../../src/Core/Packages), [host tool](../../tools/Zenith.PackageTool),
[distributor](../../deploy/distributor).
References: [package design](../PACKAGE-MANAGER.md), [format/transactions](../PACKAGES.md),
[online client](../ONLINE-PACKAGES.md), [server overview](../PACKAGE-SERVER.md).
Operational commands belong in the [deployment runbook](../../deploy/distributor/README.md),
[automation](../../deploy/distributor/AUTOMATION.md) and
[dashboard setup](../../deploy/distributor/DASHBOARD.md).

Preserve [package](../../tools/package-smoke.py), [repository](../../tools/repository-smoke.py),
[kernel](../../tools/kernel-update-smoke.py), [native](../../tools/native-process-smoke.py)
and [login](../../tools/login-session-smoke.py) gates where applicable. Require
signature/tamper/replay/expiry/conflict/local-edit/cancel checks, reboot persistence
and independent filesystem audit. Complete state means the controller finished;
client acceptance is a separate observation. Historical results:
[archive](../archive/2026-10-15/PROGRESS.md).
