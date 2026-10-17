# Active work

Updated **2026-10-17**. [Status](STATUS.md) owns delivery boundaries;
[Roadmap](ROADMAP.md) orders future work.

## Native terminal streams and pipelines

Selected by the operator on 2026-10-17. Scope: canonical foreground input,
streamed stdout/stderr, bounded byte pipes, protected pipeline contexts and
Sunbeam wc/head/tail. Raw TTY/job control/general process scheduling remain later.

- [x] Add bounded byte channels with EOF, reader close, backpressure/cancel tests.
- [x] Connect shell pipelines and native stdin/stdout/stderr to those streams;
  retain current last-stage shell state and isolate producer state.
- [x] Serialize raw BSP execution while keeping bounded pipeline contexts rooted;
  preserve maintenance exclusion, identity, resource limits and cleanup.
- [x] Reuse local/desktop/serial line discipline for live input, Ctrl+D and Ctrl+C.
- [x] Add Sunbeam wc/head/tail and declare a separate native stream capability.
- [ ] Verify host/native streaming, Unicode/binary pipes, early consumer exit,
  stderr separation, interactive input, cancel/fault cleanup and release gates.

Host stream/console suite passes (654 total host tests); freestanding Sunbeam build and
five host utility-algorithm tests pass. Release ISO/ELF builds. The first native
run reached live input/output successfully but its assertion expected an intact
keyboard echo despite existing serial debug interleaving; it was corrected to
require the tool's complete output line. The next run verified EOF/cancel, 160 KiB protected pipelines/FP state and binary
redirection; its Unicode assertion exposed the existing ASCII-only serial logger.
UTF-8 is now checked through native byte counts and independent persisted bytes,
with terminal decoding covered separately. A focused test then reproduced Cosmos
recursive exception fail-fast during simultaneous native cancellation. Stream/pipe
cancellation now uses errno/normal cleanup rather than concurrent exceptions;
early-cancelled stages and sleeping builtins return 130 directly. Repeated native
and managed pipeline cancellation are regression gates. Serial read ownership now
ends immediately on Ctrl+C and preserves public partial/queued input plus EOF;
secret type-ahead remains discarded. Expected native setup/VFS errors are converted
under a managed-service gate, which is never held across blocking standard streams.
Focused fresh-install TCG acceptance passed in 69.268 s: five early/blocked native
pipeline cancellations, managed sleep cancellation, simultaneous missing-file/setup
errors, eight stages, early reader exit, usable shell and clean shutdown; no runtime
fail-fast. Full clean-source/common acceptance is pending.
No stream release is tagged, published or installed yet.

## Intermittent installed TCP connections

- [x] Trace refresh/update/fetch/nslookup: downloads resolve automatically through
  the same resolver; no separate manual DNS priming requirement or client DNS cache.
- [x] Operator reproduced on 2026-10-17 after successful full refresh: subsequent
  update refreshed core generation 24, then extra exhausted both fetch/connect
  attempts to zenith-stable.net.local:443 (10.0.10.3).
- [x] Operator capture reproduces SYN -> stale plain ACK -> appropriate RST;
  later successful traffic in that capture is another VM. Waiting subsequently
  allowed the operator's command to work. Actual pinned DLL inspection confirms
  source ports restart at 49152 each boot; external-state collision is a hypothesis.
- [x] Operator says failures also occur without reboot/resume. Stale external
  state remains a working hypothesis; its exact location is not established.
- [x] Implement entropy-seeded dynamic-port offset/stride with active-port checks;
  pass 639 host tests, 12 real-dependency adapters and a Release ISO/ELF build.
- [x] Targeted TCG TCP/HTTPS regression passed (100.486 s): bounded exhaustion,
  recovery, 12 verified HTTPS fetches, new source ports on wire and clean poweroff.
- [x] Clean-source acceptance passed at `db90ebc`: 112 controllers, 639 host,
  71 login/session, 92 repository, native programs and 169 kernel recovery checks.
  Repeat exact-artifact TCP/HTTPS and all 12 adapters passed.
- [x] Operator authorized source/release publication; `kernel/v0.0.17` pushed at
  exact tested `db90ebc` with source/validation notes on main.
- [x] Operator reports 0.0.17 installed and initially working after release handoff.
- [ ] Explicit boot confirmation and sustained repeated VMware refresh/update are
  not separately reported. Stale-flow location remains unconfirmed.

Earlier timeout-retirement/fresh-socket retry is implemented and locally tested;
actual-path reliability is still open. See [Networking](components/NETWORKING.md)
and [packet/dependency evidence](archive/2026-10-17/tcp-stale-flow.md).

## Dashboard retry controls

- [x] Add Retry build for failed/interrupted rows, background queue/service API,
  duplicate/busy guards and restricted installed publisher helper.
- [x] Add helper/API regression tests; validate rendered desktop/mobile controls.
- [x] Operator reports restored dashboard running and all three failed-delivery
  records green after retries on the replacement distributor.
- [ ] Retry persistence across browser/SSH closure is not separately reported.

[Validation and deployment boundaries](archive/2026-10-17/dashboard-retry.md).

## Native input/write handoff

- [x] Repair and verify normal Cosmos VS Code Debug/Release ISO builds; operator
  confirms they work. Cosmos stays pinned at 3.0.89.
- [x] Implement finite shell stdin, guarded regular-file write/create/truncate/
  append/flush, resource limits and cleanup; add independent Sunbeam `tee`/stdin `cat`.
- [x] Declare the native I/O capability and update references/manuals.
- [x] Pass clean-source common acceptance at `2a515e8`: 109 controllers, 632 host,
  71 login/session, 92 repository, expanded native programs and 169 kernel checks.
- [x] Push `kernel/v0.0.16`, `sunbeam-tools/v0.3.0` and
  `zenith-man-pages/v0.1.1` at that exact tested source.
- [x] Operator reports replacement distributor restored, all three release records
  green after verification retries, and a full installed-system update successful.
- [ ] Explicit boot confirmation and piped-input/persistent-write acceptance remain
  separately unreported; repeated repository access has the TCP blocker above.

[Detailed evidence and failed attempts](archive/2026-10-16/native-input-and-writes.md).
The long GUI gate uses virtio keyboard; separate standard-VGA recovery retains
PS/2 coverage. Sustained legacy PS/2 input loss in the affected QEMU graphical
profile remains open in [Shell](components/SHELL.md).

Canonical streams/byte pipes are the active slice above; raw TTY, seek and general
process scheduling remain future slices. Existing recovery builtins and read-only ABI-1 binaries stay supported.
