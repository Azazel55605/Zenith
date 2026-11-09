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

## PK1–PK5 — Remaining steps

- [ ] PK1: define `/etc` default/merge/conflict semantics and restart notices.
- [ ] PK4: preflight running-kernel capabilities and schema/rollback compatibility;
  coordinate activation with matched recovery generations (R2), not fake atomicity.
- [ ] PK2: rotate/revoke signing keys and TLS certificates; define roles and rollback
  protection, renewal and compromise recovery procedures.
- [ ] PK3: perform a real server backup restore drill; verify bounded retention with
  active/pinned builds and preserve diagnostics. Operator-reported backups alone
  do not establish restore success.

## PK5 — Guest cache maintenance

Requested 2026-11-09 after the installed VM ran out of partition space while
`pkg update` downloaded 0.0.48. Guest package caches have no automatic retention
policy yet; the distributor's existing artifact cleanup is a separate system.

- [ ] Add `pkg clean`, including a dry-run/preview and bytes reclaimed. Start with
  re-downloadable archives below `/var/lib/zenith/pkg/repositories`; define age,
  size/free-space budgets and explicit protection for pinned offline archives.
- [ ] Use one cleaner/policy for the CLI and a periodic system service, with
  configurable cadence and a way to disable automatic cleanup. Keep background
  work bounded and report failures without blocking the desktop/session.
- [ ] Coordinate with package/boot maintenance and active downloads. Never remove
  live staging or transaction files; reclaim abandoned staging only after proving
  its owner is no longer active. Refuse linked/unexpected cache paths.
- [ ] Preserve installed payloads/databases, signed repository indexes and their
  anti-rollback generation floors, trust material, boot generations and recovery
  state. Cache cleanup is separate from kernel-generation retention (R2).
- [ ] Preflight download/staging space for `pkg update` and report the failing
  filesystem, required/available bytes and cleanup guidance for out-of-space
  failures instead of only `download write failed`.
- [ ] Validate full-disk recovery, preview/actual cleanup agreement, simultaneous
  downloads/updates, interrupted cleanup and reboot persistence. Confirm guest
  behavior on an installed system before marking PK5 accepted.

## Source and operations

[Guest](../../src/Core/Packages), [host tool](../../tools/Zenith.PackageTool),
[recipes](../../deploy/distributor/recipes.json).
[Format and activation](../PACKAGES.md), [online client](../ONLINE-PACKAGES.md),
[server](../PACKAGE-SERVER.md), [deployment runbook](../../deploy/distributor/README.md).
Use package/repository/kernel/native/session gates; Q1/Q2 own release acceptance.
