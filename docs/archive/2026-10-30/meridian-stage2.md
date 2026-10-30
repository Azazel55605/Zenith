# Meridian stage 2 pages — 2026-10-30

Owners: S1/D1/U1 in the [master roadmap](../../ROADMAP.md);
[design](../../MERIDIAN-BEHAVIOR.md), [plan and departures](../../components/MERIDIAN-STAGE2.md).
Distributor build and installed acceptance are pending.

## Change

Meridian is rebuilt on the stage-1 widgets as a navigation model, page bindings and a
shell; `app.c` is now the drawing-free colors model. All 43 destinations are form
pages; unsupported options stay visibly unavailable. Services, permissions, revision
checks, CLI modes and log markers are unchanged.

## Findings while implementing

- The host admits at most 640x480 at open and has a 600 px minimum, so the planned
  800x560 default would be refused on kernel 0.0.29, and single-pane mode could never
  appear. Meridian opens at 640x480 with two panes.
- Stage 1 gave hover event 9, already `Z_GUI_CLOSE_INTENT`; it is now 11. No shipped
  host sends either to a client that enables hover.
- Below-the-fold controls are not registered, so Tab reaches visible controls only;
  Up/Down/Home/End scroll the page. Page Up/Down await the stage-3 key codes.

## Local validation

Sanitizer host suites (navigation, bindings, colors model, shell, widgets), 352
controller tests and the real-guest Settings gate (96 checks) on kernel 0.0.29. The gate
covers pointer and keyboard previews, dialog Cancel, material on/off, whole-app
background, keyboard combo apply with CLI parity, search, wheel stress, maximize/restore,
reboot persistence, independent upgrade, permissions and the ext2 audit. Evidence is
retained under ignored `obj/s2-gate`.

## Clean-source release qualification

Unchanged source `2a80ce2` passed fresh unsigned acceptance
(`deploy/distributor/validate.py --keep obj/s2-qualified`, four native jobs): 352
controller tests, host tests, kernel build, runtime adapter, package tool and all ten
native gates (login 228.7s, Settings 283.9s, network 282.9s, CLI 256.6s, repository
560.7s, materials 113.6s, system app styling 39.4s, graphics 189.6s, native processes
736.4s, kernel update 764.4s; overlapping, not summed). Tag `zenith-settings/v0.4.0` was
pushed at `2a80ce2`.

## Selector correction and stage 3 — local

The operator found a stray focus box on Quick Settings in installed 0.4.0. Clicking the
back row left focus on row id 0, which the rebuilt list gave to another row; rings were
also shown for pointer focus and clipped by the list edge. Rings are now keyboard-only,
rows reserve space for them, replaced rows drop focus, and Enter/arrows act on the
focused sidebar row rather than the highlighted one (found during that trace).

Stage 3 adds call 627 and event 11, one coalesced pending move outside the event queue,
window-manager leave tracking, Page Up/Down key extras and toolkit hover. 783 host tests,
sanitizer suites and the Settings gate (98 checks, on a locally built kernel) pass,
including real-input hover and Page Down. Settings falls back without hover on -38; that
path is covered by its bounds check and by code review, not by a run on kernel 0.0.29.
