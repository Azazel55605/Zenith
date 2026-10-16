# Active work

Updated **2026-10-16**. [Status](STATUS.md) summarizes delivery; [Roadmap](ROADMAP.md)
orders future work. Completed evidence belongs in the [archive](archive/README.md).

## Current slice: 0.0.15 delivery and installed TCP acceptance

0.0.14 is operator-installed, but package refresh intermittently fails TCP setup.
The vmnet8 capture shows a SYN receiving an unrelated bare ACK, followed by the
appropriate reset. Stale NAT/proxy flow state is a hypothesis, not a proven device
fault. The gateway DNS-cache issue was resolved separately.

- [x] Implement explicit timed-out connection retirement and bounded fresh-socket
  retry, with destination-specific errors and repository refresh labels.
- [x] Validate exact source `f6bf893`: 109 controller tests, 628 Release host tests,
  all four common TCG native gates (69 login, 92 repository, native programs,
  169 kernel recovery checks), final artifact identity and clean source checks.
- [x] Pass nine real-dependency adapter regressions and the targeted guest failed
  connection/recovery plus 12 sequential verified HTTPS fetches on the rebuilt ISO.
- [x] Prepare kernel/v0.0.15 at the exact tested code commit for distributor handoff.
- [ ] Confirm server build/sign/publication and final HTTPS delivery verification.
- [ ] Install/reboot/confirm 0.0.15, then repeat refresh/update over the actual
  VMware/proxy path. Local QEMU proof does not establish this final acceptance.

[Detailed TCP evidence and release validation](archive/2026-10-16/tcp-connect-reliability.md).
The running 0.0.14 kernel does not yet contain the patch. Dashboard deployment and
explicit 0.0.14 boot confirmation are not separately reported.

After installed networking acceptance, resume bounded native input/writable file
handles in [userland](components/USERLAND.md).
