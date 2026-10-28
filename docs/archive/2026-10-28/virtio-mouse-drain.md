# Stranded virtio mouse input — 2026-10-28

Owners: H1/Q2 in [master roadmap](../../ROADMAP.md). Kernel candidate; existing
kernel/v0.0.27 remains immutable. This is separate from the earlier controller
public-prompt handoff fixes, which the operator's later replay passed.

## Reproduction and boundary

The distributor's focused replay passed two maximize/restore cycles, then failed
an ordinary move from (1222,22) to (938,184). Both MouseManager samples remained
(1222,22), matching the framebuffer. The scheduler continued running. This ruled
out a cursor-only stale presentation for that failure; it did not yet identify
whether the device had queued the missing movement.

The laptop uses QEMU 11.1.2; the distributor uses Debian QEMU 10.0.13. The existing
local `zenith-qemu-trixie:ovmf-repro` image contains the exact Debian emulator
(`10.0.13+ds-0+deb13u1`) and its OVMF firmware. With the unchanged server ELF
`32a0f8f77117910e2036b06892626acf0a48c343040a8da2f517f71f7cabba4b`,
ten ordinary cycles passed, but a repeat with Docker's CPU quota at 0.5 reproduced
the missed large movement immediately after the wheel/search prelude.

Both bounded typed device observations reported:

| Field | Value |
|---|---:|
| Queue size | 64 |
| Driver last-used index | 1430 |
| Device used index | 1462 |
| Pending used buffers | 32 |
| Free descriptors | 32 |
| Initialized / IRQ registered | true / true |
| Partial report pending | false |

MouseManager stayed at (746,466), and QEMU recorded 283 queue-full trace events.
All 32 submitted receive buffers had been delivered but were not being consumed.
The old ELF has no reachable MouseManager.Poll: input delivery depends on the
interrupt handler. A missed/coalesced notification can therefore leave the queue
stranded while timers, rendering and other workers continue. The exact interrupt
signalling failure is not established; the missing periodic drain is a confirmed
recovery gap, rather than a test deadline or pixel-tolerance issue.

## Implementation

- GUI Input.Poll explicitly calls MousePolling.Drain before sampling coordinates,
  buttons and wheel state. The pinned adapter binds this bridge to the existing
  Cosmos mouse-manager poll in copied build references. An unadapted bridge throws
  rather than silently omitting the recovery path.
- The copied manager's poll saves/disables IRQs, drains registered devices, then
  restores the caller's prior IRQ state on success and caught failure. This keeps
  BSP polling and device interrupt processing from re-entering the same queue.
- The copied virtio-mouse drain processes at most 64 used buffers per invocation,
  including IRQ entry. Partial reports retain their existing state until the next
  SYN; remaining work is consumed by the next poll/notification. No IRQ-disabled
  unbounded input loop is introduced.
- Original dependency files and the sibling Cosmos checkout are untouched.
  Controller cursor destinations, deadlines and tagged application assertions are
  unchanged. No mouse movement or click is repeated to manufacture a pass.
- Source-specific runtime-adapter tests execute after the kernel build in unsigned
  acceptance and distributor builds, and join CI's existing adapter checks.

## Local candidate evidence

An exploratory Release candidate (`036935933693dc037791c1f2ef3bc88aa2593cd8219e7205bd275354aa80e5af`)
built successfully; 782 host tests passed. Tests execute the adapted real manager
wrapper and bounded queue helper with hardware calls stubbed: prior-enabled and
prior-disabled IRQ states, exception restoration, 100 queued events across two
bounded polls, no duplicates, zero-budget outputs, bridge binding/fail-closed
behavior, idempotence and contract drift.

Using QEMU 10.0.13, matching container OVMF and the same 0.5 CPU quota:

- Guest installation/package setup completed. A controller selection bug was found:
  stopped setup/live VMs shared the same monitor parent as the desktop. The public
  prompt observer now selects the live VM; a regression covers that fresh path.
- The prepared Settings sequence then passed in 218.246 seconds: unchanged UI,
  account/permissions, hidden cancellation, wheel, maximize/restore, launcher,
  package upgrade, reboot persistence and independent ext2 audit assertions.
- Twenty focused maximize/restore cycles passed in 181.609 seconds: 80 button
  commands, no cursor correction or click replay, original disks preserved.

The reproduction uses a private wrapper around the cached Docker image, not a
system QEMU replacement. Per-process resource counters in these runs describe the
Docker launcher, not actual QEMU CPU usage; no CPU-performance conclusion uses
those counters. Image identity:
`sha256:7d3945295ee559157f6f9ff7504bd67483fea789d273aec5f4abb6724b088829`.
Evidence is retained under ignored `obj/maximize-input-qemu10*` and
`obj/mouse-poll-candidate-*`. The modified source still requires clean-source full
release qualification before any new kernel tag. Distributor and installed-VM
acceptance remain separate; physical/PS2 behavior and the earlier installed
wheel/drag freeze are not certified by this virtio-specific reproduction.

## Clean-source release qualification

Clean source commit `5306703` passed fresh unsigned acceptance with:

```sh
PATH="$PATH:/home/azazel/.dotnet/tools:/usr/sbin:/sbin" \
ZENITH_VALIDATION_ACCELERATOR=tcg ZENITH_NATIVE_JOBS=4 \
python3 -B deploy/distributor/validate.py --keep obj/mouse-poll-qualified-5306703
```

322 controller tests, 782 host tests and eight real runtime-adapter tests passed.
All ten native gates ran fresh and passed; no receipt was reused:

| Gate | Seconds |
|---|---:|
| Login/session | 224.507 |
| Settings | 285.135 |
| Network | 281.334 |
| Online repository | 435.208 |
| CLI | 248.508 |
| Graphical clients | 155.467 |
| Materials | 84.437 |
| System app styling | 38.218 |
| Kernel update/recovery | 681.124 |
| Native processes/terminal streams | 765.966 |

Independent gates overlapped; these durations must not be summed as elapsed wall
time. Kernel update completed all 169 checks, repository all 94, network all 72,
CLI all 100, and native processes retained all 64 exact byte-stream repetitions.
The fresh Settings gate included wheel, resize, public/hidden handoff, independent
package upgrade, reboot persistence and the ext2 audit.

Artifacts qualified at this source commit:

- ELF SHA256: `5fc6d12aa26a2acf23b859a8fe2fe0c29171f287c7911611e1b1cdd36659477b`
- ISO SHA256: `7b7b6965a58dabe95b71e9ed3dc28525ee639a2f51b702515b7fff35987c6290`

The same clean ELF and a separate copy of the completed desktop fixture also
passed twenty exact maximize/restore cycles under QEMU 10.0.13 and a 0.5 CPU quota
in 185.039 seconds (`obj/mouse-poll-clean-qemu10/result.json`). All 80 button commands
completed, without coordinate correction or click replay. The original fixture
was preserved. This repeats the device-specific regression on the actual qualified
binary rather than relying on the exploratory build.

A successor kernel release is ready for publication approval. Existing 0.0.27 is
unchanged. Distributor and installed acceptance, including sustained wheel/drag
behavior, remain pending; local success does not certify those environments.
