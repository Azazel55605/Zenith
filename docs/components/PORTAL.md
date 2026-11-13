# Privilege elevation: sudo and the Desktop Portal

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md) · [Accounts](../ACCOUNTS.md)

Work IDs below are detailed steps of the master tracker (W4). Requested 2026-11-13: a sudo
equivalent called `sudo`, and a Desktop Portal service for privilege requests from graphical
programs, built so that more kinds of request can be added later.

## Who may administer the system

Administrators are root and the members of the **`wheel`** group (gid 10).
- A new installation puts the installing person's account in `wheel`.
- Kernel 0.0.57 creates `wheel` once on older installations, with the installer's account
  (uid 1000) in it, and logs `accounts: created the wheel administrators group`. A `wheel` group
  that already exists is never changed. A member removed later is not added back.
- Root changes membership with `gpasswd -a NAME wheel` and `gpasswd -d NAME wheel`. `groups [NAME]`
  lists an account's groups.
- Membership is read from `/etc/group` each time, not from credentials taken at sign-in, so a change
  applies at once without signing in again.

## sudo

`sudo [-n] [-u USER] COMMAND [ARGS...]`, `sudo -v`, `sudo -k`, `sudo -l`.
- The invoking account types **its own** password at `[sudo] password for NAME:`. Root never needs
  one. Three wrong passwords end the attempt (one second's pause after each).
- After a correct password, the same account runs further `sudo` commands in **this terminal**
  without a password for five minutes. `-k` forgets it. `su`, `login` and other terminals do not
  share it. `-v` asks now and extends the five minutes. `-n` fails instead of asking.
- The command runs in a child shell as the target account (root, or `-u USER`). It keeps the
  working directory, sets `USER`, `LOGNAME`, `HOME` and `SUDO_USER`, `SUDO_UID`, `SUDO_GID`,
  `SUDO_COMMAND`. Built-in commands, native programs and scripts are found as usual. Shell
  functions are not run. A native program started this way runs as the target account.
- The terminal's own identity never changes. `id` after `sudo id` still shows the account.
- Every use and every refusal is logged:
  `sudo: user : USER=root ; PWD=/home/user ; COMMAND=...`, `sudo: bob : not in wheel ; ...`,
  `sudo: user : 3 incorrect password attempts ; ...`.
- Not implemented: `-s`/`-i` shells, a sudoers file with per-command rules, `-E`, and running as a
  group. Without accounts (live FAT roots) `sudo` reports that accounts need an ext2 installation.

## Desktop Portal

Programs ask the Desktop Portal for things only the person may decide. The graphical session is the
portal's **frontend**. It shows a dialog that the desktop draws itself:
- above every window, the taskbar and Prism, on a dimmed screen;
- it takes every key and click until it is answered;
- it names the program and its process.

A program cannot draw over that dialog or type into it. The password goes from the dialog straight
to the account service and is never given to the program.

### How it is built

| Part | Where | Role |
|---|---|---|
| Broker | `src/Core/Portal/PortalBroker.cs` | Interfaces by name; requests with handles, owned by one process; states; limits (4 open per process, 32 in all); cleanup when a process ends or the session ends. |
| Interface | `IPortalInterface` | `Name` (reverse DNS), `Version`, `Open` (check a request: answer at once, or leave it for the frontend, or refuse), `Abandoned`. |
| Frontend | `src/Gui/Shell/Portal/PortalHost.cs` | Attaches when the desktop starts and detaches when it stops (open requests are cancelled). Shows one request at a time in the trusted frame. |
| Dialog | `IPortalDialog`, registered in `PortalDialogs` | The body for one interface: heading, height, drawing, keys, clicks, background work, done. |
| ABI | `ProcessPortal.cs`, `userland/include/zenith_portal.h` | zenith-native-portal-abi 1.0, calls 650 to 653. |

Request states are Pending (waiting for the desktop), Shown, then one of Approved, Denied, Cancelled
(withdrawn, program ended, session ended), Failed, and Unavailable. Unavailable means no graphical
session can answer, or the desktop has no dialog for the interface. In a terminal, use `sudo`.

**Adding an interface** (file chooser, screenshot, open with...):
1. Implement `IPortalInterface` and register it in `Portals.Initialize`.
2. Implement `IPortalDialog` and register it in `PortalDialogs`.
3. Document its methods, payload and response here, and give it a constant in `zenith_portal.h`.

The calls, the broker and the frame do not change. Programs find what is offered with call 653.

### Native ABI (zenith-native-portal-abi 1.0, kernel 0.0.57)

| Call | Arguments | Result |
|---|---|---|
| 650 open | request, 256 | Handle > 0. Errors: -22 malformed, -38 no such interface or method, -2 no such action, -24 four already open, -16 the broker is full. |
| 651 poll | handle, response, 256 | The state. -9 for a handle that is not the caller's. |
| 652 close | handle | Withdraws an open request (its dialog closes) and forgets it. |
| 653 info | buffer, 1024 | Interfaces and versions. Flag 1: a graphical session is attached. Per interface, flag 1: the desktop answers it. |

The request has version 1, flags 0, the interface name (64 bytes, NUL-padded), the method (32),
`payload_bytes` (up to 144) and the payload. Bytes past the payload must be zero. The response has
version, state, handle, `response_bytes`, a message (96 bytes: why it ended as it did) and up to
136 response bytes. Calls 651 and 653 do not wait for the filesystem service gate. Older kernels
answer -38 to all four calls, so programs check for that and carry on without the portal.

### org.zenith.portal.Authorization 1

The first interface asks for administrative actions, as polkit does. Its payload is the action id,
NUL-padded to 64 bytes. Its response is u32 granted (1/0) and u32 privileges now held. Methods:
- **Authorize** asks:
  - a program running as root, or one that already holds the privilege, is approved at once;
  - otherwise the dialog asks an administrator (the signed-in account, when it is in `wheel`) for
    their own password;
  - anyone else is asked for root's password;
  - with no administrator able to approve (root locked, caller not in `wheel`), the request is
    denied.
  - Three wrong passwords, Esc or Cancel deny it.
- **Check** answers at once, with no dialog.
- **Release** gives the privilege back.

| Action | Privilege | What it unlocks |
|---|---|---|
| `org.zenith.disks.manage` | Disks | Calls 647/648: partitioning, formatting, mounting, startup mounts (Equinox). |
| `org.zenith.settings.system` | System settings | Calls 620/621, 625/626, 640/641, 608/609, 616..619 and system-scope 622/623: keyboard, time zone, session, display and the system appearance (Meridian). A personal appearance scope always stays the caller's own. |

A grant belongs to **one process** until it exits or releases it. For that process alone, the
kernel serves that area's calls as root (uid 0 in the call context too). Every other call (files,
network, processes) keeps the program's own identity. A new process, even with a reused pid, starts
with nothing. Each answer is logged as `portal: equinox (pid N) org.zenith.portal.Authorization.Authorize: approved (authenticated as user)`.

