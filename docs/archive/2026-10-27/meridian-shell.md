# Meridian shell and first native system apps

**Local pre-release implementation, 2026-10-27.** This is a completed foundation
increment within the larger migration, not completion of all system apps or full
release qualification. [Current scope and remaining ports](../../components/SESSIONS-DESKTOP.md#native-app-migration).

## Changes and boundaries

- Meridian has 10 categories, seven drill-down groups and all 43 destinations.
  Search covers page names, IDs and user-facing planned-option labels. Separate
  presentation data prevents engineering instructions from appearing in the UI.
- WIP pages show their scope without fabricated device state or save actions.
  Colors retains actual preview/save, revision conflicts and permission checks;
  leaving a dirty page requires explicit discard. History is bounded and in-memory.
- Shared word wrapping and single-line UTF-8 editing preserve original bytes and
  parent clipping. Text shaping, scalable fonts and multiline editing remain open.
- Welcome and System Information have independent native frontends in the new
  `zenith-desktop-tools` recipe. Both shell `open` and actual start-menu entries
  use the asynchronous signed launcher. Missing packages retain recovery windows;
  present invalid binaries fail closed. The live/unconfigured startup recovery
  windows remain trusted.
- Owned interactive quota renewal and read-only public system metadata support
  these clients. Legacy budgets, UID/session admission and raw CPU/fault/cancel
  checks remain. Terminal, Editor and Sunrise have not been ported. Files is
  intentionally unchanged.

## Validation

Local environment: QEMU **11.1.2**, explicit **TCG**, .NET SDK **10.0.113**,
Pillow **13.0.0**. No distributor or installed-VM conclusion follows from this.

- `dotnet test tests/Zenith.Tests --no-restore`: **747 passed**.
- `deploy/distributor/validate.py --controller-only`: **238 passed**.
- Actual C renderer/state tests pass ASan/UBSan, all 43 pages, nested groups,
  pointer capture through redraw, search, discard/save isolation, UTF-8 editing
  and clipping/bounds regressions. GCC and Clang freestanding builds pass.
- Signing-tool checks: four passed, one native RSA shared-library test skipped
  because that library was not supplied. Builder tests: 11 passed; meta: 3 passed.
- Debug ISO/ELF build succeeds. The final targeted native gate passes on fresh
  disposable disks: signatures/dependencies, malformed requests, unprivileged
  write refusal, preview/dirty-chrome confirmation, GUI save/live apply, independent
  client upgrade, actual metadata, real Ctrl+F search into a visible WIP page,
  unchanged preferences, Welcome/System through shell and actual menu clicks,
  normal cleanup, independent reboot persistence and ext2 audit.

Final native evidence: `/var/tmp/zn-meridian-native-9`; host/controller logs under
`obj/meridian-host-final.log` and `obj/meridian-controller-complete.log`. Files under
those paths are local ignored artifacts, not source or release assets.

| Artifact | SHA256 |
|---|---|
| Debug kernel ELF | `d000342bcf9982fd585c985cab32b608af0011fef216e119a213003879c52445` |
| Debug live ISO | `64c78ac94487258842f7d9b6b0de1d3d269639982c8e1b6c6c7b0ee712197bcd` |
| Settings fixture 0.1.0 ELF | `a6739e2ad04da354f5c0af5cdf7d67c087cecf6a81e98775ec7812c146483d13` |
| Desktop-tools fixture 0.1.0 ELF | `b1c89eedd90ac0609fdbedecd007424be65515ea3a3c192d05d499853f3fa5ae` |

Fixture version numbers are test inputs, not published releases.

## Corrections found by validation

Cosmos layouts produce zero text for Ctrl+letters. The native event adapter now
supplies the logical letter while preserving ordinary Unicode/AltGr; host and real
keyboard tests cover the change. Review also found the start menu bypassed the
new signed launch path; it now uses the same dispatcher as shell `open`.

A readiness log was insufficient to prove visible page pixels. Frame observation
now distinguishes the WIP banner from same-color trusted chrome. The synthetic
mouse helper's one-pixel restore can fail when a menu press starts a new app; the
menu test instead holds exactly one press until actual window admission, releases
once, then observes the native frame. No click or application action is repeated.
A misplaced unittest-discovery argument was corrected by invoking the existing
signing/builder scripts with their supported arguments.

## Remaining acceptance

Finish the other three app ports and detailed settings forms, then run the required
complete clean-source Release qualification before any new kernel tag. No new tags
or pushes were made for this increment; the in-flight 0.0.25 releases are unchanged.
