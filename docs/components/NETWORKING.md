# Networking and transport trust

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md)

Work IDs below are detailed steps of the master tracker, not a separate schedule.

## Implemented boundary

Primary wired E1000E IPv4, DHCP/static/off, persisted hostname/hosts/resolvers,
DNS/ICMP diagnostics, software UTC/NTP and verified bounded HTTP/TLS clients exist.
Owned native TCP/TLS handles and atomic download sinks back independently packaged
net-tools. Kernel 0.0.23/net-tools 0.1.0 and representative home-path checks were
operator confirmed. This is not IPv6/Wi-Fi, a general sockets/server API or secure NTP.

## N1 — Reliability verification

- [ ] Record sustained refresh/update/connect/transfer/cancel/replacement results
  through VMware and the actual DNS/proxy route; add a repeatable throughput profile.
- [ ] Retain actionable DNS/TCP/TLS phase/error counters and cleanup evidence.
- [ ] Reproduce before assigning a new active TCP defect. The entropy-seeded port
  mitigation is installed; the location of historic stale flow state was not proven.
  A manual nslookup is not a package-client prerequisite.

## N2 / PK2 / H1 — Expansion

Define listener/UDP/server and multiple-device/profile contracts before new ports;
add NICs, IPv6 or Wi-Fi against explicit supported hardware. Certificate renewal and
package-key lifecycle are PK2, separate from transport success. Device failure,
reconnect, framing, cancellation and authorization need native acceptance.

## Source and evidence

[Services](../../src/Core/Network), [transport](../../src/Core/Network/Transport),
[native client](../../userland/net-tools), [contract](../NETWORKING.md).
[Network extraction/installed evidence](../archive/2026-10-26/native-network-clients.md).
Home DNS/proxy settings are deployment-specific, not public defaults.
