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

## Pre-qualification boundary (superseded by the result below)

Final source includes the expanded minimized-cancellation, different-UID denial,
focus-isolation and desktop-quota tests. Complete real-guest and clean-source
regression qualification before a release tag. No new kernel/probe tag or hosted
signing/publication/installed acceptance is claimed by this record yet.

## Final clean-source qualification

Exact tested source: **cea1f5ace138e001026602affb13c637a1aee7b9**. The worktree
was clean throughout qualification. Cosmos 3.0.89, local .NET 10, QEMU 11.1.2 and
TCG were used. A fresh unsigned Release ISO/ELF was built; no production signing
keys or installed operator disks were used.

Command: `ZENITH_VALIDATION_ACCELERATOR=tcg python3 -B deploy/distributor/validate.py --keep obj/graphics-qualified-cea1f5a`
(with the local dotnet toolchain on PATH). Retained ignored logs/fixtures are in
that workspace directory; the durable reproduction contract is the source and command.

| Gate | Result | Seconds |
|---|---|---:|
| Common controller regressions | 211 passed | pre-build gate |
| Host tests | 716 passed | 9.407 |
| Fresh kernel build / package tool | Passed | 25.427 / 1.013 |
| Login/session | 71 passed | 205.310 |
| Signed repository | 94 passed | 358.491 |
| Protected graphical clients | 48 passed; visible pixels, focused input isolation, close, minimized cancellation/retirement, four-client quota/recovery, UID denial and same-kernel upgrade | 138.185 |
| Native network | 72 passed; TLS, cleanup and ext2 audit | 250.001 |
| Native CLI | 100 passed; raw input/fault/cancel restoration | 222.967 |
| Native process | Passed; all 64 pipelines plus desktop/local input | 735.415 |
| Kernel update/recovery | 169 passed; independent ext2/EFI audits | 493.815 |

```text
Zenith.iso SHA256 bdc019ee94ada505fb53f348a9b83c0db7e6d8ea73ef816c39d7b19533edfa4d
Zenith.elf SHA256 3e09627e7a6c518516f088e36b7eee0a40a498e6d905734e27b192bb090b0281
```

All gates completed without an edit or retry of the qualified source. The GUI
fixture uses preloaded scripts and compositor settling; minimized-client cleanup,
real focus filtering and different-UID/USER spoofing denial all pass. A signed
probe upgrade from 0.1.0 to 0.1.1 leaves kernel bytes unchanged, with a clean ext2 audit.

Prepared release targets: kernel **0.0.24** and zenith-gui-probe **0.1.0**, both
at exact tested `cea1f5a`. Documentation-only follow-ups must not replace that tag
target. Publication requires operator authorization; no new tag or distributor
signing/delivery is claimed yet. [Installed VM checklist](../../GRAPHICAL-VM-TEST.md).

Next implementation: typed Settings service authorization/restart semantics and
shared client widgets/text/resources. This is a qualified first protected client
API, not a completed Settings application or independent compositor.
