# System audit and VMware power-off — 2026-10-30

Owners: mapped per finding in the [master roadmap](../../ROADMAP.md). A targeted review of
input, power, locking and native-window paths, not an exhaustive audit. Each entry says
whether it was reproduced or only read from source.

## Fixed in this batch

| Finding | Evidence | Change |
|---|---|---|
| VMware reports a halted CPU on shutdown | Cosmos discards the LAI result, tries only QEMU/Bochs/VirtualBox ports, then `cli; hlt`. Not run on VMware | ACPI mode requested when firmware left it off, direct `_S5` write, then LAI with a logged result. A QEMU BIOS boot logged `sci=0` then `sci=1` and powered off. Firmware values go to `boot.log.1`. VMware acceptance is pending |
| Launcher Shut down/Restart panics while any native app runs | Reproduced: `panic: InvalidOperationException` | Items are disabled with the reason; clicks no longer reach the panic path; login screen too |
| Terminal could stay busy forever after an unexpected command exception | Read from source; relied on `finally`, which Cosmos can skip | Explicit catch and flag reset |

## Open

| Severity | Finding | Evidence | Owner |
|---|---|---|---|
| High | A CPU-bound native program blocks the scheduler and desktop for up to about 10 s, then dies with status 124: the raw window only counts timer ticks and never yields | Source (`z_bridge`), not measured | U1 |
| High | `lock` bodies that throw may never release, since Cosmos can skip unwind-finally; `Installer.cs:65` throws inside the maintenance gate that `PowerControl` blocks on. 156 `lock` statements exist | Source plus the repository's own notes; failed mounts did not reproduce it because they return errors | U1/R3 |
| Medium | `SystemMounts.Shutdown` stops log persistence and unmounts in order; a failure mid-way leaves a running system with some filesystems gone | Source | R3 |
| Medium | The 8042 driver has no stuck-buffer recovery, no status/AUX check and no mouse packet resync. The freeze cause is fixed, but this defense was proposed and not added | Source | H1 |
| Medium | If every power-off path fails, the CPU parks on a frozen desktop with no "safe to power off" message | Source | R3 |
| Medium | Shut down needs every native app closed by hand; there is no close-all flow, and the native Welcome counts | Reproduced | W3 |
| Low | Gate timeouts are load-sensitive: an install took 13x longer right after a kernel build and failed at 300 s; the rerun passed | Reproduced once | Q3 |
| Low | Sunrise `ClearSecrets` drops string references; managed password strings are not wiped | Source | R3 |
| Low | System window hard-codes version 0.1; About reports the kernel version as not recorded | Observed | U5 |
| Low | Below-the-fold form controls cannot be reached with Tab until scrolled | Known from stage 2 | U1 |
| Low | Hardware exceptions from user mode such as NMI are treated as that program's fault | Source | U6 |
