# Packages and distribution

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md)

Work IDs below are detailed steps of the master tracker, not a separate schedule.

## Implemented boundary

Signed `.zpk` and separate kernel containers, named core/extra catalogs, freshness
floors, dependency/conflict/ownership checks, protected local edits and ext2
staging/undo support installed updates. `pkg update` plans data plus kernel but
commits them separately. Native code is verified again on launch.

Base/fs/net tools, manuals/docs/fonts, native graphical clients and styling
resources are actual recipes. `zenith-session` is branding; the greeter is still
trusted. `zenith-style` is a dependency-only meta, not an executable desktop.

Distributor immutable tags/build/gates/sign/publish, verification retry, useful
logs, version grouping, storage reporting, automatic cleanup and retry controls
already exist. Old 0.0.13 retry requests are superseded historical investigations.

## PK1–PK4 — Remaining steps

- [ ] PK1: define `/etc` default/merge/conflict semantics and restart notices.
- [ ] PK4: preflight running-kernel capabilities and schema/rollback compatibility;
  coordinate activation with matched recovery generations (R2), not fake atomicity.
- [ ] PK2: rotate/revoke signing keys and TLS certificates; define roles and rollback
  protection, renewal and compromise recovery procedures.
- [ ] PK3: perform a real server backup restore drill; verify bounded retention with
  active/pinned builds and preserve diagnostics. Operator-reported backups alone
  do not establish restore success.

## Source and operations

[Guest](../../src/Core/Packages), [host tool](../../tools/Zenith.PackageTool),
[recipes](../../deploy/distributor/recipes.json).
[Format and activation](../PACKAGES.md), [online client](../ONLINE-PACKAGES.md),
[server](../PACKAGE-SERVER.md), [deployment runbook](../../deploy/distributor/README.md).
Use package/repository/kernel/native/session gates; Q1/Q2 own release acceptance.
