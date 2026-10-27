# Active work

Updated **2026-10-27**. [Status](STATUS.md) owns delivery boundaries;
[Roadmap](ROADMAP.md) orders future work.

## Functional Appearance settings

- [x] Inspect settings authority, graphics ownership and signed resource loading.
- [x] Add typed appearance persistence/revision state, bounded native snapshot/save
  requests and independent resource palettes; retain legacy themes until explicit
  preferences or signed native resources select the new language.
- [x] Add owned unsaved-state/close-intent support; preserve legacy close behavior.
- [x] Build the independent Settings Appearance page and matching CLI access.
- [x] Pass 734 host tests plus sanitizer UI/state tests and 238 controllers; verify
  permissions, malformed requests, actual preview/dirty chrome confirmation, GUI
  save/live activation, independent upgrade/reboot persistence and ext2 audit.
- [x] Clean-source Release qualification passes at `12ded91`: all eight native
  gates, including 169 kernel recovery checks, plus unchanged artifact/clean-source
  verification. [Exact evidence](archive/2026-10-27/appearance-settings.md).
- [x] Record qualification and the [kernel-first package handoff](SETTINGS-VM-TEST.md).
- [x] Operator authorizes publication; main and kernel 0.0.25/Settings 0.1.0/
  theme-default 0.1.1/manuals 0.1.6 tags pushed atomically. Remote peeled tags
  all match qualified `12ded91`.
- [ ] Confirm distributor delivery and operator-installed acceptance, then port
  other applications onto the shared GUI.

## Shared native UI foundation

- [x] Inspect the protected graphics SDK and keep the first widgets wholly userland.
- [x] Implement neutral semantic palettes, paired six-accent themes, clipped
  rounded rendering and build-time Inter text atlases with font license delivery.
- [x] Implement buttons/toggles/choices, Tab/reverse Tab, pointer capture,
  enabled/selected/pressed/focus states and focus-loss/reset cancellation.
- [x] Build independent 600x440 ui-gallery with disabled unsupported effects;
  it changes its own preview, not system settings. GCC and Clang builds pass.
- [x] Pass ASan/UBSan host checks, render/inspect dark/light actual library pixels,
  and exercise 10,000 input sequences. Package intermediates stay outside payloads.
- [x] Complete local TCG signed gallery install/upgrade, no-display refusal,
  real keyboard/pointer light/Teal selection, disabled effects, shell restoration,
  unchanged kernel digest and independent ext2 audit.
  [Evidence](archive/2026-10-27/shared-userland-ui.md).
- [ ] Implement authorized Settings state/services and resource preference loading.

[Library contract, limits and commands](USERLAND-UI.md). Fixed ASCII atlases and
static client linkage are explicit first-slice limits; no scalable/Unicode text,
live theme loading, blur, completed Settings or desktop migration is claimed.

## System design language

- [x] Record user direction before shared widgets: neutral, softly layered UI;
  light/dark together, dark default; six accents then custom picker; optional
  transparency/translucency and blur with user controls.
- [x] Draft [visual and interaction rules](DESIGN-LANGUAGE.md) and a
  [dark/light specimen](design/system-specimen.svg); separate agreed direction
  from proposed measurements and future renderer/Settings dependencies.
- [x] Render and visually inspect the SVG. Check primary/secondary text across
  all three surfaces plus six accent foreground/background combinations:
  minimum contrast 6.18:1 dark, 4.82:1 light. These checks cover opaque draft
  tokens only, not actual native rendering or composited materials.
- [x] User accepts the design baseline on 2026-10-27. Correct the specimen’s
  misaligned title-bar decorations using individually centered vector icons.
- [x] Implement the first [userland token/text/widget contract](USERLAND-UI.md).
- [ ] Define and implement the typed Settings service.
- [ ] Implement effects only with compositor support and verified opaque fallback.

## Distributor graphical-input validation

- [x] Read the failed 0.0.24 event trace: no pointer/focus-loss event, three keys
  and successful client exit. Intended mouse-driven focus changes did not occur.
- [x] Add explicit, verified virtio-mouse selection for this disposable graphical
  profile only, preserving the kernel/tag and other device coverage.
- [x] Add phase acknowledgements, bounded event/geometry diagnostics and no-build
  fixture replay on a verified independent disk copy.
- [x] Local virtio and PS/2 replays pass; unchanged tagged graphical harness passes
  48 checks and ext2 audit under the corrected controller profile.
- [x] Current hardened graphical gate passes all 48 checks and ext2 audit (133.226 s);
  all 223 controller regressions pass. Kernel/userland sources remain unchanged.
- [x] Distributor replay observes the first pointer but not focus loss; retained
  screenshot confirms the cursor is exactly at its target. Movement is correct.
