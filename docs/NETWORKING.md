# M3 IPv4 network lab

Zenith now owns a shared network service used by both the test DE and COM1 headless
shell. This is a single IPv4 uplink over Cosmos 3.0.89, not a production network security
boundary. See [PROGRESS](PROGRESS.md) for actual native validation and remaining gates.

## Using it

Boot with a supported NIC. The service configures the primary adapter asynchronously;
the desktop/serial session starts even when DHCP fails. `ip`/`ifconfig` report adapter,
MAC, physical link indication, address, mask, gateway, DNS, operation state and clock
status. `ip addr` and `ip route` currently print the same combined diagnostic report.

```sh
ip
nslookup mirror.home
nslookup mirror.home 10.0.2.2 5353
ping 10.0.2.2
nc -z 10.0.2.2 8080
fetch http://10.0.2.2:8080/test.bin /tmp/test.bin
```

`ping` sends one identifier/sequence-correlated echo with a three-second deadline.
`nc` currently probes a TCP connection; interactive streams/listeners are later work.
`nslookup` resolves one IPv4 A answer, following up to sixteen in-message CNAME links.
An explicit server bypasses hosts-file lookup; its optional port is for lab servers.
Names are ASCII DNS names, not internationalized names. IPv6/search domains/TCP DNS/
DNSSEC are not implemented. NXDOMAIN, malformed, truncated and unmatched DNS replies
produce an error; there is no fallback public DNS server.

## Configuration and lifecycle

Root controls the uplink through the same service:

```sh
netcfg dhcp
netcfg static 10.0.2.15 255.255.255.0 10.0.2.2 10.0.2.3
netcfg off
netcfg apply
netcfg ntp time.home
netcfg ntp off
ntp time.home
```

The versioned `/etc/zenith/network.conf` stores mode, IPv4 address/mask/gateway/DNS
and optional boot NTP host/port. Invalid values are rejected before saving. Unknown
schema/keys are preserved and disable automatic network initialization; a deliberate
root `netcfg dhcp/static/off` save replaces that file. `netcfg apply` applies the loaded
configuration and rereads resolver overrides; reboot reloads edited network.conf.
Live changes are volatile; installed ext2 saves survive reboot. Settings are staged,
flushed, renamed and synced through the existing settings store.

`/etc/hostname` supplies the service and new shell hostname; `/etc/hosts` is read on
lookup; `/etc/resolv.conf` supports up to three `nameserver IPv4` overrides. An empty
resolver file selects DHCP/static DNS. DHCP nameservers remain runtime state and do
not overwrite local administrator files. These reads require bounded regular UTF-8
files (hostname 256, configuration 4096, hosts/resolver 16384 bytes).

DHCP checks BOOTP type, transaction, MAC, cookie, bounded TLVs, ACK type, selected
server/address, mask, gateway and lease duration. It does two two-second attempts per
phase, then reports offline; failed DHCP is retried after thirty seconds. Halfway through
the lease (capped at a day), the service reacquires with discovery; it does not implement
seamless unicast T1/T2 renewal, multiple uplinks or DHCP option overload. Reacquisition
briefly clears the old address. Expired leases and down/missing adapters reject traffic.
Operations serialize the framework's shared client registrations; a concurrent command
gets a busy error. Cooperative cancellation checks run during UDP/transfer polling.
QEMU link changes are tested through QMP; physical unplug/replug and multi-NIC behavior
still require hardware validation.

## Time

There is deliberately no default public time server. A configured `netcfg ntp HOST
[PORT]` target is used on subsequent boot; `ntp HOST [PORT]` synchronizes immediately.
Replies must match endpoint and a hardware-random originate token, have server mode,
supported version, synchronized leap state/stratum, and a time in 2020..2100. The
software UTC correction feeds `date` and shared timezone conversion. Existing BCL/
Cosmos and ext2 filesystem timestamp sources still use the RTC; unifying those clock
consumers remains work before production TLS/updates. It does not write the RTC,
persist a trusted time floor or implement NTP discipline.
The response transmit time is used without a four-timestamp offset calculation.

Status explicitly calls NTP **unauthenticated**. Origin matching resists unrelated
replies; it does not authenticate a time server, establish certificate validity or prove
package metadata freshness. This is not NTS.

## HTTP lab downloads and bounds

`fetch [--replace] http://HOST[:PORT]/PATH FILE` requests HTTP/1.0 with identity encoding
and close semantics. Only status 200 and exactly one numeric Content-Length are accepted.
Redirects, chunking, compression, credentials, fragments and HTTPS are rejected. URLs
are printable ASCII and at most 350 characters; headers at most 8192 bytes; payloads
at most 8 MiB. The decoder streams to a same-directory private temporary file, emits
progress per 64 KiB and flushes/renames/syncs only after receiving the declared bytes.
The default refuses existing names; `--replace` permits regular files. New downloads
retain private mode 0600. VFS caller permissions apply, including parent-directory and
sticky-directory checks. Failed/cancelled transfers clean staging and preserve the
previous destination. As with ordinary rename, this is not an exclusive namespace
transaction against other trusted kernel threads concurrently editing that directory.

