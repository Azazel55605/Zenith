# Sunset

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md) · [Applications](../APPLICATIONS.md)

Work IDs below are detailed steps of the master tracker (U10), not a separate schedule.

Sunset is the small popup for **Lock**, **Log out**, **Restart** and **Shut down**, and the lock screen
behind Lock. [Concept](../design/sunset-concept.svg). It is part of the session suite, drawn by the
desktop itself (like Prism and portal dialogs), not a separate window or package.

Requested 2026-11-15: implement Sunset and its functionality.

## Opening it

- **Meta+Shift+L** from anywhere. Cosmos reports no key releases, so a Meta press followed within
  a second by Shift+L counts as the chord. (Meta alone still opens the Prism palette; the chord closes
  it again.)
- **Ctrl+Alt+Delete**, which needs no Meta key.
- The launcher menu's bottom bar: **Lock** locks at once, **Leave...** opens Sunset.
- Prism actions Lock, Log out, Restart and Shut down. Each opens Sunset with that action focused, so
  the person sees what would be lost first. Before Sunset, Prism restarted and shut down at once and
  offered both only to root.

## The popup

- The heading asks "End this session?" and names the person, the host and the uptime
  (`user on zenith · up 3 h 12 min`).
- Four tiles, one key each: L, O, R, S. Focus starts on Lock, the only action that loses nothing,
  unless the popup was opened for a particular action. Arrows and Tab move, skipping what cannot be
  used. Enter, Space, the letter or a click acts at once: opening Sunset was already the deliberate
  step. Esc, the × or a click outside cancels.
- With nothing in the way it says "Everything is saved. Nothing is running." and "Safe to leave".
- Otherwise it lists what would be lost or interrupted, up to six items, using the same checks a log
  out makes:
  - a window with unsaved changes (the built-in Editor, or a native program that reported them);
  - a terminal command or background job (the built-in Terminal, Polaris, by the program that owns
    the terminal session);
  - an installation, package change or kernel update;
  - a native program without a window.
  The first two windows get a **Show** button that brings them forward. Log out, Restart and Shut
  down stay unavailable until the list is empty; Lock stays available. The list is checked again
  twice a second, so saving the file or ending the command enables the actions without reopening.
- Restart and Shut down are open to the signed-in person, not only root: the greeter already offers
  both to anyone at the machine. They still wait for everything above, and the kernel's own power
  checks still apply.
- If the session refuses at the last moment (something started meanwhile), Sunset stays open and
  says why.

## The lock screen

**Meridian → Security → Screen Locking** sets the system's idle timeout and password grace. Automatic locking defaults to off. Choose 1, 5, 10, 15 or 30 minutes. Grace defaults to immediate authentication and can be set to 5, 15 or 30 seconds. The CLI takes minutes and seconds, for example `settings set idle-lock 5 15`. Zero minutes disables automatic locking.

Keyboard or pointer input during grace wakes the covered desktop and consumes that input. Grace starts at the inactivity deadline, including when a busy frame handles the event late. After grace, the signed-in account must authenticate. Manual Lock always requires the password. Accounts without a usable password keep the desktop accessible.

- Drawn opaque over everything: the wallpaper dimmed, the time and date, the person's name and a
  password field. No window, taskbar, Prism or portal dialog is drawn, and no key or click reaches
  them. Programs keep running; their frames are not shown. A portal request waits until unlock.
- Only the signed-in account unlocks it, with its own password, checked off the drawing thread by the
  account service like the greeter.
- Each wrong password costs the second the check already waits. From the fifth wrong one in a row,
  each further try waits 30 seconds more, at most five minutes.
- An account without a password cannot lock (the live system, or installs before passwords are set):
  Lock is unavailable and says why.

## Materials

The popup, the lock card and Prism draw on the desktop's own material, like the taskbar: blurred and tinted
when materials are on, solid otherwise.

## Motion

- The card fades and scales in over 120 ms above the dimmed desktop (`motion: SunsetOpened`), scaled
  by the Animations setting; Off opens it at once.
- Log out uses the existing session fade; Restart and Shut down first draw one frame without the popup,
  then the existing shutdown screen takes over.

## Stages

- **SS1 (local, kernel 0.0.60):** the popup, blockers with Show, the lock screen, Meta+Shift+L,
  Ctrl+Alt+Delete, launcher and Prism entries, the open animation. Icons `lock`, `log-out`, `power` and
  `user` added to `zenith-icons` 0.4.0 (simple shapes without it). Checked by host tests (choices, keys,
  focus, wording, uptime, unlock throttling) and `tools/sunset-smoke.py`.
- **SS1.1 (kernel 0.0.61):** with zenith-icons installed, Sunset still drew its built-in shapes: icons are
  stored at 16, 24, 32 and 48 px and Sunset asked for 20 and 26 px. Other sizes are now scaled from the next
  larger stored one (any shell surface), and the icon cache keeps every icon and looks a missing one up again
  after a few seconds. The card and the Prism menu blur at the taskbar's scale (`MaterialPolicy.SampleScale`),
  and key letters use the proportional font, so Log out's O no longer reads as a zero. The gate installs the
  pack and requires `sunset: icons drawn=5 fallback=0`.
- **SS2:** lock before
  suspend once suspend exists; switching user from the lock screen.
- **SS3:** native programs naming what they would lose (a per-window reason instead of "has unsaved
  changes"), and "Save all" where programs support it.
