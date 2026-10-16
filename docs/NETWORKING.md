# M3 IPv4 networking

> Technical/operating reference for the bounded IPv4 client. Work steps: [Networking](components/NETWORKING.md).

## On this page

- [Test on your network](#test-on-your-network)
- [Commands and saved configuration](#commands-and-saved-configuration)
- [Clock and certificate trust](#clock-and-certificate-trust)
- [Transfer and transport bounds](#transfer-and-transport-bounds)
- [Build and validation](#build-and-validation)

Zenith provides one primary IPv4 uplink, DHCP/static/off configuration, DNS/hosts
lookup, ICMP and TCP diagnostics, optional NTP, and bounded HTTP/verified HTTPS
file downloads. Both the test DE and headless shell use this service. This is a
bounded LAN client profile; Internet-wide protocol hardening and package installation
are separate work. Home DNS is **10.0.10.3**; the package VM is not provisioned yet.

## Test on your network

Build the ISO, then boot with explicit e1000e networking:

```sh
dotnet publish -c Release -r linux-x64 -o output-x64
NETWORK=user tools/run.sh live
# COM1 shell, without a graphical window:
UI=headless NETWORK=user tools/run.sh live
# After installing and adding EFI files:
NETWORK=user tools/run.sh installed
```

The Cosmos VS Code Run button reads `.cosmos/config.json`, which selects `e1000e`
for this project. Without that setting the extension defaults to no NIC. For the
bare Cosmos CLI, select the model explicitly (the CLI does not read this file):

```sh
cosmos run -a x64 --nic e1000e
```

The default user network gives the guest a private QEMU address, typically
10.0.2.15, gateway 10.0.2.2 and DNS proxy 10.0.2.3. It can make outbound connections
to your LAN through the host. It does not give Zenith its own address on your
10.0.10.0 network or expose inbound services. The host must be able to reach the
LAN; for remote testing, connect the **host** to your existing VPN first.

In Zenith, wait for `ip` to show `state=online`, then run as root:

```sh
ip
netcfg dns 10.0.10.3
nslookup example.com
ping 10.0.10.3
# Restore DHCP-provided DNS later:
netcfg dns auto
```

A DNS answer verifies the DNS/UDP routing path. A ping timeout can also mean ICMP
is filtered; test the relevant TCP service with `nc -z HOST PORT`. If a local name
fails, confirm that exact name resolves on your host through 10.0.10.3. Resolver
search suffixes are not implemented: use the complete DNS name. No package-server
address or service is assumed while the VM is absent.

For a guest with its own LAN address, create/authorize a TAP interface and attach
it to your host's existing bridge using your normal network configuration. The
launcher consumes it without configuring bridges, changing host addresses or
running network scripts:

```sh
NETWORK=tap TAP_IFACE=zenith0 tools/run.sh live
```

DHCP then comes from your LAN. Alternatively set a **reserved, unused** address,
its mask and gateway with `netcfg static IP MASK GATEWAY 10.0.10.3`; do not reuse
the QEMU NAT example address on your LAN. `NETWORK=off` removes the adapter.
`UI=desktop` is the default; `UI=headless` uses the no-GPU serial fallback.
`DISK`, `MEM`, `QEMU`, `OVMF_CODE` and `OVMF_VARS` select local launcher resources.
No host-network privileges are needed for user networking.

## Commands and saved configuration

```sh
ip [addr|route]
ifconfig
netcfg status
netcfg dhcp
netcfg static 10.0.2.15 255.255.255.0 10.0.2.2 10.0.2.3
netcfg off
netcfg apply
netcfg dns auto
netcfg dns IP [IP [IP]]
nslookup HOST [SERVER [PORT]]
ping HOST
nc -z HOST PORT
netcfg ntp HOST [PORT]
netcfg ntp off
ntp HOST [PORT]
tls selftest
tls trust FILE SHA256
fetch [--replace] http://HOST[:PORT]/PATH FILE
fetch [--replace] https://HOST[:PORT]/PATH FILE
```

Configuration writes, NTP and trust import require root; diagnostics and downloads
respect the caller's filesystem permissions. `nc` supports bounded connection probes.
Interactive streams, listeners and remote login are later work.

`/etc/zenith/network.conf` stores version 1 mode/address/mask/gateway/DNS and optional
boot NTP host/port. Invalid and future files are preserved and stop automatic setup.
A deliberate root `netcfg dhcp/static/off` save replaces the configuration.
`netcfg apply` validates and reloads hostname/configuration/hosts/resolvers from disk
before changing active settings. `/etc/hostname` supplies the service and new shells;
existing shell environment variables are not rewritten. `/etc/hosts` is read on lookup.
`netcfg dns` atomically saves `/etc/resolv.conf` and reapplies the uplink. Up to three
unicast IPv4 `nameserver` lines override DHCP/static DNS; `dns auto` clears overrides.
Reads require bounded regular UTF-8 files: hostname 256, configuration 4096,
hosts/resolver 16384 bytes. Installed saves survive clean reboot; live saves are volatile.

DHCP validates BOOTP type, MAC, transaction, cookie, TLVs, selected server/address,
mask/gateway and lease. Two two-second attempts per phase precede an offline result;
failed DHCP retries after thirty seconds. At half lease (capped at a day), discovery
reacquires and briefly clears the address. Expired leases and absent/down adapters
reject traffic. Operations serialize client registrations and report busy to a second
caller. Seamless unicast T1/T2 renewal, multiple NICs, IPv6, DHCP option overload and
physical link-loss validation are outside this profile.

Names are ASCII DNS names. One A answer is returned, following up to sixteen
in-message CNAME links. Explicit DNS server queries bypass hosts. Replies must match
endpoint, transaction, question and record shape. No public resolver fallback, IDN,
search domains, TCP DNS or DNSSEC exists. Three configured resolvers are tried in order;
there are two two-second attempts per server. Use `netcfg apply` to retry a failed boot.

## Clock and certificate trust

There is no default public NTP server. `netcfg ntp HOST [PORT]` saves a subsequent
boot target; `ntp HOST [PORT]` synchronizes immediately. Replies match endpoint and
a hardware-random originate token, synchronized server mode/stratum and 2020..2100.
Transmit time is used without four-timestamp discipline. A shared adjustment at the
Cosmos raw clock seam feeds DateTime, DateTimeOffset, filesystem timestamps, date
and timezone conversion. It does not write the RTC or persist across reboot.

**NTP is unauthenticated.** It does not establish a trusted time floor, provide NTS,
or prove package metadata freshness. TLS checks certificate dates against current
system UTC. Ensure the boot RTC or your chosen time service supplies a reasonable
time; an invalid clock causes TLS initialization/date verification to fail.

No public or private CA is trusted by default. To use your future VM's HTTPS service,
export its issuing CA's ASCII PEM file and obtain the **SHA256 of the exact PEM file**
through a trusted channel. This hash is not the DER certificate fingerprint reported
by `openssl x509 -fingerprint`. On the host/VM use `sha256sum ca.pem`.
Copy the file through a local volume or download it over HTTP, then:

```sh
tls trust /tmp/ca.pem EXPECTED_64_HEX_DIGIT_PEM_FILE_HASH
fetch https://YOUR_FULL_VM_DNS_NAME/test.txt /tmp/test.txt
```

For a single download-and-import operation, root can instead run:

```sh
tls bootstrap http://YOUR_VM_ADDRESS/tls.pem EXPECTED_64_HEX_DIGIT_PEM_FILE_HASH
```

Bootstrap downloads at most 4096 bytes, checks the independently supplied exact
PEM SHA256 and validates the bundle before persistent replacement. Temporary files
are removed on success or failure. HTTP is permitted for this fingerprint-checked
bootstrap; HTTPS sources require existing TLS trust. Failed download, cancellation,
bad hash or invalid PEM preserves existing trust. This replaces the current bundle;
it does not silently accept a server certificate or change package-signing trust.

Import validates the hash and complete PEM bundle before staged replacement of
`/etc/zenith/tls-ca.pem` (maximum 4096 bytes). Malformed files and wrong hashes preserve
old trust. HTTPS verifies CA chain, DNS name or IPv4 SAN, key/algorithm policy and
**every certificate's validity dates**. There is no insecure bypass. The server must
support TLS 1.2 ECDHE-RSA or ECDHE-ECDSA with AES-128/256-GCM and P-256; RSA keys
must meet the library's 2048-bit minimum. TLS 1.3, client certificates, revocation
retrieval, public CA bundles, tickets/resumption and certificate pin rotation are
not implemented. IP URLs require a matching IP SAN, not just a DNS certificate.

## Transfer and transport bounds

Fetch sends HTTP/1.0 with identity encoding and close semantics. Only status 200
with exactly one numeric Content-Length is accepted. Redirects, chunking,
compression, credentials and fragments are rejected. URLs are printable ASCII
and at most 350 characters, headers 8192 bytes, payloads 8 MiB by default. Explicit `fetch --kernel` permits 32 MiB + 4096 bytes
and a 15-minute total deadline for signed kernel containers (normal total deadline:
30 seconds). Kernel downloads use a 30-second idle deadline; ordinary downloads
retain 3 seconds. HTTP timeout diagnostics identify idle versus total expiry and
include received/expected byte counts. It streams into a
same-directory private 0600 temporary file, reports progress per 64 KiB, and
flushes/renames/syncs after all declared bytes. Existing files require `--replace`
and must be regular files. Errors and cancellation remove staging and preserve the
old destination. This is not an exclusive namespace transaction against concurrent
trusted kernel writers.

Two attempts maximum; each uses the above idle/body deadlines,
plus DNS/connect/handshake time. Each TCP connection uses at most two fresh sockets,
with a 250 ms pause after the first pinned connect failure. Each Cosmos connect
blocks up to five seconds and observes cancellation on return. Failed sockets are
closed before retry; cancellation and unrelated errors are never connection-retried.
The final diagnostic includes hostname, port and resolved IPv4 address. A fetch may
therefore make at most four connection attempts across its two transfer attempts. TLS handshake/write have fifteen-second deadlines;
TLS body reads honor the selected idle deadline, capped by the remaining transfer
budget. Ordinary TLS reads default to three seconds. Fetch drains productive reads
without a sleep, then uses a one-millisecond wait when no data progresses; effective
wakeup granularity depends on the scheduler.
There is a fixed 1 MiB TLS arena and two 32 KiB encrypted queues. CPU/hypervisor
RDSEED, retried at most 256 times per word, seeds Mbed TLS CTR-DRBG; its reseeds also
require RDSEED. No timer, framework insecure RNG or predictable fallback is used.

[Version-pinned build adapters](../src/Core/Network/Transport/SOURCE.md) validate
Ethernet/IPv4 lengths/options/checksums before framework parsing, reject fragments/
IPv6/unsupported frames, cap each UDP/ICMP queue at 32 packets, and cap pending TCP
at 128 KiB. They bypass unsafe legacy DHCP parsing and unused TCP option allocation.
The receive window advertises up to 32,768 bytes without scale negotiation and
shrinks/reopens with unread data. Immutable 4 KiB chunks reduce per-packet copies;
held read snapshots remain stable. High-volume successful IPv4/TCP serial tracing
is disabled in adapted build copies; errors and service counters remain available.
Limited IPv4 broadcasts also receive the Ethernet broadcast destination; a VMware
capture revealed that the released Cosmos enqueue path otherwise sent DHCP to a
zero MAC despite using 255.255.255.255 at the IP layer. The native suite checks
actual Discover/Request Ethernet headers in both live and installed sessions.
The E1000E receive address is explicitly activated even if firmware populated its
MAC registers. `ip` includes receive-frame/ingress counters, initial filter validity
and DHCP reply/rejection counts with the last rejected-reply reason. A DHCP timeout
with no replies differs from received offers rejected by the bounded codec.
`receive-filter-initially-valid=false` records the state before the driver activates
the filter; it does not mean the filter remains disabled. Compare `rx-irqs`,
`rx-frames` and `accepted` before/after a retry against a host packet capture.
`dhcp-replies` and rejection details reset for each DHCP acquisition.
The timer-driven kernel loop also polls the E1000E receive ring with interrupts
masked, restoring the previous mask explicitly on success or failure. Both IRQ
and poll drains process at most one 32-descriptor ring per call. This supplies a
receive path when platform NIC interrupts are not delivered. `rx-polls` counts
poll calls; `polled-frames` counts frames delivered by that path. The VMware
screenshot showed a valid filter, zero NIC interrupts and zero received frames;
the precise platform interrupt-routing cause remains unverified.
Cosmos protocol state, retransmission/reassembly, ARP caching, connection retirement
and shared tables still have limits; these bounds are not a whole-kernel budget or
an Internet-facing server assurance. Native cleanup explicitly catches/disposes in
owning frames because Cosmos 3.0.89 skips unwind-finally and misselects typed catches.

HTTPS authenticates the transport to a trusted server. It does **not** authorize
package installation: signed metadata/artifacts, rollback/expiry policy and the
transaction executor remain package milestones. P0 is still inspect/plan only.

## Build and validation

[Mbed TLS 3.6.7](https://github.com/Mbed-TLS/mbedtls/releases/tag/mbedtls-3.6.7) is
pinned as an offline source archive with a checked SHA256 and Apache-2.0 attribution.
The [port record](../vendor/mbedtls/SOURCE.md) documents source, configuration and ABI.
No SslStream/framework crypto plug is used. `tls selftest` runs native AES/GCM/SHA/
CTR-DRBG known-answer tests and exercises malformed ingress at the patched entry.

```sh
python3 -B tools/build-tls.py /tmp/zenith-tls-host --host
python3 -B tools/test_tls_bridge.py /tmp/zenith-tls-host/libzenith-tls.so
python3 -B tools/test_run_network.py
python3 -B tools/network-smoke.py /tmp/zenith-network
# Explicit real home DNS acceptance; CI never uses private LAN addresses:
python3 -B tools/network-smoke.py /tmp/zenith-network-lan --lan-dns 10.0.10.3
```

The native suite uses disposable loopback HTTP/HTTPS/DNS/NTP servers and e1000e
QEMU/slirp guests. It covers faults, certificate/entropy rejection, cancellation,
link loss, saved configuration/trust, users, clean disk-only boots, exact downloaded
bytes and independently checked ext2 timestamps/fsck. Existing DE/headless suites
remain regressions. No real disk or upstream Cosmos checkout is modified.
Home VM/VPN acceptance waits for the VM and its name/address; see
[PACKAGE-SERVER](PACKAGE-SERVER.md). A guest VPN client is not implied.

Interactive fetch progress updates one terminal line with a byte percentage/bar
and MiB counts, capped at five numeric updates per second. Redirected stdout omits
progress and retains the final result. Errors/cancellation end the progress line.
The home proxy now has hostname zenith-stable.net.local; its inspected Zoraxy default
certificate has no matching DNS name. See [PACKAGE-SERVER](PACKAGE-SERVER.md) for
private certificate trust setup. Trusting a certificate does not disable name checks.
