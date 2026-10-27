# Protected graphical-client VM test

> Reusable test procedure. Results and remaining acceptance are tracked only in
> [the master roadmap](../ROADMAP.md#state-and-evidence); version-specific examples
> are historical handoffs, not new release instructions.

Use a currently qualified kernel/client pair from the master roadmap. The original
0.0.24/probe 0.1.0 handoff is historical and was operator exercised; no pending
publication claim is maintained in this procedure.

## Distributor and installation

When the distributor is idle, pull its controller to recognize the required graphical
probe recipe and current native gates:

```sh
cd /home/zenith-release/Zenith
git pull --ff-only
sudo systemctl start zenith-distributor.service
```

Wait for both releases to finish signing/publication/delivery. In Zenith, install
the kernel first, reboot and confirm before installing the new ABI package:

```sh
pkg refresh
pkg kernel update --yes
reboot
```

After boot/login:

```sh
boot confirm
pkg install --yes zenith-gui-probe
pkg verify
native run gui-probe --version
```

Expected version: gui-probe 0.1.0. Other CLI packages need no new release for this
API. This is a graphical API probe, not Settings or a redesigned desktop.

## Graphical session

Use a Terminal in the Desktop session, with the shell running as the same account
that logged into that desktop. `su` to a different UID intentionally cannot attach
a window to another account's session; USER environment variables do not override
this rule. Headless/local-text/serial sessions intentionally report unavailable.

```sh
native run gui-probe --probe
native run gui-probe
```

The first should print GUI_PROBE_OK and return successfully. The interactive
probe opens a decorated window labelled Native, containing simple colored cards.
Check that it can move and minimize through the trusted decorations, and that
clicking inside changes its accent color. Type a key while focused; Escape or q
exits. Closing the window should also return control to the terminal.

Switch focus to the terminal or another application: its input must not go to the
native window. Restore the native window through the taskbar before typing Escape.
Existing Terminal/Files/login behavior should remain usable.

For a cancellation check, start the probe, minimize it, open another Terminal with
Ctrl+Alt+T, then run `ps` and `kill PID` for that probe's PID. Only the native client
window/taskbar entry should disappear; the session and other windows remain usable.
The original terminal should return with cancellation status 130.

The automated fixture already covers hostile pointers, different UID/USER spoofing,
fault cleanup, client quota, event overflow and same-kernel executable upgrade.
No need to tamper with installed files or real credentials for this checklist.

## Report and next work

Please report exact versions, boot confirmation, session/account, visible drawing,
input/focus behavior, close/minimize/cancellation and any error output.

This first API has fixed-size copied surfaces and finite process/request budgets.
No hardware framebuffer mapping, global input access, dynamic resizing, clipboard,
font service or general IPC is exposed. Settings needs the next typed authorized
service contract and shared client widgets/resources. The desktop/compositor and
login remain trusted kernel code. See [ABI details](../GRAPHICAL-CLIENTS.md).
