# Active work

Updated **2026-10-16**. [Status](STATUS.md) summarizes delivery; [Roadmap](ROADMAP.md)
orders future work. Completed evidence belongs in the [archive](archive/README.md).

## Current position: TCP release handed off; operator improvement reported

Kernel/v0.0.15 was pushed at tested source `f6bf893`. Full local release acceptance
passed, including all four native gates and the focused TCP/HTTPS regression.
Following the handoff, the operator reports that the intermittent connection issue
"seems to be gone" on the VMware setup. This is operator-observed improvement;
individual server delivery checks, exact installed version and `boot confirm`
were not separately supplied in that report.

- [x] Implement failed connection retirement, bounded fresh-socket retry and
  destination/repository diagnostics.
- [x] Complete clean-source release validation and push the 0.0.15 tag.
- [x] Record the operator's real-path improvement report.

[Detailed TCP evidence and release validation](archive/2026-10-16/tcp-connect-reliability.md).
The initial unrelated ACK is consistent with stale flow state; the originating
network device remains unproven. The earlier gateway DNS-cache issue was separate.

## Next agreed slice: native input and writable file handles

Resume [userland](components/USERLAND.md) after this reliability interruption.
Define the bounded descriptor/access/cancellation contract, implement controlled
input/writes and cleanup, and package a utility that writes persistent data without
a kernel rebuild for executable-only changes. Implementation has not started.
