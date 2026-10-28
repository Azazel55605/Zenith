# Shared settings stage — 2026-10-28

Owners: S1/U1/D3; installed acceptance Q2 in [master roadmap](../../ROADMAP.md).

## Scope

Captured-UID personal appearance profiles with live system-default inheritance and
confirmed reset; root-only system choices; searchable keyboard/time-zone/registered
session selectors; actual read-only primary-network/account information. Trusted
host and native desktop clients resolve the session's personal palette/materials.
Files remains excluded. Driver-dependent controls, elevation, account lifecycle,
network mutations and automatic schedules retain WIP ownership.

## Validation boundary

Host regressions cover isolated profiles, inheritance/revision conflicts, failed
storage and malformed/future preservation, public data filtering and legacy/v2
contracts. C UI tests cover scope/reset, searchable selectors and dirty/error state.
Native acceptance exercises separate UIDs, protected raw profile access, root-only
writes, real CLI/GUI choices, inherited reset and reboot persistence on disposable
disks. Full clean-source Release qualification and publication are recorded below
when complete. The installed operator checklist remains separate; no claim is made
that the earlier 0.0.26 wheel/drag freeze has been reproduced or cured.

An initial native permission assertion exposed the recovery builtin cat returning
success after reporting permission denied. No profile bytes were disclosed. The
fixture now checks the native filesystem open result (-EACCES) directly and requires
its explicit acknowledgement; it does not infer permissions from that builtin's status.

Review also found account-file reads in each non-root desktop appearance poll.
Loaded profile snapshots now stay in memory; actual native admission and every
save still validate identity. Failed display loads use a boot-local fallback cache
instead of retrying malformed files each frame. Regression loops assert no account
or filesystem reads across 200 display polls, including malformed fallback. Explicit
successful service saves clear that failure cache; direct root file edits require
restart/reload through the normal boot route.
