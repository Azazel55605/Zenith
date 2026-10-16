# Current system status

Reviewed **2026-10-16**. Source baseline: `f502085`. This page owns the cross-component
summary; [component checklists](README.md#components) own detailed work.

## Release and installation boundary

| Item | What is established | What remains |
|---|---|---|
| Kernel 0.0.12 | Operator reports installed and working login; supporting resource packages installed | No broader hardware certification implied |
| Kernel 0.0.13 | Implemented, locally built/tested, tag pushed | Latest reported server failure: native password-update completion at 30 seconds; shared authentication policy awaiting retry; publication/installed acceptance pending |
| Sunbeam tools 0.2.0 | Read-only file tools locally verified; independent tag pushed | Server publication and installed acceptance with kernel 0.0.13 |
| Home repository | Operator has used verified HTTPS and installed packages by name | Backup restore test not confirmed; no live server audit in this docs review |

**Upgrade order for the new native file tools:** update the kernel, reboot and
confirm it, then update Sunbeam. New bundles require `zenith-native-fs-abi 1.0`;
older kernels must reject that dependency. See [native guide](NATIVE-PROGRAMS.md).

## Component summary

| Component | Implemented and verified scope | Remaining scope |
|---|---|---|
| [Shell/terminal](components/SHELL.md) | Scripting, layouts/history, worker cancellation and manuals | Streaming byte pipes, raw TTY/jobs and larger CLI ports |
| [Storage/security](components/STORAGE-SECURITY.md) | Bounded ext2, virtual filesystems, users and access enforcement | Larger filesystem profile, journaling/guest repair, broader hardware |
| [Boot/recovery](components/BOOT-RECOVERY.md) | Guided install, UEFI disk boot, signed retained kernels, explicit confirm/rollback, synced power actions | Matched root/package snapshots and automatic trial/fallback |
| [Networking](components/NETWORKING.md) | Primary IPv4 NIC, DHCP/static, DNS/NTP, bounded HTTP and verified HTTPS | General socket ABI, wider NIC/hardware support, authenticated time |
| [Packages](components/PACKAGES.md) | Signed core/extra catalogs; dependency/conflict plans; install/remove/verify; all-package update; build/sign/publish automation and dashboard | Stronger trust lifecycle, configuration merges, matched data/boot transaction |
| [Sessions/desktop](components/SESSIONS-DESKTOP.md) | Login, local text/DE switching, serial fallback, signed theme/fonts/branding and style meta | Proper DE, automatic-tiling WM, Settings UI, Files/Editor rework, GUI isolation |
| [Userland](components/USERLAND.md) | Protected foreground x64 ELF; separately packaged Sunbeam; bounded read-only filesystem ABI | stdin/writes, byte streams/TTY, general process lifecycle, network/GUI APIs |
| [Multicore](components/MULTICORE.md) | Opt-in native integer workers, default disabled | Managed multicore scheduler, GC coordination, safe parallel drivers/processes |

The shell, most commands, GUI and privileged services remain kernel-linked.
Native Sunbeam is isolated; the desktop is still trusted kernel code. An independent
resource package does not make its consuming renderer an independent executable.

## Latest local evidence

| Scope | Recorded result | Source / durable record |
|---|---|---|
| Shared authentication validation | 85 regressions; 69 login/session checks (242.241 s); native-program gate (246.195 s); unchanged ISO, forced TCG | [authentication record](archive/2026-10-16/authentication-validation.md); server retry pending |
| Native filesystem slice | 620 host tests; Release build; expanded native gate with opt-in two-CPU workers passed in 297.571 s | `f502085`; [slice record](archive/2026-10-15/PROGRESS.md#2026-10-15--native-filesystem-tool-unbundling-locally-verified); [harness](../tools/native-process-smoke.py) |
| Recovery transport fix | 169/169 kernel checks; 69 login/session checks | `61618cd`; [recovery record](archive/2026-10-15/PROGRESS.md#2026-10-15--displayed-kernel-recovery-validation-locally-verified) |
| Dashboard superseded failures | 19 dashboard tests | `d521b9c`; [record](archive/2026-10-15/PROGRESS.md#2026-10-15--hide-superseded-failed-dashboard-activity) |

These are separate runs. The complete kernel-update and repository suites were
not repeated locally for 0.0.13; they remain required distributor gates. Local
`/tmp` evidence paths in the archive may expire; use the commit, harness and
retained distributor logs to reproduce or investigate a release.