**Clients.** Equinox shows **Unlock** where it used to say "Sign in as root", on the plan panel and
in the mount editor. `equinox --unlock` asks at once; gates use it. Meridian shows **Unlock** under
read-only system settings (keyboard, time zone, session and similar pages, display, and the
system colours) when call 653 answers. Both say why when the answer is no, and point to `sudo`
when no desktop can answer.

## Stages

- **EL1 (local, kernel 0.0.57):**
  - the `wheel` group;
  - `sudo`, `groups` and `gpasswd`;
  - the broker, the trusted frontend, the Authorization interface with two actions, and the ABI;
  - Equinox 0.4.0 and Meridian Unlock.
  - Checked by host tests (sudo parsing and ticket, membership, broker, grants and ABI, the Meridian
    unlock flow) and `tools/portal-smoke.py`: sudo on the serial console, the dialog with a wrong
    and then the right password, Esc denial, and sudo in the desktop terminal.
- **EL2:**
  - a terminal agent for the portal, so that Authorization works from a headless session (pkexec);
  - a time limit for grants that an app may ask for.
- **EL3:** more interfaces on the same broker, each with its own sign-off:
  - file chooser (open/save outside the program's own files);
  - screenshot;
  - open a file or link with another app;
  - removable-drive access ([S6](REMOVABLE-STORAGE.md)).
- **EL4:**
  - per-command rules (a sudoers-like policy) and per-action policy, such as "allow without a
    password for wheel";
  - only after a review of what those would let a compromised account do.
