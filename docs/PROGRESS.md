# Active work

Updated **2026-10-16**. [Status](STATUS.md) summarizes delivery; [Roadmap](ROADMAP.md)
orders future work. Completed evidence belongs in the [archive](archive/README.md).

## Current slice: intermittent package TCP connections

The operator reports 0.0.14 installed. The Cloud Gateway Max's stale negative DNS
cache was cleared; hostname resolution works again. A new screenshot shows TCP
connect failures across refresh/update, including core generation 17 succeeding
before extra fails. This is not specific to the combined update command.

- [x] Locate pinned Cosmos connect: one SYN, five-second wait, generic exception
  bypassing fetch's IOException retry. CloseTcp also leaves SYN_SENT registrations.
- [x] Adapt timeout retirement in signature-checked build copies; add fresh-socket
  bounded retry, cancellation/cleanup and destination diagnostics.
- [x] Pass four host connector tests and three real-dependency adaptation tests
  (including byte-idempotence and changed-contract refusal). Release ISO builds.
- [x] Pass disposable TCG guest failed-connect/recovery and 12 sequential verified
  HTTPS fetches; full host suite 628/628. Failed endpoint diagnostic was observed.
- [x] Pass 92/92 installed repository checks in 317.023 s on the rebuilt candidate,
  including combined update, real kernel activation/reboot and independent ext2 audit.
- [ ] Complete the full common release gate plan before any new kernel tag.
- [x] Read operator vmnet8 capture: SYN receives bare ACK with an unrelated ACK
  number, then Zenith sends the appropriate RST. Stale flow state is a hypothesis;
  guest-side capture does not identify the replying device.
- [ ] Verify the patched kernel over the actual VMware/proxy path. A fresh-port
  retry addresses the observed pattern; installed acceptance is still pending.

Previous delivery/dashboard evidence: [delivery record](archive/2026-10-16/distributor-delivery-diagnostics.md).
Installed 0.0.14 is operator-confirmed; boot confirmation and dashboard deployment
are not separately confirmed. After network reliability, resume bounded native
input/writable file handles in [userland](components/USERLAND.md).