- [x] Replace fixed-duration synthetic clicks with rendered-frame acknowledgement
  of held press/release, without repeating clicks or changing net position.
  The no-build replay and all 48 unchanged-tag graphical checks pass locally
  (137.135 s), with a clean ext2 audit. All 230 controller regressions pass.
- [x] Operator's distributor replay passes on QEMU 10.0.13/TCG with exact kernel
  SHA256 `078261ccf5d6bf96329361a5b94ef7d9f87efc7a2096be5ef90a89ce509c1ade`:
  pointer acknowledgement and focused-key isolation succeed; original disk preserved.
- [x] Retry passed input isolation but captured no client pixels for close. Client
  presentation/open/focus logs were present; fixed screenshot waits were not frame
  readiness. Require complete captures and visible probe pixels for all three
  fixture screenshots instead of sleeping and reading immediately.
- [x] Add --full no-build replay of every retained desktop assertion, signed probe
  upgrade and ext2 audit on a disk copy. It passes locally (21 desktop checks);
  original tagged gate passes 48 checks and all 237 controller regressions pass.
- [x] Operator confirms full desktop replay passed on QEMU 10.0.13/TCG with
  exact server kernel SHA256 `078261ccf5d6bf96329361a5b94ef7d9f87efc7a2096be5ef90a89ce509c1ade`:
  21 desktop checks and independent ext2 audit; original fixture preserved.
- [x] Operator reports 0.0.24/probe installed and tested. Screenshot confirms
  gui-probe 0.1.0, GUI_PROBE_OK, an independent visible window, focus and pointer
  events, and pointer-driven green rendering. Legacy PS/2 cause remains unconfirmed.

[Evidence and replay command](archive/2026-10-27/graphical-input-profile.md).

## Protected graphical-client API

- [x] Inspect desktop/window input and protected-process service boundaries.
- [x] Implement versioned fixed-size private XRGB surfaces, copied presentation,
  bounded focus/key/pointer/close events, session/UID admission and worker-to-UI queue.
- [x] Add owned process calls and an independently compiled graphical probe.
- [x] Pass 716 host tests and GCC/Clang builds; initial real-compositor fixture
  passes 25 checks, visible pixels, routed input/close, fault/cancel cleanup,
  same-kernel package upgrade and independent ext2 audit.