There are at most two attempts, a three-second idle and thirty-second body deadline
per attempt, plus DNS/connect time. DNS has two two-second attempts per server; TCP
connect uses Cosmos's five-second blocking connect and observes cancellation on return.
Transfer loops check cancellation every ten milliseconds of idle polling. Socket close
requests zero wait. A local version-pinned [TCP receive adaptation](../src/Core/Network/Transport/SOURCE.md)
uses immutable snapshots/CAS and caps pending TCP data at 128 KiB per connection.
Overflow closes the connection and rejects the transfer. The application also caps
its receive staging and drains currently available data.

These bounds constrain Zenith's decoder/file output and adapted TCP storage, not all
allocations made earlier by Cosmos's packet/UDP queues. Upstream TCP sequence/window
handling, reassembly and packet/client table concurrency need hardening before untrusted
Internet use.
Cosmos's native exception dispatcher skips unwind `finally` handlers: the service
explicitly catches/disposes resources in their owning frame before propagating errors.
Typed catch selection also misclassified cancellation in native tests; the adapter uses
one catch-all with explicit classification and invokes retry callbacks outside handlers.
Native rejection/cancellation tests must stay in CI; host tests cannot reproduce this.

Plain HTTP, DHCP and DNS have no authentication. Downloads are **not installable
packages**, and fetching bytes does not authorize `pkg install/update`. P0 remains
inspect/plan only. See [package trust design](PACKAGE-MANAGER.md).

## Native validation and home infrastructure

`python3 -B tools/network-smoke.py /tmp/zenith-network` starts ephemeral loopback
HTTP/DNS/NTP servers and disposable no-GPU QEMU/slirp guests with an explicit e1000e.
It exercises boot address/routes/DNS, correlated replies, clock correction, TCP probes,
streaming/progress, retry, oversize/header/encoding/truncation/stall faults, Ctrl+C,
staging cleanup, static/off configuration, installed saves/boot NTP, persistent downloads
QMP unplug/replug, account denial/recovery, and a disk-only boot without a NIC.
Installed reboots use clean shutdown; independent e2fsck and debugfs check the final
root and exact payload bytes. It needs QEMU, OVMF, mtools and local socket access;
it does not depend on public DNS/NTP/HTTP servers. Existing DE/headless suites remain
regressions. No real disk or upstream Cosmos checkout is modified.

The user's package VM has its own IP and will use existing home DNS/VPN reachability.
No address, hostname, subnet or VPN route has been supplied, so those routes cannot be
verified yet. Provisioning/guest access tests belong in [PACKAGE-SERVER](PACKAGE-SERVER.md).
A guest VPN client is not implied. Replace the lab URL with that VM only after transport
and package authentication gates are met.

## TLS feasibility gate

The pinned Cosmos source has no SslStream/TLS plug and documents raw TCP without HTTPS.
Its `InteropSysPlug.GetCryptographicallySecureRandomBytes` currently calls the same
non-cryptographic generator as its nonsecure counterpart. Zenith's RDSEED source is
used for protocol correlation; it must be wired explicitly into any future TLS port,
with no timer/PRNG fallback. Existing security KATs do not certify a TLS implementation.

[Mbed TLS](https://github.com/Mbed-TLS/mbedtls) is a candidate, not a selected dependency.
Its [official porting guide](https://mbed-tls.readthedocs.io/en/latest/kb/how-to/how-do-i-port-mbed-tls-to-a-new-environment-OS/)
describes replaceable network, timing and entropy callbacks and bare-metal builds.
Inference for Zenith: a freestanding native bridge could use bounded send/receive,
allocator and RDSEED callbacks, but needs toolchain/C-library integration and managed/
native ABI validation. The guide warns that disabling absolute time skips certificate
validity checks; we must retain and test chain, hostname and validity checks with a
trustworthy clock policy. No library was downloaded, linked, or advertised as working.

Before HTTPS: select/pin a maintained dependency and license, establish bounded TCP
and safe unwind cleanup, build the freestanding bridge, run native crypto/entropy KATs
including missing-RDSEED failure, verify good/bad chains, hostname, expiry and boot clock,
and test handshake/record bounds, cancellation and cleanup. Then add a private lab CA
and authenticated mirror test. TLS implementation and actual home DNS/VPN verification
remain open M3 work; the networking lab is not the production update transport.
