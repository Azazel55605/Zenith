# Native system app migration

**Active source work, 2026-10-27. Not released or operator-installed.**

The agreed batch includes Terminal, Editor, System Information, Welcome and
Sunrise. **Files is excluded**: retain its current interface until the separate
redesign. Its existing open-in-Editor route must continue to work.

## Current implementation

| Application | Native frontend | Remaining work |
|---|---|---|
| Meridian | All 10 categories/43 destinations and seven drill-down groups, settings search, six in-memory recent pages, Quick Settings links, reusable Colors page, read-only About snapshot | Detailed disabled forms, persistent history, per-user scope and the remaining capability-backed settings |
| Welcome | `desktop-tools welcome`, shared native widgets/text and signed launcher dispatch | Qualification and installed acceptance |
| System Information | `desktop-tools system`, bounded actual runtime/display/clock snapshot and refresh | Hardware inventory and explicit running release-version metadata; qualify and install |
| Editor | Existing trusted window retained | Native multiline editing/selection and clipboard, permission-aware staged saves, dirty close, signed launcher integration and native roundtrip checks |
| Terminal | Existing trusted shell/recovery window retained | Owned PTY/shell session contract, native renderer/input, hidden authentication/type-ahead/raw modes, jobs/cancel/EOF, session teardown and recovery acceptance |
| Sunrise | Existing trusted installer retained | Typed root/live-only installation/progress contract and native wizard; preserve explicit target erase confirmation, secret handling and maintenance/session blockers |
| Files | Intentionally unchanged | Separate redesign later |

Do not describe the whole application migration as complete while the three
service-dependent frontends remain trusted windows. Shared styling alone is not a
native port. Terminal must use an owned shell/PTY, not a privileged command-string
RPC. Sunrise must retain the existing installer checks; neither app is replaced by
a decorative nonfunctional window.

## Launcher and recovery

The desktop launches signed `desktop-tools` and `settings` through a worker with
captured credentials, invocation-local cancellation and a bound on concurrent
starts. The compositor thread never performs executable verification/entry.
Session stop cancels owned invocations and revokes surfaces. Programs have no
terminal input lease by default.

Welcome/System use the existing trusted recovery windows only when the native
executable is absent. A present invalid executable fails verification and shows a
launch error; it never silently falls back. Meridian has an explicit missing-package
notice. Native programs still require signed package ownership, matching digest and
executable permissions. Native windows remain subject to session/UID admission.

`zenith-desktop-tools` is a new core recipe with one executable and two entry
modes. It requires the graphics, interactive and system-information capabilities.
Current `zenith-settings` builds also require the new interactive/system-information
capabilities; install the matching future kernel first. Existing 0.0.25 release
tags remain unchanged. No new release is authorized by this implementation record.

## Interactive and metadata contracts

Call **605** `(owned_surface, 1, 0)` opts into interactive ABI 1. It requires a
live invocation-owned surface; a repeated enable is `-16`, a foreign/missing handle
is `-9`, malformed version/reserved fields are `-22`. While the surface remains
live, the original general/graphics request, presentation, file-transfer and raw
CPU tick quotas renew once per monotonic minute. Network lifetime limits remain
unchanged. Legacy clients retain lifetime quotas. Closing/revoking the surface
prevents renewal and permits only a two-second cleanup interval at service
boundaries. Full pointer checks, per-call byte bounds, descriptor/surface quotas,
fault/cancellation checks and the raw CPU watchdog still apply. This is not an
unrestricted background-process facility. Clock adjustment cannot renew quotas.

Call **612** `(writable_buffer, capacity, 1)` returns UTF-8 public system metadata
and its byte count, with capacity 1..4096. It validates the complete writable range
before collecting data, returns `-34` if too small, and writes no partial result.
No NUL byte is appended. It reports boot mode, kernel-generation ID when known,
hostname, active display geometry/refresh, uptime and configured local time/zone.
Unknown running release versions are explicitly unknown; installed package versions
are not substituted. This API contains no account secrets or device pointers.

Ctrl+letter graphical events carry a lowercase logical letter when Cosmos supplies
no text; AltGr and ordinary Unicode text remain unchanged. This makes native
application shortcuts usable with the existing layout maps.

## Verification

Use `tools/test_ui_library.py` for the real C renderer/navigation and sanitizer
checks, host tests for quota/revocation/key encoding, and
`tools/native-settings-smoke.py` for signed Settings/desktop-tool delivery,
Appearance save/reboot/upgrade, actual keyboard search/WIP navigation, launcher
Welcome/System, cleanup and independent ext2 integrity. [Targeted local evidence](archive/2026-10-27/meridian-shell.md) records the passed
checks and exact artifacts. Full clean-source release qualification and operator
installation remain separate from this targeted gate.
