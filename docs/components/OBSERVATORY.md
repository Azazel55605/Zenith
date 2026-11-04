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

A sample is one byte per CPU (0..100, 255 offline), the average and memory in
use. The kernel samples at most once a second, from the desktop tick or a read,
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
4. **O4 per-program use, Apps, disk and network:** per-program CPU time and
   memory, which need accounting in the process bridge and page allocator; Apps
   groups windows with their programs; disk and network rate cards need counters
   in the block and network drivers.
5. **O5 History and Services:** history kept on disk in a fixed size, with 10 min
   and 1 h ranges, and a Services page once services exist as a model.

Not planned: temperature (no sensor driver), killing a program that ignores its
stop request (cancellation is cooperative throughout Zenith).
