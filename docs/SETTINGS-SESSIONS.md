# Shared settings and session foundation

S0 system-settings/session seam, 2026-10-11. The current floating desktop remains
the test DE. Headless, tiling WM, user preferences and a graphical Settings app are
later slices; `session list` reports their actual availability.

## Current operations

```sh
settings show
settings set keyboard de
settings set timezone Europe/Berlin
settings set session de
session status
session list
session set-default de
```

System changes require root. `localectl set-keymap` and `timedatectl set-timezone`
call the same service as `settings set`; validation and persistence have one owner.
`loadkeys` changes the active keyboard only. `settings show` distinguishes saved and
active values, and identifies live settings as volatile. Installed saves persist.
Saving a session default affects the next boot; it does not replace running windows.

| Setting | Administrator-owned file | Representation |
|---|---|---|
| Keyboard | `/etc/vconsole.conf` | Existing `KEYMAP=name`; aliases become canonical names |
| Timezone | `/etc/timezone` | Existing zone name/fixed offset followed by newline |
| Session | `/etc/zenith/session.conf` | `VERSION=1` and `SESSION=de` |

Keyboard saves preserve comments and unrelated keys. Session saves preserve extra
keys/comments and migrate an unversioned legacy file to version 1. Unknown future
session versions fall back at boot with a diagnostic, and cannot be silently
overwritten/downgraded through the settings service. Invalid/duplicate values fall
back per file: US keyboard, UTC, DE. One bad file does not discard valid other files.
Missing files use defaults. Configuration reads require regular files, valid UTF-8
and at most 16 KiB; devices and symlinks are rejected rather than opened indefinitely.

Saves validate first, write/flush a private staging file, replace the destination,
set ext2 mode 0644 and sync. Runtime keyboard/clock changes occur after persistence
succeeds. A failed staging write leaves the runtime snapshot unchanged; hardware
apply failures after replacement leave a saved value pending application/reboot.
This is not a crash-atomic transaction: ext2 has no journal, and rename/sync/device
failures or power loss still need the documented recovery route.

## Session selection and lifecycle

`SystemRuntime.Initialize` prepares storage, accounts, configuration and shared
commands. `Kernel` then selects and starts an `ISessionHost`. Fonts, canvas and
initial windows are constructed in `DesktopSessionHost`, which adapts the current
DE. The shared idle/memory-pressure loop calls the selected host's `Tick`.

Selection precedence is a single `session=de|headless|wm` boot argument, then the
saved default. Malformed/duplicate arguments use the saved default and log a
diagnostic. A requested host absent from the build falls back to DE with a clear
message. Saving an unavailable mode through the CLI is refused. Only DE is currently
available; manually saving headless does not create a headless session. No active
session switching is implemented. The model separates session choice from live/
installed storage and is independent of graphical types.

The next S1 slice adds a serial host, graphics-independent panic/diagnostics, bounded
input/output, cancellation and authentication using the existing shell/accounts.
It must prove operation without a graphical device before headless is marked done.
The current graphical panic path and Cosmos hardware discovery remain in place.

## Packaging and later migration

Local `/etc` settings are mutable administrator state, not replaceable package
payload. P0 data bundles cannot contain `/etc`, home directories, account databases
or boot files. Future shipped defaults belong under `/usr/share/zenith/defaults`,
with local overrides kept separately. This separation is a policy now; package
default loading/upgrade reconciliation is not implemented yet.

The current DE is a replaceable test implementation behind the host seam, but remains
kernel-linked. Component inventory does not make it independently updatable. Proper
DE/WM and user preferences follow the planned toolkit and protected-userland work.
Use explicit API/runtime compatibility when migrating these services to IPC; no
binary userspace ABI has been frozen by this slice.

Validation commands and exact native evidence are in [DEVELOPMENT](DEVELOPMENT.md)
and [PROGRESS](PROGRESS.md). See [package prototype](PACKAGE-PROTOTYPE.md) for P0.
