# App materials and the first shared Settings backends

Work ownership: [D1/D3/U1/S1/Q2](../../ROADMAP.md). This records implementation and
local evidence; it is not another tracker or a published release.

## Implemented slice

Terminal, Editor, the trusted Welcome/System recovery windows and non-credential
Sunrise steps now use an owned host background material when Whole app background
is enabled. They share the neutral chrome treatment and bounded compositor/cache.
Terminal hidden input and Sunrise account entry force a solid body/chrome. Files
remains excluded; the independent Welcome/System clients already use the workspace
material path. This is presentation integration, not completion of native app ports.

Colors gains a persisted 0..100 translucency control. Its minus/plus controls change
by 10 points; the trusted CLI accepts every integer with `settings set translucency`.
The host maps this range onto a palette-specific readable opacity range rather
than fading text or letting most upper values collapse onto one contrast floor.
Zero produces a solid base. Schema 4 and ABI 618/619 add the level; all earlier
appearance/material request layouts remain unchanged and their saves preserve it.
New Settings packages require the material-control capability before installation.

The first S1 service slice exposes actual keyboard-layout/time-zone catalogues and
root-only revisioned saves through 620/621. It uses the existing SettingsService,
configuration store and live keyboard/clock callbacks. CLI changes participate in
the same boot-local revision. No client receives paths, shell callbacks, arbitrary
field writes or authority from its page name or USER environment variable.

Meridian's Keyboard and Date & Time pages now have functional selectors,
save/discard, stale-edit handling and close-intent protection. They explicitly edit
system defaults; ordinary users see read-only controls. Unimplemented model,
repeat/Num Lock/shortcut and clock-edit/sync controls retain a WIP banner. These
are bounded initial selectors, not the completed richer U1 selector/search widgets.
Per-user preference inheritance, network/account backends and the other page work
remain in the master roadmap. No new automatic schedule or time-sync authority is
implied by choosing a time zone.

## Local checks

- Debug kernel/ISO build passed.
- 775 host tests passed: schema migration/legacy-save preservation, level bounds,
  monotonic readable translucency, real catalogue bounds, root/ordinary permissions,
  malformed/stale requests, write failure, CLI revision changes and reload.
- 240 controller tests passed. Their existing temporary native-release fixture
  requires writable `/var/tmp`; that run used the host permission context after
  the workspace-only sandbox correctly rejected its temporary directory.
- Real C UI state/renderer tests passed ASan/UBSan: functional page draft/save/
  discard/close behavior, stale edits, read-only controls and level round trips.
  Settings compiled under both GCC and Clang with warnings-as-errors.
- Native materials acceptance passed in 83.968 seconds.
- Native prepared-fixture Settings replay passed in 119.910 seconds: real GUI
  keyboard choice `gb` and time zone `Europe/London` applied; CLI and native reads
  agreed; ordinary-user writes with USER=root were refused; defaults were restored
  to us/UTC. Terminal material pixels, real hidden-input solid fallback and public
  input recovery passed without submitting a password. Level 80, restored defaults
  and appearance survived reboot. Existing upgrade, wheel/maximize/input teardown
  checks and independent ext2 audit also passed.

The first new-page click fixture matched the WIP banner instead of the selector;
its locator now cross-checks the sidebar field geometry. A subsequent helper
assertion used a returned log offset as if it were text; it now slices the log
explicitly. Neither was a guest save failure. The package tool's broad source glob
also needed to exclude the new runtime-only service, just as it excludes the
existing appearance bridge. These failures were corrected before the passing run.

Final Debug ELF SHA256:
`985f1c9a0397d5cc6c812677905fc13db1140d39a2a143ec5b29120564e5535a`.
Final ISO SHA256:
`cd96f29b071640f46fa70d633cfc9d910f21dbbc67331e72980b97840a75850a`.
Local evidence: `obj/settings-layer-replay-final`, `obj/settings-layer-materials-final`
and adjacent host/controller/build/UI logs.

A final UI-only correction clears a previous page's success caption when switching
settings fields; the sanitizer and GCC/Clang checks were rerun afterward.

## Acceptance boundary

No new tag/publication or full clean Release qualification is claimed. The installed
VM checklist, including sustained VMware wheel/drag behavior, remains required.
The previously reported freeze has not been established as fixed by this slice.
The [contracts](../../NATIVE-SETTINGS.md) and
[operator procedure](../../testing/SETTINGS-VM-TEST.md) describe the current handoff.
