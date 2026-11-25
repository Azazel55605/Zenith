# Networking and transport trust

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md)

Work IDs below are detailed steps of the master tracker, not a separate schedule.

## Implemented boundary

Primary wired E1000E IPv4, DHCP/static/off, persisted hostname/hosts/resolvers,
DNS/ICMP diagnostics, software UTC/NTP and verified bounded HTTP/TLS clients exist.
Owned native TCP/TLS handles and atomic download sinks back independently packaged
net-tools. Kernel 0.0.23/net-tools 0.1.0 and representative home-path checks were
operator confirmed. This is not IPv6/Wi-Fi, a general sockets/server API or secure NTP.

Beacon (U11) shows and changes the connection through call 667: status, traffic and event history, diagnostics,
ping and a safe apply that keeps the previous settings when new ones do not connect; see [Beacon](BEACON.md).

## N1 — Reliability verification

### Update mirror measurements (2026-11-25)

The laptop and a disposable one-vCPU Zenith guest downloaded the same 20,869,871-byte kernel 0.0.79 archive. The Linux client used curl with HTTP/1.0 and TLS 1.2 for the HTTPS comparison. The guest used the built-in downloader and wrote to its installed ext2 root. All HTTPS runs verified the proxy certificate and hostname. The guest files matched the host SHA-256, and the volumes passed an independent ext2 check.

| Median of three transfers, decimal Mbit/s | HTTP direct to nginx | HTTPS through the update proxy |
| --- | ---: | ---: |
| Linux laptop | 1,950 | 477 |
| Original Zenith build, fresh installed root, QEMU TCG/e1000e | 9.8 | 4.6 |
| Updated Zenith build, fresh installed root, QEMU TCG/e1000e | 10.7 | 4.1 |

In the local installed-disk fixture, the original guest sent 15,439 backward acknowledgements among 19,444 pure ACKs. The final guest sent zero among 19,440. It processes the IPv4 send queue oldest-first and reads into the caller’s buffer without copying the whole unread TCP queue.

The mirror runs show no sustained HTTPS throughput gain. A second set with previously used fixture disks gave HTTPS medians of 5.5 Mbit/s before the TCP fixes and 5.6 after. In the local HTTPS trace, 253 of 254 packet gaps over 10 ms ended within 0.1 ms of the guest reopening its receive window. A 32 KiB TLS read experiment nearly halved the number of long gaps but reduced the mirror median to 4.5 Mbit/s in that comparison. The default stays at 8 KiB.

During a separate HTTPS mirror transfer, 151 of 417 sampled guest instruction pointers were in memory-mapped device register access, 79 in the TLS byte-copy routine and 78 in GCM multiplication. The next measurement is to identify the callers of the device register accesses and separate receive, decrypt and file-write time on installed VMware. The figures above time file delivery and commit through the built-in downloader. A package update also verifies the archive and performs a transaction.

For the local four-way comparison, run `python3 -B tools/transfer-bench.py obj/transfer-bench --https --runs 3`. For an installed check, use Polaris's command duration for `fetch --kernel URL FILE` on a fresh destination with sufficient space. Record archive byte count, total seconds and destination filesystem. Calculate Mbit/s as bytes × 8 ÷ seconds ÷ 1,000,000. Time package verification and installation separately. [Benchmark procedure](../DEVELOPMENT.md#toolchain)

- [ ] Record sustained refresh/update/connect/transfer/cancel/replacement results
  through the installed VMware guest and the actual DNS/proxy route.
- [ ] Retain actionable DNS/TCP/TLS phase/error counters and cleanup evidence.
- [ ] Reproduce before assigning a new active TCP defect. The entropy-seeded port
  mitigation is installed; the location of historic stale flow state was not proven.
  A manual nslookup is not a package-client prerequisite.

## N2 / PK2 / H1 — Expansion

Define listener/UDP/server and multiple-device/profile contracts before new ports;
add NICs, IPv6 or Wi-Fi against explicit supported hardware. Certificate renewal and
package-key lifecycle are PK2, separate from transport success. Device failure,
reconnect, framing, cancellation and authorization need native acceptance.

## N3 / N4 — Downloads and file transfer

N3 adds stdout, output files, HEAD, bounded redirects and chunked HTTP bodies to signed `fetch`. Web downloads use embedded public roots plus any configured local CA. Package operations continue using the configured mirror trust. The default file limit is 8 MiB, with an explicit ceiling of 32 MiB + 4096 bytes. Larger disk-aware downloads, installed qualification and any desktop notifications remain open. [Current transport contract](../NETWORKING.md#transfer-and-transport-bounds)

The signed guest command fetched the 200,116-byte dated CA bundle from curl.se before any local CA import. Reading the file back from ext2 matched the published digest. The kernel build passed the 77-check native network suite, 183 network checks and 111 online repository checks. The native suite includes malformed bodies, redirects, failed replacement, cancellation and permission checks. [Native gate](../../tools/native-network-smoke.py) · [Dated public digest](https://curl.se/ca/cacert-2026-11-05.pem.sha256)

SMB is the selected N4 protocol. Start with authenticated share enumeration and file upload/download, including signing, per-user credentials and interrupted-transfer behavior. The later Horizon and mount stages need ownership, reconnect, open-file and unmount rules. [Roadmap](../ROADMAP.md#core-delivery-work)

## Source and evidence

[Services](../../src/Core/Network), [transport](../../src/Core/Network/Transport),
[native client](../../userland/net-tools), [contract](../NETWORKING.md).
[Network extraction/installed evidence](../archive/2026-10-26/native-network-clients.md).
Home DNS/proxy settings are deployment-specific, not public defaults.
