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
disks. Full clean-source Release qualification and the release handoff are recorded below. The installed operator checklist remains separate; no claim is made
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

## Clean Release qualification

Qualified source: `0d8663598a78f799581786d01a126df511322e7f`.
User-authorized release set: `kernel/v0.0.27`, `zenith-settings/v0.3.0` and
`zenith-desktop-tools/v0.2.0`, all at that exact source commit. Earlier viewport,
material and trusted-app changes are included through its ancestry. No theme or
manuals payload changes require a new tag in this batch.

The ordinary pre-tag command rebuilt Release from clean source:

```sh
PATH="$PATH:/home/azazel/.dotnet/tools:/usr/sbin:/sbin" \
ZENITH_VALIDATION_ACCELERATOR=tcg \
python3 -B deploy/distributor/validate.py --keep obj/shared-settings-release-0d86635
```

- 782 host tests and 240 controller regressions passed.
- C renderer/state tests passed ASan/UBSan; Settings and desktop-tools built with
  GCC and Clang. Documentation validation covered 95 files, 43 work IDs and 43 pages.
- All ten mandatory native gates passed on local disposable QEMU/TCG disks:

| Gate | Seconds | Result |
|---|---:|---|
| Login/session | 236.9 | 71 checks, including hidden authentication and transcript audit |
| Repository | 414.2 | 94 checks, signed combined update, reboot and cache recovery |
| Settings | 288.7 | Fresh signed install, CLI/GUI shared choices, captured UID/private reads, inheritance/reset, public data, upgrade, rapid wheel/maximize and reboot/ext2 audit |
| System-app style | 39.0 | Dark/light Terminal/Editor, pointer Save, guarded close |
| Materials | 83.9 | Foreground/backdrop rules, fallback, retirement and ext2 audit |
| Graphics | 159.5 | 48 checks, input/focus isolation, lifecycle and ext2 audit |
| Native network | 292.9 | 72 checks, TLS, full kernel-sized transfer and owned cleanup |
| Native CLI | 247.7 | 100 checks, raw input/fault/cancel restoration |
| Native processes | 771.0 | 64 consecutive exact 163840-byte pipelines, fault/cancel, all terminal hosts and disk audit |
| Kernel update | 710.9 | 169/169 checks, interrupted staging/menu recovery, external ISO, confirmation/rollback, broken-kernel manual fallback and filesystem audits |

An additional `native-materials-smoke.py --material-light` run passed in 84.076
seconds, including solid/tint/blur, opaque content/controls, movement and independent
ext2 audit. It used the same unchanged Release ELF/ISO.

Final Release SHA256:

```text
Zenith.elf  5a214cb6c65189dc4d817c91cef85eb8f4ca7709f542987c72e44bb19a778437
Zenith.iso  8c7ae85649b724f9e58784cd9a3e83cbda78fdfa3671e24f03b2249ddc44e977
```

One earlier targeted Debug run stopped when the rendered mouse acknowledgement
missed a one-pixel destination. Its prepared replay passed unchanged, followed by
the fresh full Release gate above. No click retry, relaxed pixel check or timeout
increase was added. This is not proof of the earlier installed-VM freeze's cause
or resolution. A separate first Release run was intentionally stopped after review
found per-frame account-file reads; only the corrected source above is qualified.

## Distributor and installed handoff

These are local qualification results. The distributor still builds/signs/publishes
from the release tags; its result and the operator's installed acceptance are Q2.
Install and boot-confirm kernel 0.0.27 before upgrading the clients, which require
the new personal/shared-settings capabilities. Follow the [VM checklist](../../testing/SETTINGS-VM-TEST.md),
especially personal/system scope, two-account login/reboot inheritance, selectors,
read-only information and wheel/drag behavior. Direct root edits to protected profile
files require normal reboot/reload. Account deletion/UID reuse invalidation belongs
to the future W3 account lifecycle, rather than an implemented account-delete flow.
