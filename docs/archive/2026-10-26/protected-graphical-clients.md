# Protected graphical-client implementation

## Scope

The operator accepted the installed CLI/network release and selected graphical
client APIs next. This slice adds a first bounded copied-surface/event protocol
and an independently compiled/signed probe. It retains the trusted desktop,
login and window manager. Settings authorization, reusable widgets/text resources,
resizing, long-lived application budgets and compositor migration are separate.
The authoritative [ABI contract](../../GRAPHICAL-CLIENTS.md) records the wire layout,
limits, responsibility split, SDK header and lifecycle.

## Implementation

- Invocation-local handles; complete request/pixel/event memory validation.
- Session-generation and actual UID admission; no environment-selected identity.
- Opaque fixed XRGB surfaces, private compositor copies and a four-client quota.
- UI-thread-only window mutation, trusted decorations and focus-filtered events.
- Bounded queues with overflow reset and sticky close; no global input/device pointers.
- Exit/fault/cancel/timeout cleanup; stopped sessions revoke pending and active surfaces.
- Independent graphical probe/package and declared graphics capability, without
  changing the ELF loader note ABI or granting boot/configuration permissions.
- Current release plans add the graphical gate; older immutable tags without the
  harness retain their existing mandatory tests.

## Checks and corrections during implementation

716 host tests pass, including eight new graphics ownership/layout/buffer/event
checks. GCC and Clang compile the freestanding probe. All 211 common controller
regressions pass, including the expanded release plan.

The first guest attempt used named-package --yes syntax with a local archive;
the installer correctly rejected it as a filename. The fixture was stopped and
corrected; no product mutation was replayed to claim success.

The corrected initial disposable guest passes 25 graphical checks and an independent
ext2 audit: visible native pixels, pointer/key input, decoration close, fault and
cancellation recovery, signed executable upgrade 0.1.0 to 0.1.1 under unchanged
kernel bytes, pointer/layout guards and no-display refusal. The retained screenshot
shows the probe's decorated private window over the terminal.

Review additionally corrected first-frame dirty consumption, focus-loss delivery
while minimized, and retirement of minimized dead clients. An expanded fixture
initially attempted to click through a raised terminal: its trace correctly showed
focus loss. It now selects the native client's taskbar pill before testing input.
Another run passed focus isolation, quota/cancellation and executable upgrade, but
synthetic keyboard entry lost part of a status label. A subsequent retained screen
showed the first key of a new command delivered during old-window focus teardown.
The fixture now fetches scripts during serial setup, launches short lowercase paths
and permits compositor settling before the next command. It never retries a guest
mutation or relaxes the expected output/status.

## Outstanding qualification

Final source includes the expanded minimized-cancellation, different-UID denial,
focus-isolation and desktop-quota tests. Complete real-guest and clean-source
regression qualification before a release tag. No new kernel/probe tag or hosted
signing/publication/installed acceptance is claimed by this record yet.
