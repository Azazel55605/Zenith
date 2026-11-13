# Observatory

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md)

Work IDs below are detailed steps of the master tracker (U7), not a separate schedule.

Observatory is the system, process and resource monitor, modeled on Windows Task
Manager and Mission Center. [Concept](../design/observatory-concept.svg). It is a
native package, `zenith-observatory`, built from the shared toolkit (`userland/ui`).

## The boundary

The kernel samples and the client draws. The monitor holds no references to
programs and takes no service locks while sampling. A reader sees the same
programs `ps` shows that account: its own, or everyone's for root. End process
is the cooperative stop `kill` sends, authorized by owner, never a forced kill.

**CPU usage is measured, not estimated.** Every managed halt goes through the
runtime's `_native_cpu_halt` import, which an adapter routes to
`zenith_cpu_halt_counted`. That opens a halted span in TSC ticks for this CPU.
The managed interrupt dispatch closes an open span first, so a timer switch to
another thread never counts that thread's run time as idle. With `smp=native`,
an AP's idle park is counted the same way. Busy is the share of a second that a
CPU was not halted. Interrupt handler time inside a halt counts as idle until
dispatch, a few microseconds per interrupt.

## Native ABI: `zenith-native-monitor-abi` 1.0

| Call | Arguments | Result |
|---|---|---|
| 642 | 4096-byte buffer, 4096 | System snapshot: CPUs in use and detected, uptime, memory, programs, frames, processor name, latest sample and up to 120 samples, oldest first |
| 643 | 4096-byte buffer, 4096 | Program list: up to 63 records (PID, UID, state, stop/exit flags, last CPU, allowed CPUs, status, name, account); live programs first |
| 644 | PID | Asks the program to stop; `-13` for another account's program, `-3` when it already ended, `-22` for itself |
| 649 | 4096-byte buffer, 4096 | Activity (kernel 0.0.56): up to 8 disks (bytes read and written since boot, the latest rates), up to 4 network interfaces (bytes received and sent, rates, link flags, IPv4 address, MAC), 120 samples of combined rates (oldest first), and up to 24 windows (owning PID, focused/minimized, title) |

Call 649 lists only the windows of programs the caller may see (as 643 does), plus the desktop's
own windows (PID 0). Disk bytes are counted where Zenith moves blocks: its ext2 driver and raw
`/dev` reads. RAM disks are left out, and FAT volumes (Cosmos's driver) are not counted yet.
Network bytes are whole Ethernet frames. Received frames are counted at Zenith's ingress guard.
Sent frames are counted by a build-time patch that adds a call before every device send in the
Cosmos stack (15 sites in 3.0.89). The stack does not say which adapter carried a frame, so all
traffic is reported on the primary interface. Older kernels refuse the call, and Observatory then
hides the Disk and Network cards and the Apps page.

A sample is one byte per CPU (0..100, 255 offline), the average and memory in
use. Offset 60 holds the newest sample's age in milliseconds (0 before kernel
0.0.46), so a client can glide its graphs in step with sampling (`zui_graph_phase`,
only while animations are on). The kernel samples at most once a second, from the desktop tick or a read,
and keeps two minutes. Layouts are in `userland/include/zenith_monitor.h` and
`src/Core/Monitoring/`. `top` prints the same snapshot in the shell.

## Stages

Each is qualified separately.

1. **O1 monitor service (done locally):** halted-time accounting on every CPU, the
   sampler, calls 642 to 644 and `top`. Host tests cover sampling and records;
   a headless VM shows about 2% busy at idle and the loaded CPU rising under load.
2. **O2 Performance (done locally):** cards for CPU, memory and display with live
   graphs; the selected card fills the main view with two minutes of history,
   per-CPU graphs and figures. The toolkit gains `zui_graph` and `zui_meter`.
   Display history is the client's own: the kernel reports the last second.
3. **O3 Processes (done locally):** a filterable, sortable table (name, PID,
   account, state including Stopping and Exited, last CPU), details for the
   selected program and End process behind a confirmation.
4. **O4 Apps, disk and network (done locally, 0.0.56):**
   - Disk and Network cards: rates with read/write and in/out graphs drawn together, scaled to
     1, 2 or 5 × 10ⁿ B/s, plus totals, IPv4 and MAC addresses.
   - An Apps page: every window grouped under the program that owns it, with PID, account, state
     and CPU, and End app behind the same confirmation as End process.
   - `--probe` prints the activity snapshot for gates.

   Still open from the original O4: **per-program use**, per-program CPU time and
   memory, which need accounting in the process bridge and page allocator; Apps
   groups windows with their programs; disk and network rate cards need counters
   in the block and network drivers.
5. **O5 History and Services:** history kept on disk in a fixed size, with 10 min
   and 1 h ranges, and a Services page once services exist as a model.

Not planned: temperature (no sensor driver), killing a program that ignores its
stop request (cancellation is cooperative throughout Zenith).

## U7/Q2 graphics-fixture handoff

The 0.0.45 test launches Observatory from Polaris. A launch command completing
is not a window-retirement acknowledgement: server teardown took three seconds,
while the tagged helper waited only 1.5s. Current distributor input policy waits
for host admission before one Escape, then observes retirement and the focused
public shell prompt. This works with the unchanged tagged client, without its
newer optional FOCUSED diagnostic. The focused no-build replay and exact source/
binary boundaries are recorded in [the evidence](../archive/2026-11-04/observatory-handoff.md);
the master U7/Q2 entries own server acceptance.
