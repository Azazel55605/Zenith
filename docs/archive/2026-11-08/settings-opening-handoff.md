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


## Prepared replay endpoint lifecycle

The operator's prepared replay at `/var/tmp/zn-settings-di5n68e0/replay` fails
before the shared-page check: `network/file contention did not overlap native UI
rendering`. The setup fixture contains `/root/io` with its original temporary
HTTP server port. The immutable harness regenerates `served/io` with a new port,
but its prepared path skips setup downloads and launches the stale installed
script. Its new handler therefore does not observe the contention request. The
previous fresh-install qualification did not exercise this prepared lifecycle.

For prepared Settings fixtures with contention coverage only, the controller
captures the current loopback fixture server. After observing a public Terminal
prompt it uses normal guest `fetch --replace` to refresh `/root/io` on the
already-copied disposable disk, requires `S_REPLAY_IO_REFRESH=0`, observes the new
prompt and starts the original `/root/u` once. The endpoint is restricted to the
fixture's `10.0.2.2` gateway and valid port. No password, account, trust, package,
result marker or original disk is modified. A failed refresh aborts without
repeating it. Fresh installs and other fixture scripts keep their original path;
the server capture is scoped and restored after the harness exits.

Regressions cover refresh-before-launch ordering, the second public prompt,
failed-refresh refusal, invalid endpoints, other-script isolation and restoration
of the HTTP server constructor. All 558 controller tests pass, including the
documentation checks. The actual prepared replay passes under TCG in
115.708 seconds, including the original overlap assertion, all four shared-page
Apply/CLI checks, upgrade, permissions, reboot persistence and ext2 audit. Evidence:
`/var/tmp/zenith-settings-048-prepared-replace/replay`; log:
`/tmp/settings-048-prepared-replace.log`. The original setup disk SHA256 remains
`67eed75c1570ff1963e9c62f7a928a18f48d5e1fb3442001db921a089037eb3f`
after the run.

On 2026-11-08 the operator confirms the prepared replay passes on the distributor
after repair `c66c54d`. This establishes the server-side Settings replay result,
including the previously blocked shared-page and contention checks. Full release
retry/publication and installed-VM acceptance are separate; this report does not
establish those outcomes.
