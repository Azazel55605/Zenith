# Networking and trust

[Documentation](../README.md) · [Current status](../STATUS.md)

**State:** M3's bounded IPv4 LAN client is implemented. Operator use includes
VMware DHCP and verified HTTPS package delivery from the home repository.

## Implemented steps

- [x] Configure primary E1000E IPv4 with DHCP, static settings or off mode.
- [x] Persist hostname/hosts/resolver settings and expose diagnostic commands.
- [x] Add bounded DHCP reacquisition, cancellation and receive polling fallback.
- [x] Implement DNS, correlated ICMP/TCP diagnostics and shared software UTC/NTP.
- [x] Stream bounded HTTP/HTTPS downloads with backpressure and progress reporting.
- [x] Validate TLS chain, hostname and dates through imported trust; retain separate
  package signature verification.
- [x] Bootstrap trusted proxy material using the configured delivery workflow.

## Current boundary

The private endpoint is `https://zenith-stable.net.local`, backed by
`10.0.20.7`; home DNS is `10.0.10.3`. These are deployment settings, not universal
network defaults. Zoraxy serves the explicitly trusted self-signed certificate;
there is no insecure TLS bypass. NTP is unauthenticated and does not establish a
secure time source. Fetch success alone does not authorize package installation.

Primary-NIC client support does not provide a general sockets ABI, SSH, IPv6,
Wi-Fi or broad real-hardware support. `nc` is a bounded probe rather than a full
interactive stream tool. Performance depends on the VM/VPN/storage path; do not
assume KVM or extra vCPUs improve the guest's single managed execution path.

## Active reliability work

Operator refresh/update still shows intermittent TCP connect failures despite
working DNS after the retry/retirement release. On 2026-10-17 a full refresh
succeeded, then update refreshed core generation 24 before extra exhausted
connect attempts to the correctly resolved proxy 10.0.10.3:443. Bounded fresh-socket
retry and timed-out registration retirement have local guest acceptance, but do
not establish reliable operation through the actual VMware/proxy path. A new
capture reproduces SYN -> stale plain ACK -> appropriate RST; later success after
waiting suggests expiring flow state, with its location unconfirmed. Pinned source
ports restart at 49152 each boot. See [evidence](../archive/2026-10-17/tcp-stale-flow.md)
and [Progress](../PROGRESS.md); a manual nslookup is not a client prerequisite.

Candidate `db90ebc` replaces fixed port candidates with a once-only hardware-entropy
seeded offset/stride, retaining active-port exclusion. Full clean-source release
acceptance and focused TCP/HTTPS wire checks passed. Proposed kernel 0.0.17 is not
yet published or operator-tested; this mitigation does not locate external stale
state or establish that every continuous-operation failure is resolved.

## Remaining steps

- [ ] Expose permission-controlled sockets to independent native tools.
- [ ] Expand stream/timeout diagnostics and benchmark a repeatable transfer profile.
- [ ] Define certificate renewal and package-key lifecycle operational procedures.
- [ ] Extend NIC/protocol/hardware coverage with explicit acceptance targets.

## Source and acceptance

Owners: [network service](../../src/Core/Network), [network library](../../src/Core/Network/Transport),
[TLS builder](../../tools/build-tls.py). Exact limits/commands/framework adaptations:
[network reference](../NETWORKING.md).

Use [network smoke](../../tools/network-smoke.py),
[DHCP receive smoke](../../tools/dhcp-receive-smoke.py) and
[repository smoke](../../tools/repository-smoke.py) as applicable. Check missing NIC,
link loss, rejected offers, DNS correlation, cancellation, malformed/partial
transfers, TLS failure and complete signed delivery. The
[archive](../archive/2026-10-15/PROGRESS.md) distinguishes QEMU results from operator
VMware/home-server observations.
