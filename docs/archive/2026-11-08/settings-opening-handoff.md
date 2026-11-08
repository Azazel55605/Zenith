# Shared Settings opening handoff (Q3/U1)

The distributor's 0.0.48 Settings gate fails on the Keyboard page at shared system
Apply. Retained evidence is `/var/tmp/zn-suqaywrs/a`: `shared-k.ppm` contains only
Terminal after `sh /root/k`; `shared-save-failure-k.ppm` contains Meridian with
`gb` selected, an unapplied-change message and the combo focused. There is no
save error or successful revision marker. The public log shows focused admission
at 72.346s, an opening transition interrupted by input at 73.757s, and an unsaved
change at 75.135s. The exact missing/misordered navigation event is not yet known.

The immutable harness waits for `MERIDIAN_FOCUSED`, sleeps half a second, then
sends Shift+Tab, Enter, four Down keys, Enter, Tab, Enter on fixed short delays.
Admission is not presentation. A controller candidate waits for the admitted
window's opening transition and a stable rendered frame before the first
Shift+Tab. It sends each original key once; no save is repeated, no result is
fabricated and the existing revision and CLI-parity assertions remain intact.
Legacy fixtures and disabled animation retain their original transport. A
changed page/window, unavailable VM or readiness timeout fails before navigation
and preserves phase-only diagnostics. This fences the evidenced opening race;
it is not proof that every potential keyboard-loss path is fixed.

The host widget regression now exercises Shift+Tab, combo selection, Tab to Apply
and Enter rather than clicking Apply, with real control rebuilding between events.
All five UI host tests pass. The 552-case controller suite passes after rerunning
its sole sandbox-blocked temporary-directory test with the necessary filesystem
permission; the final expanded targeted set passes 59 cases, including the added
nonshared-page compatibility test. Documentation links and tracker boundaries pass.

A matching Release build of the unchanged kernel sources passes the full Settings
gate under TCG in 232.967 seconds: all four shared-page Apply/CLI checks, live
materials, secret cancellation, permissions, upgrade, wheel/maximize, reboot
persistence and independent ext2 audit. Evidence is
`/var/tmp/zenith-settings-048-opening-matched/replay`; log is
`/tmp/settings-048-matched-gate.log`. Local ELF SHA256 is
`ccc340280f38b733092d47baac57717ab8a7ffb45624e42c78789172033af26d`.
An earlier attempt used stale local binaries lacking the framebuffer ABI and
failed before GUI admission; that is not qualification or a distributor defect.

The distributor's exact binary and timing still require the focused Settings
replay. Original server evidence and release tags remain unchanged. The controller
repair does not claim to resolve every open keyboard-loss path.