- [x] Full clean-source qualification passes at `cea1f5a`: 211 controllers,
  716 host, 71 login/session, 94 repository, 48 graphics, 72 network, 100 CLI,
  full native process/64 pipelines and desktop/local input, 169 kernel recovery.
  First-frame/focus fixes, minimized cancellation/retirement, desktop quota and
  actual UID admission denial pass. [Exact evidence](archive/2026-10-26/protected-graphical-clients.md#final-clean-source-qualification).
- [x] Operator authorizes publication; main and kernel 0.0.24/gui-probe 0.1.0
  tags pushed atomically. Both remote peeled tags resolve to exact `cea1f5a`.
- [x] Operator reports installation and testing completed; graphical-terminal
  screenshot confirms the self-check, independent drawing and pointer interaction.
  [Installed evidence](archive/2026-10-26/protected-graphical-clients.md#operator-installed-acceptance).
- [x] Verify independent package upgrade under unchanged kernel and headless refusal.
- [x] Document [ABI, SDK header and limits](GRAPHICAL-CLIENTS.md).
- [ ] Next: Settings service authorization, restart behavior and shared client
  widgets/text/resources. No Settings or compositor replacement is claimed yet.

## CLI unbundling and application foundations

- [x] Operator confirms kernel 0.0.20 is installed in the VM and running. Its
  distributor success and full qualification are archived; do not reopen the
  resolved scheduler blocker without new evidence. Explicit per-command installed
  stream exercises and `boot confirm` were not separately reported.
- [x] Priority confirmed: finish remaining command-line packages before GUI work.
  Settings and a consistent GUI design language follow the CLI milestone.
- [x] Implement bounded environment/process ownership, raw terminal leases,
  jobs/wait/cancellation and guarded namespace/seek services. Add independently
  signed base/filesystem tool recipes and normal-command dispatch with explicit
  recovery builtins. [Implemented command coverage and limits](archive/2026-10-26/cli-process-foundation.md).
- [x] Diagnose the clean candidate's actual payload rejection and fix the pinned
  runtime allocation-publication IRQ gap in build copies. Eight targeted adapter
  tests and all 64 dependency-adapter regressions pass.
- [x] Full clean-source acceptance passes at `5852867`: 197 controllers, 700 host,
  71 login/session, 92 repository, 75 CLI, full native processes with 64 pipelines
  and GUI/local input, and 169 kernel recovery checks. Exact hashes/times and
  failed-candidate evidence: [qualification record](archive/2026-10-26/allocation-publication.md).
- [x] Push source and kernel 0.0.21, Sunbeam 0.5.0, base/fs tools 0.1.0 and
  manuals 0.1.3 tags; remote peeled refs all resolve to exact tested `5852867`.
- [x] Operator confirms all five releases built, installed and boot-confirmed.
  The VM checklist passed, including routing, file commands, jobs and terminal
  input. Screenshot corroborates multi-file cat/wc/stat and file-operand ls.
- [x] Extract text/traversal algorithms with existing ABI and signed dispatch.
  Pass 17 algorithm tests, 700 host tests, Release build and initial 100-check
  installed fixture, including permissions, symlink cycles and filesystem audit.
  [Scope, limits and evidence](archive/2026-10-26/native-text-traversal.md).
- [x] Pass 206 common controller tests and both freestanding compiler builds.
- [x] Diagnose optional-index 404 masking during the first full run; preserve
  the definitive 404 instead of retrying it. Pass 701 host and all 94 real HTTPS
  repository checks, including one-request fallback and downgrade refusal.
- [x] Full clean-source qualification passes at `6615ecb`: 206 controllers,
  701 host, 71 login/session, 94 repository, 100 CLI, full native matrix/64
  pipelines, and 169 kernel recovery checks. [Exact evidence](archive/2026-10-26/native-text-traversal.md#final-clean-source-qualification).
- [x] Push kernel 0.0.22, Sunbeam 0.6.0, base/fs 0.2.0 and manuals 0.1.4;
  remote peeled tags all resolve to exact tested `6615ecb`.
- [x] Operator supplies installed graphical-terminal checklist results: sort/uniq,
  grep, seq, cut, sed, diff/status, find, du and tree match expected behavior.
  The initial sed no-op was an operator-confirmed typo; the corrected command
  returns the expected result. Exact versions and boot confirmation for this
  release were not displayed in that screenshot. [Checklist](CLI-TEXT-VM-TEST.md).
- [x] Extract the existing text/traversal command set into native packages;
  documented bounded profiles retain explicit recovery implementations.
- [x] Implement owned bounded TCP/verified-TLS connections, resolver/echo/status
  primitives and atomic download sinks; extract ip/ifconfig/nslookup/ping/nc/fetch
  into zenith-net-tools. Native code owns CLI/HTTP decoding; trusted services retain
  driver, DNS/TLS and commit policy. Preserve cancellation, credentials, handle
  cleanup and existing serialized network operations; no insecure TLS mode.
- [x] Pass 708 host tests and native client host algorithms; initial installed
  matrix passes 58 checks, full kernel delivery, ownership/fault/cancel cleanup
  and ext2 audit. [Contract and evidence](archive/2026-10-26/native-network-clients.md).
- [x] Final targeted loopback DNS/hostname, package upgrade and CA-file denial
  matrix passes 66 checks plus ext2 audit (234.326 s).
- [x] Full clean-source qualification passes at `faecc72`: 211 controllers,
  708 host, 71 login/session, 94 repository, 72 network, 100 CLI, full native
  process/64 pipelines and graphical/local input, and 169 kernel recovery checks.
  Explicit symlink-replacement and TLS hostname-mismatch refusals pass.
  [Exact source, hashes and timings](archive/2026-10-26/native-network-clients.md#final-clean-source-qualification).
- [x] Push kernel 0.0.23, zenith-net-tools 0.1.0 and manuals 0.1.5; all remote
  peeled tags resolve to exact qualified `faecc72`, with handoff notes on main.
- [x] Operator confirms the new releases installed and kernel boot-confirmed.
  Graphical-terminal screenshot verifies net-tools 0.1.0, signed nslookup/nc
  dispatch, DHCP/online status, ping, hostname TCP connect and verified TLS index
  fetch (26002 bytes). Replacement/cancellation exercises were not shown.
  [Acceptance record](archive/2026-10-26/native-network-clients.md#operator-acceptance).
  Selected base/text/filesystem/network CLI extraction is implemented and qualified.
  Shell evaluation/xargs, account/package and privileged configuration stay trusted.
- [ ] Next: protected graphical-client/settings service contract, independently
  packaged Settings and shared UI tokens/widgets. Desktop/service migration is
  unfinished; a source split or a kernel-linked Settings window is not completion.

[0.0.20 diagnosis and acceptance](archive/2026-10-18/native-pipeline-server-stall.md).
[Stream implementation](archive/2026-10-17/native-streams.md).

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

The CLI slice above adds bounded raw input, seek and jobs; general process spawning,
full TTY support and managed multicore remain future work. Existing recovery
builtins and read-only ABI-1 binaries stay supported.
