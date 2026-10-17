# Active work

Updated **2026-10-17**. [Status](STATUS.md) owns delivery boundaries;
[Roadmap](ROADMAP.md) orders future work.

## Native terminal streams: release handoff

- [x] Implement live stdout/stderr, canonical input/EOF, bounded byte pipes and
  protected pipeline contexts; preserve state, identity and cleanup.
- [x] Add Sunbeam wc/head/tail, stream capability and updated manuals.
- [x] Fix status inheritance, concurrent cancellation/error handling and console
  input handoff; add regression coverage.
- [x] All clean-source test gates at `cba1cbf`: 117 controller, 654 host,
  71 login/session, 92 repository, expanded native programs and 169 kernel checks.
  Serial, desktop and local text input/EOF/cancel and binary file audits passed.
  Evidence-copy tmpfs exhaustion was recovered; final integrity checks passed.
- [x] Operator approved release; main and immutable kernel 0.0.18, Sunbeam 0.4.0,
  manuals 0.1.2 tags pushed. All three remote tags resolve to tested `cba1cbf`.
- [ ] Distributor delivery and installed VMware acceptance: kernel update,
  reboot/confirm first, then tools/manuals and representative pipe/input checks.

[Implementation, failures, commands and evidence](archive/2026-10-17/native-streams.md).
Raw TTY/jobs, seek and general process scheduling remain future work.

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
