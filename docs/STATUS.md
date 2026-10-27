# Current system status

Reviewed **2026-10-27**. Stability fixes: `c622c13`, `ce42cfd`; native filesystem
feature baseline: `f502085`; TCP reliability candidate: `f6bf893`. This page owns the cross-component
summary; [component checklists](README.md#components) own detailed work.

Kernel **0.0.21**, Sunbeam **0.5.0**, base/filesystem tools **0.1.0** and manuals
**0.1.3** are operator-confirmed built, installed and boot-confirmed. The operator
also confirms the complete VM checklist passed. [Acceptance record](archive/2026-10-26/allocation-publication.md#operator-acceptance).

The text/traversal release is qualified at exact **6615ecb**: ordinary commands
now dispatch to signed native filters and traversal tools. Full clean-source
acceptance passes 206 controllers, 701 host and all five native gates. It also
preserves definitive optional-index 404 errors instead of masking them through a
transport retry. [Scope, limits and exact evidence](archive/2026-10-26/native-text-traversal.md).
The operator supplies successful installed graphical-terminal text/traversal
checks; the initial sed no-op was a confirmed typo. Exact package versions,
0.0.22 boot confirmation and distributor receipts were not displayed in that
screenshot. [VM handoff](CLI-TEXT-VM-TEST.md).

The selected CLI network extraction is fully qualified at **faecc72**: owned
TCP/verified-TLS services and atomic download sinks support independently packaged
ip/ifconfig/nslookup/ping/nc/fetch. All six native gates pass. Kernel **0.0.23**,
**zenith-net-tools 0.1.0** and manuals **0.1.5** are operator-reported installed,
with kernel boot confirmation. The graphical-terminal network checks pass;
replacement/cancellation checks were not separately shown. [Evidence](archive/2026-10-26/native-network-clients.md) · [VM checklist](NETWORK-VM-TEST.md).
Graphical-client/service migration remains unfinished; shell evaluation and
privileged administration remain trusted.

The first [protected graphical-client API](GRAPHICAL-CLIENTS.md) is implemented:
private copied surfaces, routed events and session/UID ownership, with a separate
probe executable. Full clean-source qualification passes at `cea1f5a`, including 48 graphics checks
and all six existing native gates. The operator reports the release installed and tested; the screenshot confirms
gui-probe 0.1.0, its self-check, independent drawing and pointer interaction. Settings and compositor migration remain
unfinished. [Execution evidence](archive/2026-10-26/protected-graphical-clients.md).

## Release and installation boundary

| Item | What is established | What remains |
|---|---|---|
| Graphical API / kernel 0.0.24 | Full clean-source qualification at `cea1f5a`; private surfaces/events and independent probe, all seven native gates pass | Controller input/frame readiness corrections pass the full server desktop replay. Operator reports installed and tested; screenshot verifies probe 0.1.0, self-check, drawing and pointer events. Settings remains next |
| Kernel 0.0.23 / net-tools 0.1.0 / manuals 0.1.5 | Full clean-source qualification at `faecc72`; 72 native network checks and all other gates pass | Operator confirms installation and boot confirmation; screenshot verifies net-tools 0.1.0, signed routing and home-path network/TLS checks. Replacement/cancellation not separately shown |
| Kernel 0.0.22 / text/traversal packages | Full clean-source acceptance at `6615ecb`; explicit native profiles and 404 preservation | Installed functional checklist matches expected output; exact versions/boot confirmation and distributor receipts not separately displayed |
| Kernel 0.0.21 / CLI packages | Full clean-source acceptance at `5852867`; release tags target that exact commit | Operator confirms distributor builds, installation, boot confirmation and complete VM checklist; broader hardware acceptance remains separate |
| Kernel 0.0.20 | Tagged at fully accepted `431dfb9`; operator reports successful distributor run | Operator confirms installed and running; explicit boot confirmation and individual native stream/input checks not separately reported |
| Kernel 0.0.12 | Operator reports installed and working login; supporting resource packages installed | No broader hardware certification implied |
| Kernel 0.0.13 | Tag pushed; server validation exposed ext2 corruption: directory `/var/lib` became a regular file at inode 49 | Failed version remains immutable; a retry cannot include the driver fix |
| Kernel 0.0.14 | Tagged at `fe1f5a9`; operator log confirms server native gates passed, signed archive published and core/extra indexes generation 15 published | Gateway stale DNS cache resolved; operator reports 0.0.14 installed. Intermittent TCP connect failures led to the 0.0.15 reliability release. Explicit boot confirmation is not separately reported |
| Kernel 0.0.15 | Tagged at `f6bf893`; full clean-source pre-tag acceptance passed, including all four native gates; bounded TCP connect retry/retirement and endpoint diagnostics | Tag pushed; operator reports the TCP issue seems gone after handoff. Exact installed version, explicit boot confirmation and individual server delivery checks were not separately supplied |
| Sunbeam tools 0.2.0 | Read-only file tools locally verified; independent tag pushed | Operator screenshot shows Sunbeam 0.2.0 installed alongside kernel 0.0.14; interactive file-tool acceptance remains |
| Kernel 0.0.16 / Sunbeam 0.3.0 / manuals 0.1.1 | Tags pushed at fully tested `2a515e8`; finite stdin, guarded writable descriptors and independent tee | Operator reports all three published and reconciled green on the replacement distributor, then a successful full system update; explicit boot confirmation and native I/O exercise remain unreported |
| Kernel 0.0.17 | `db90ebc` adds entropy-seeded outgoing TCP port allocation; full clean-source acceptance, 12 dependency adapters and focused TCP/HTTPS regression passed | Tag pushed at exact tested source; operator reports 0.0.17 installed and initially working. Explicit boot confirmation and sustained VMware reliability remain separately unreported; stale-flow cause not established |
| Home repository | Operator reports replacement VM restored, delivery verification retries successful and full guest update successful | Repeated guest refresh/update still intermittently exhausts TCP connects to correctly resolved 10.0.10.3:443; packet diagnosis pending |

**Upgrade order for the new native file tools:** update the kernel, reboot and
confirm it, then update Sunbeam. New bundles require `zenith-native-fs-abi 1.0` and, for current input/write builds,
`zenith-native-io-abi 1.0`;
older kernels must reject that dependency. See [native guide](NATIVE-PROGRAMS.md).

Native streams and independent wc/head/tail were released for validation in
0.0.18, whose distributor gate failed. Locally accepted 0.0.19 also stalled on the
server. Typed queue observations identified unsigned ticket underflow and global
pass starvation; 0.0.20 corrects that accounting and includes the earlier queue,
GC-root, wait and GUI EOF fixes. It also makes the stderr fixture tolerate known
Cosmos trace interruptions while requiring exact error/output/status evidence.
The operator confirms the 0.0.20 distributor run succeeds. Detailed failed
candidates and acceptance evidence are preserved in the
[server investigation](archive/2026-10-18/native-pipeline-server-stall.md) and
[prior scheduler/GC record](archive/2026-10-17/scheduler-late-wake.md).

## Component summary

| Component | Implemented and verified scope | Remaining scope |
|---|---|---|
| [Shell/terminal](components/SHELL.md) | Scripting, layouts/history, live native streams and bounded byte pipes, cancellation and manuals | Raw-input/jobs/base/fs operator-accepted; text checklist accepted; network installed checks accepted within the recorded checklist scope |
| [Storage/security](components/STORAGE-SECURITY.md) | Bounded ext2, virtual filesystems, users and access enforcement | Larger filesystem profile, journaling/guest repair, broader hardware |
| [Boot/recovery](components/BOOT-RECOVERY.md) | Guided install, UEFI disk boot, signed retained kernels, explicit confirm/rollback, synced power actions | Matched root/package snapshots and automatic trial/fallback |
| [Networking](components/NETWORKING.md) | Primary IPv4 NIC, DHCP/static, DNS/NTP, HTTP/verified HTTPS and qualified owned native clients | General socket ABI, wider NIC/hardware support, authenticated time |
| [Packages](components/PACKAGES.md) | Signed core/extra catalogs; dependency/conflict plans; install/remove/verify; all-package update; build/sign/publish automation and dashboard | Stronger trust lifecycle, configuration merges, matched data/boot transaction |
| [Sessions/desktop](components/SESSIONS-DESKTOP.md) | Login, local text/DE switching, serial fallback, signed theme/fonts/branding and style meta | Proper DE, automatic-tiling WM, Settings UI, Files/Editor rework, GUI isolation |
| [Userland](components/USERLAND.md) | Protected foreground x64 ELF; separately packaged Sunbeam; bounded filesystem/stream ABI, canonical input and independent wc/head/tail | Selected CLI extraction operator-exercised; first protected GUI API qualified; Settings and service/compositor migration remain |
| [Multicore](components/MULTICORE.md) | Opt-in native integer workers, default disabled | Managed multicore scheduler, GC coordination, safe parallel drivers/processes |

The shell, recovery builtins, GUI and privileged services remain kernel-linked.
Installed base/file command names dispatch to verified native packages.
Native executables are isolated; the desktop is still trusted kernel code. An independent
resource package does not make its consuming renderer an independent executable.

## Latest local evidence

| Scope | Recorded result | Source / durable record |
|---|---|---|
| Native network clients | 211 controllers; 708 host; 71 login, 94 repository, 72 network, 100 CLI, full native-process/64 pipelines and graphical/local input, 169 recovery | `faecc72`; [exact evidence](archive/2026-10-26/native-network-clients.md#final-clean-source-qualification) |
| CLI foundation / allocation publication | 197 controllers; 700 host; 71 login, 92 repository, 75 CLI, full native-process matrix/64 pipelines, 169 recovery; 64 dependency-adapter regressions | `5852867`; [exact evidence](archive/2026-10-26/allocation-publication.md) |
| Native stream slice | 117 controllers; 654 host; 71 login, 92 repository, expanded native programs and 169 kernel recovery checks | `cba1cbf`; [stream evidence and handoff](archive/2026-10-17/native-streams.md); publication/operator acceptance pending |
| Native input/write slice | 109 controllers; 632 Release host; 71 login, 92 repository, expanded native programs and 169 kernel recovery checks | `2a515e8`; [evidence and keyboard-profile limits](archive/2026-10-16/native-input-and-writes.md) |
| TCP reliability candidate | 109 controller and 628 Release host tests; 69 login, 92 repository, native programs and 169 kernel recovery checks; nine adapter regressions and forced TCP failure/recovery plus 12 verified HTTPS fetches | `f6bf893`; [TCP record](archive/2026-10-16/tcp-connect-reliability.md); operator reports real-path improvement after handoff |
| Ext2 fix and common release acceptance | 109 controller tests; 624 Release host tests; all four common TCG gates (69 login, 92 repository, native programs, 169 kernel recovery); independent ext2/EFI audits; directory inode 49 preserved after upgrade | `c622c13`, `ce42cfd`; [corruption investigation and acceptance](archive/2026-10-16/kernel-update-ancestor-investigation.md); hosted CI/server/installed acceptance pending |
| Native filesystem slice | 620 host tests; Release build; expanded native gate with opt-in two-CPU workers passed in 297.571 s | `f502085`; [slice record](archive/2026-10-15/PROGRESS.md#2026-10-15--native-filesystem-tool-unbundling-locally-verified); [harness](../tools/native-process-smoke.py) |
| Recovery transport fix | 169/169 kernel checks; 69 login/session checks | `61618cd`; [recovery record](archive/2026-10-15/PROGRESS.md#2026-10-15--displayed-kernel-recovery-validation-locally-verified) |
| Dashboard superseded failures | 19 dashboard tests | `d521b9c`; [record](archive/2026-10-15/PROGRESS.md#2026-10-15--hide-superseded-failed-dashboard-activity) |

These are separate runs. Complete kernel-update and repository suites passed in
the rebuilt candidate's common acceptance run; they remain required distributor gates. Local
`/tmp` evidence paths in the archive may expire; use the commit, harness and
retained distributor logs to reproduce or investigate a release.

## Shared UI foundation (2026-10-27)

The accepted [design language](DESIGN-LANGUAGE.md) now has a first independently
compiled [client widget/text library and gallery](USERLAND-UI.md). Local GCC/Clang,
sanitizer/palette checks and real-compositor keyboard/pointer/upgrade/ext2 checks
pass on an unchanged kernel. This is not published/operator-installed acceptance;
Settings persistence, resource preference loading, scaling/Unicode text and optional
compositor effects remain pending. [Evidence](archive/2026-10-27/shared-userland-ui.md).
