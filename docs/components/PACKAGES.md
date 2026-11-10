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
`pkg update` downloaded 0.0.48. Implemented locally 2026-11-10
([policy](../../src/Core/Packages/PackageCachePolicy.cs),
[cache](../../src/Core/Packages/NativePackageCache.cs),
[service](../../src/Core/Packages/NativePackageMaintenance.cs)).

- [x] `pkg clean [--dry-run]` removes re-downloadable archives (`DIGEST.zpk`) and
  abandoned `.incoming`/`.pkg-new` stages in `/var/lib/zenith/pkg/repositories`, and
  reports each file and the bytes reclaimed; the preview lists exactly the same set.
  Signed indexes (`.zri`, anti-rollback floors) stay; links, non-files and unknown
  names are kept and reported. A linked or non-directory cache path is refused.
- [x] One policy for the command, the cleanup after each completed `pkg update`,
  `install`, `upgrade` and `kernel update` (their archives are installed or staged),
  and a periodic service (two minutes after boot, then every six hours: archives older
  than a day, stages older than an hour). `pkg clean auto on|off` persists
  `/etc/zenith/package-cache.conf`; off disables both automatic paths.
- [x] Every path holds the package service gate: no download, transaction, installer
  or kernel update owns a stage while cleanup runs; a busy service defers the
  periodic run. Installed payloads, databases, trust, boot generations (R2) and
  transaction staging outside the cache are not touched.
- [x] Downloads check root space first (archives plus installed payloads plus 4 MiB),
  clean the cache automatically when that makes room, and otherwise fail with the
  filesystem, required and available bytes and guidance.
- [ ] Installed acceptance: a full root during `pkg update`, interrupted cleanup and
  reboot on the operator's VM. Local: repository gate (preview/actual agreement,
  archives gone after transactions, auto off/on) and host policy tests.

## Source and operations

[Guest](../../src/Core/Packages), [host tool](../../tools/Zenith.PackageTool),
[recipes](../../deploy/distributor/recipes.json).
[Format and activation](../PACKAGES.md), [online client](../ONLINE-PACKAGES.md),
[server](../PACKAGE-SERVER.md), [deployment runbook](../../deploy/distributor/README.md).
Use package/repository/kernel/native/session gates; Q1/Q2 own release acceptance.
