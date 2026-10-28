# Shared settings and session foundation

Work owners: **S1/S2/W3** in the [master roadmap](ROADMAP.md). This page is a contract/procedure, not a separate work tracker.

> Settings and fallback console reference. Local text/login lifecycle: [Login sessions](LOGIN-SESSIONS.md). Work steps: [Sessions/desktop](components/SESSIONS-DESKTOP.md).

## On this page

- [Current operations](#current-operations)
- [Session selection and lifecycle](#session-selection-and-lifecycle)
- [Serial headless operation](#serial-headless-operation)
- [Packaging and later migration](#packaging-and-later-migration)

S0 system-settings/session seam and S1 x64 serial headless, 2026-10-11. The current
floating desktop remains the test DE. Tiling WM, user preferences and a graphical
Settings app are later slices; `session list` reports availability on this boot.

0.0.11 adds [graphical login, local text Headless and logout/session selection](LOGIN-SESSIONS.md).
Configured installed accounts start at login; Ctrl+Alt+T opens Terminal and
Ctrl+Alt+L requests logout. `session switch de|headless` ends the old session and
selects the next login after busy/unsaved-work checks. With a display, Headless
uses a local text console; without one, COM1 remains recovery. The no-GPU COM1 section below describes the fallback transport; current local
lifecycle details are in that guide.

## Current operations

```sh
settings show
settings set keyboard de
settings set timezone Europe/Berlin
settings set session de
session status
session list
session set-default headless
```

System changes require root. `localectl set-keymap` and `timedatectl set-timezone`
call the same service as `settings set`; validation and persistence have one owner.
`loadkeys` changes the active keyboard only. `settings show` distinguishes saved and
active values, and identifies live settings as volatile. Installed saves persist.
German input supports Shift+7 `/` without a Caps Lock workaround, AltGr punctuation
and the ISO `< > |` key. Num Lock affects the keypad rather than the main keyboard.
Dedicated PS/2 and virtio navigation keys remain arrows with Num Lock enabled.
GUI Terminal Up/Down recalls session history and restores the current draft when
returning past the newest command. History is not persisted across reboot.
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
message. If DE also lacks a supported display, it falls back to x64 COM1 headless.
Saving an unavailable mode through the CLI is refused. The test DE and x64 headless
are available; WM remains planned. Since 0.0.11, `session switch de|headless`
returns to login after safe teardown checks. The model separates session choice
from live/installed storage.

`Kernel.OnBoot` skips Cosmos's graphical KernelConsole setup; upstream HAL/managers
are initialized earlier. Display capability checks inspect the Limine framebuffer
and PCI IDs for Cosmos's supported virtio GPU/VMware SVGA paths, without constructing
a canvas. The x64 PCI probe scans configuration functions using mechanism 1, before
shell workers start. Hardware presence is not a guarantee a driver will start:
fatal early/startup errors use serial diagnostics; graphical panic drawing failures
fall back to serial. Normal DE panics retain their graphical R-to-restart behavior.

## Serial headless operation

Boot with `session=headless`, or save `session set-default headless` as root. The
initial serial console targets x64 COM1 at 115200 baud, 8N1, using the UART already
initialized by Cosmos. Receive is polled by the timer-woken main loop, at most 64
bytes per tick. ARM64 receive remains unavailable; its transport must be implemented
and tested before the mode is advertised there. With a supported display, 0.0.11
uses a local text console instead; this section describes the no-GPU serial fallback.
SSH remains unavailable.

To try a live console without a GPU (add your normal disposable disk options to
exercise installation):

```sh
qemu-system-x86_64 -M q35 -m 512M -cpu max -accel kvm -accel tcg \
  -display none -vga none -serial stdio -monitor none \
  -cdrom output-x64/Zenith.iso -boot order=d
```

Input is ASCII. CR/LF/CRLF submit; Backspace/DEL erase; Ctrl+U clears the line;
Ctrl+C clears pending input and cooperatively cancels the worker. Ctrl+D ends a
pending `read`, or resets an idle shell. Multiline shell constructs use continuation
prompts. ANSI input sequences are ignored; cursor navigation/history recall, raw
TTY programs and full job control are not implemented. EOF/`exit` creates a fresh
shell and discards queued commands. Installed accounts are logged out; this cannot
restore the initial bootstrap root privilege after password setup.

The line limit is 4096 characters; accumulated scripts are at most 16384; eight
submitted lines can wait for the single shell worker; history retains 64 entries.
Oversized lines/scripts are discarded rather than executing a valid prefix. Pending
output is at most 64 KiB, with worker backpressure and main-loop draining of 1024
characters per tick. This bounds the console adapter, not every existing command's
internal buffers or arbitrary shell variables. Cancellation remains cooperative.

The same `passwd`, `login`, `su`, `logout`, filesystem/settings and script commands
work on serial. Password prompts suppress echo and history; queued commands and
partial input are discarded before secret reads. **Wait for each password prompt**:
pasting a command and password together can expose it before a secret prompt exists.
Configured installed accounts start as nobody and require authentication. Live mode
and initially unconfigured ext2 remain root maintenance/bootstrap as described in
[ACCOUNTS](ACCOUNTS.md). Malformed account records fail closed. `exit` resets the
shell. Successful `login` and `logout` also create a fresh shell, clearing prior
variables/functions/history and queued commands; `su` retains the current shell.

The serial shell uses shared metadata/access checks and starts without automatically
running `/etc/profile`; explicitly source permitted scripts if needed. Desktop-only
`open` reports that no desktop is running. No graphical command is registered by a
headless startup. Settings/services remain kernel-linked and do not create protected
userland or independent daemons.

`[console]` prefixes identify host prompts/diagnostics; `[zenith]` identifies kernel
logs. Cosmos boot/scheduler diagnostics share the UART and may interleave with
interactive output. Console echo/output is not mirrored into `/var/log/boot.log` or
the kernel ring, while explicit `logger` output is. Serial is an administrator-controlled
physical/hypervisor console; it provides no encryption. A fatal panic prints a trace
and waits for serial R to restart without allocating fonts/canvas/windows.

S1 is verified on QEMU live and disk-only UEFI installed systems with no VGA/GPU
attached, including invalid/missing defaults, boot override, authentication, permission
denials, persistence, cancellation, clean shutdown and panic/restart. See PROGRESS
for exact results. Real UARTs, ARM64 input and service supervision remain future work. Local text
console acceptance is covered by the later login/session gate.

## Packaging and later migration

Local `/etc` settings are mutable administrator state, not replaceable package
payload. P0 data bundles cannot contain `/etc`, home directories, account databases
or boot files. Future shipped defaults belong under `/usr/share/zenith/defaults`,
with local overrides kept separately. This separation is a policy now; package
default loading/upgrade reconciliation is not implemented yet.

The current DE is a replaceable test implementation behind the host seam, but remains
kernel-linked. Component inventory does not make it independently updatable. Proper
DE/WM and user preferences follow the planned toolkit and protected-userland work.
Use explicit API/runtime compatibility when migrating these services to IPC; the
existing native text ABI does not yet provide graphical/service IPC.

Validation commands and exact native evidence are in [DEVELOPMENT](DEVELOPMENT.md)
and [Master roadmap](ROADMAP.md#current-focus-and-execution-order). See [package prototype](archive/2026-10-27/package-prototype.md) for P0.

Functional native Appearance settings: [service, resources and wire contract](NATIVE-SETTINGS.md),
with the [installed-VM checklist](testing/SETTINGS-VM-TEST.md).

Material preferences use the same system-wide appearance authority and atomic
Save as mode/accent; [schema and 608/609 services](NATIVE-SETTINGS.md#material-preference-extension)
distinguish saved, effective and available choices. Headless/recovery stays solid
without clearing saved effects. The taskbar and decorations follow this policy;
panel customization remains W1/S4 in the master roadmap.
