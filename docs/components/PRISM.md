# Prism

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md) · [Concept](../design/prism-concept.svg)

Work IDs below are detailed steps of the master tracker (U9), not a separate schedule.

Prism is the launcher, requested 2026-11-12 to replace the fixed start menu: one search, shown
as a start menu above the taskbar button or as a palette in the upper third of the screen, in the
spirit of KRunner, Raycast and Vicinae. It is part of the compositor shell (no package).

## What it does

- **Opens** from the taskbar button (start menu or palette, chosen in Meridian › Search) and, as a
  palette, from Meta or Alt+Space anywhere. Esc or a click outside closes it.
- **Finds**, ranked as you type (whole-word prefix, then word start, then substring, then letters
  in order): installed apps, open windows, Meridian's 44 pages (by title and category), Log out,
  Restart and Shut down (the last two for root), a calculator (`12*(4+3)`, `2^10`; Enter copies
  the result) and "Run in a terminal". A leading `>` runs the rest as a command.
- **Keyboard**: type, Up/Down/PageUp/PageDown choose, Enter opens, Tab and Shift+Tab narrow to
  apps, windows, settings or actions, Backspace (Ctrl clears).
- The start menu lists every installed app before anything is typed, and ends with the user and
  the session/power buttons.

## Installed apps only

`AppRegistry.Visible()` lists built-in windows (Files, Editor, Terminal/Polaris, Welcome, System),
package apps while their program is in `/usr/share/zenith/native` (Meridian, Observatory,
Equinox), Sunrise only in the live session, and apps packages declare in
`/usr/share/zenith/apps/NAME.app` (`NAME=`, `DESCRIPTION=`, `ICON=`, `PROGRAM=`, optional
`ACTION=`; at most 32 files of 1 KiB). Removing a package removes its entry.

## Source

[Search](../../src/Gui/Shell/Prism/PrismSearch.cs) (pure, host-tested in `PrismTests`),
[view](../../src/Gui/Shell/Prism/PrismView.cs), [page table](../../src/Gui/Shell/Prism/MeridianPages.g.cs)
(generated from [MERIDIAN-PAGES](../MERIDIAN-PAGES.md); a test keeps it current) and
[apps](../../src/Gui/Shell/AppRegistry.cs). Meta comes from keyboard layouts generated from the
Cosmos tables with the Meta keys added ([generator](../../tools/keyboard-layouts/Program.cs)).

## Next

- [ ] File search (needs a bounded index, Meridian › Search), unit conversion, more actions
  (Ctrl+K: open with, copy path, run as root), recent choices ranking higher.
- [ ] Pinned applications in the start menu; a configurable shortcut.
- [ ] Polaris `--run COMMAND`, so commands run in Polaris rather than the built-in terminal.
- [ ] Installed acceptance on the operator's VM.
