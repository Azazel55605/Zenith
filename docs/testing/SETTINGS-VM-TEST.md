# Settings Appearance: installed-VM handoff

> Reusable test procedure. Results and remaining acceptance are tracked only in
> [the master roadmap](../ROADMAP.md#state-and-evidence); version-specific examples
> are historical handoffs, not new release instructions.

On the distributor build VM, install Python Pillow (`sudo apt-get install python3-pil`)
for the new atlas build and native acceptance gate.

Install and boot-confirm the compatible kernel **before** installing zenith-settings;
its settings/window-state capability requirements are new. This checklist is for
a real installed VM, separate from local disposable validation.

1. Install/update `zenith-settings` and `zenith-theme-default`. New semantic theme
   resources take effect on the next graphical login. `native run settings --version`
   identifies the independent client.
2. Open `native run settings`. Use Tab/Shift+Tab and Enter/Space, then pointer input.
   Preview both modes and the six accents. Other windows should change only after Save.
3. Try Close/Escape/title-bar close with edits. Keep editing must retain them;
   Discard must close without saving them. Reload over edits needs explicit discard.
4. Save light/teal. `settings show` and `native run settings --show` must agree on
   saved and active mode/accent. Existing host chrome changes without logging out.
5. As root, run `settings set appearance dark blue`; a clean Settings window follows.
   If that window has unsaved edits, it retains them and rejects a stale Save until
   an explicit reload. Verify the error is understandable and no edits disappear.
6. As an ordinary user, reading works but saving is denied. `USER=root` must not
   grant permission. The matching user's graphical page is read-only.
7. Reboot normally. Saved appearance must remain, and saved/active must agree.
8. Update only the Settings package; verify its version changes while the confirmed
   kernel stays the same.
9. In Colors, enable Transparent surfaces and Backdrop blur, then Save. The panel,
   window decorations and Meridian navigation should reveal a tinted/diffused
   backdrop; document/text/credential surfaces stay opaque. The decoration divider
   disappears; the inner content top-left curve and all hit targets stay fixed.
10. Turn transparency off and Save. Blur stays selected but inactive; the solid
    panel and divider return. Turn transparency back on: blur reactivates. Reboot
    and verify the saved choices persist. `native run settings --materials-show`
    distinguishes saved/effective/available flags; headless reports no active renderer.
11. Move and overlap windows over text and bright/dark backgrounds, minimize and
    restore, and repeat in light mode. Text must remain readable. Open enough
    surfaces to exercise tint/solid fallback; no saved setting may be overwritten.
    Protected password entry must remain solid. Report prolonged frame lag or
    repaint artifacts along with the display size.

12. In supported workspace apps, enable **Whole app background** in Colors and
    Save. Blank content areas should reveal the backdrop while controls/text remain
    opaque. Toggle transparency off/on; the whole-background choice must survive.
    Check both light/dark and reboot persistence. Older clients stay compatible.
13. Rapidly scroll the category sidebar and detail results, alternate directions,
    then type/search/navigate. Drag repeatedly and test release plus keyboard input.
    Record any freeze with input profile, effects, screen size and serial diagnostics;
    the operator-reported 0.0.26 freeze is not proven fixed by local tests.
14. Maximize/restore Meridian, Welcome and System Information. Resize with an unsaved
    Colors draft: edits must survive without saving. Persistent categories and inset
    detail panes should remain usable; close during a drag must release input.

Report the exact kernel/client/theme versions and which steps passed. Source and
local QEMU tests do not complete this operator checklist automatically.
