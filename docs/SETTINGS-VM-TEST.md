# Settings Appearance: installed-VM handoff

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
   kernel stays the same. Transparency/blur remain marked unavailable.

Report the exact kernel/client/theme versions and which steps passed. Source and
local QEMU tests do not complete this operator checklist automatically.
