# Owned native network clients

## Scope and boundaries

The operator accepted the installed text/traversal checklist. This slice extracts
ip, ifconfig, nslookup, ping, nc -z and fetch into the independently signed
zenith-net-tools package. Native code owns argument/URL parsing, HTTP request and
response handling, output and download orchestration. The kernel supplies typed
owned services, not a shell-command execution callback.

Drivers, shared DNS/ICMP policy, certificate/clock/entropy checks and atomic VFS
commit remain trusted services. Configuration, TLS trust import, NTP clock changes,
package/account administration and shell evaluation/xargs remain trusted frontends.
No UDP/listen/server, arbitrary raw packet, POSIX socket or insecure-TLS interface
is added. This is the selected bounded CLI client profile, not general OS API
compatibility or complete desktop/service migration.

## Ownership and service contract

Calls 500..503 are x64 managed-service yields, retaining caller credentials and
full-range user-memory checks before side effects. Handles live in one invocation,
are not PIDs or host descriptors, and are never reused within it. Another invocation
cannot access a handle even if it guesses its numeric value.

| Call | Contract |
|---|---|
| 500 | Pointer to an exact 80-byte little-endian request, size 80, reserved arg 0 |
| 501 | Owned connection handle, writable user buffer, byte capacity up to 65536 |
| 502 | Owned connection/sink handle, readable user buffer, bytes up to 65536 |
| 503 | Owned handle, zero, zero; connection close or uncommitted sink abort |

Request: operation u32, flags u32, then input pointer/length, target pointer/length,
output pointer/capacity, value, extra, reserved (all u64). Unused fields must be
zero. Ops 1 status, 2 resolve, 3 correlated echo, 4 TCP/verified-TLS connect,
5 begin download sink, 6 last error, 7 commit sink. Flag bit 0 selects TLS for
connect or regular-file replacement for a sink. Resolve/connect value is port;
download value is the byte bound; commit value is the sink handle and extra is
its expected length. Strings are explicit UTF-8 without NUL, hosts up to 253 bytes,
paths up to 1024; resolve's optional target is an explicit IPv4 DNS server.
Snapshot/resolve/error output is non-NUL text with a returned byte count, capacity
at most 4096. All complete input/target/output ranges are validated first.

An invocation permits one active connection and one sink, up to two connect
requests (each retains the shared handshake retry) and two sinks total, 32 metadata operations and 32768 network requests;
ordinary native requests retain their separate 4096 budget. Sent request bytes
are capped at 64 KiB; received bytes at twice (32 MiB + 4096 + 8192), written sink
bytes at twice (32 MiB + 4096). Each sink independently enforces its declared bound
and exact commit length. Network I/O does not relax the ordinary file-write/stream
1 MiB profile. Context/connection lifetime checks use 900 seconds; reads are
cancellable and bounded to 30 seconds, alongside existing connect/TLS limits.
These are cooperative framework limits, not a hard real-time guarantee.

A connection holds the existing serialized network-operation lease until close.
Other clients/configuration are refused busy. Peer reads park without holding the
native filesystem service gate; setup/mutations keep existing serialization.
Exit, fault, execution timeout and cancellation retire connections and abort sinks.
A sink creates a private random sibling under captured caller credentials, flushes,
rechecks replacement policy and renames only on explicit commit. Cleanup re-enters
those credentials; it never borrows root after a user invocation ends. Existing
VFS ancestor/search/ownership policy remains authoritative.

Package-owned normal command dispatch refuses present invalid/unowned/tampered
executables; absent packages use recovery builtins. `recovery fetch` etc. remain
explicitly available. New bundles declare zenith-native-network-abi 1.0 so older
kernels refuse installation.

## Native HTTP profile

URLs are bounded printable ASCII HTTP/HTTPS, with bounded DNS/IPv4 host, valid port,
no credentials, fragments, IPv6 literals, spaces, backslash or header injection.
HTTPS opens only the verified TLS 1.2 broker connection using installed trust and
hostname/date checks. No certificate bypass or HTTP downgrade exists.

Responses require HTTP/1.0 or 1.1 status 200, an 8 KiB bounded header, exactly one
numeric Content-Length and a complete body. Reject redirects/error statuses,
folded/control/malformed headers, duplicate/overflow lengths, chunking, compression,
truncation and body bytes exceeding the declared count. Bodies are binary-safe.
Native fetch creates its sink only after valid headers; failed responses/cancel
preserve an existing destination and discard staging. --replace permits regular
files only. Default maximum is 8 MiB; --kernel permits 32 MiB + 4096, without
boot-file privilege or package-signature trust. A downloaded package still needs
ordinary signature/plan/install validation.

The native client does not replay a GET after response/body failure. Connection
establishment retains the shared bounded fresh-socket retry. An operator can retry
a failed download; no guest mutation is replayed to manufacture validation success.

## Validation and delivery

- 708 host tests pass, including invocation ownership, stale handles, provider
  counts, aggregate limits, cleanup, control budgets and request validation.
- Five native-client algorithm tests pass on host mocks: split headers/binary body,
  URL/response rejection, replacement/staging preservation. This does not prove
  real sockets/TLS or VFS permissions.
- Initial corrected installed TCG fixture passed 54 checks, full signed-kernel
  download/verification, fault/cancel cleanup, user denials, secret-free transcripts
  and independent ext2 audit (197.612 s).
- A second fixture passed 58 checks, adding busy/background cancellation and recovery
  (208.721 s). The first fixture was manually stopped when review found its teardown
  needed root authentication; it was corrected, not bypassed.
- Final fixture also includes same-kernel net-tools upgrade, loopback DNS correlation,
  automatic hostname/TLS use and ordinary-user CA-file overwrite refusal. DNS fixtures
  are local; no public Internet is required for acceptance.
- The common release gate includes native-network-smoke alongside login, repository,
  CLI, native-process and kernel recovery. All six remain mandatory before a tag.
- [x] Final targeted matrix passes 66 native checks and ext2 audit (234.326 s),
  including the local DNS/hostname, executable upgrade and CA-file denials.
- [x] Clean-source full qualification passes, including explicit symlink-replacement
  and TLS hostname-mismatch denials. See exact evidence below.
- [ ] Distributor publication and installed VMware/home-path acceptance.

Owners: src/Core/Processes/ProcessNetwork*, NativeProcessNetworkSource,
src/Core/Network/NetworkService, src/Native/x64/Process.s,
userland/net-tools/main.c and tools/native-network-smoke.py.

## Final clean-source qualification

Exact tested source: **faecc722a746a3ef526edae4dfbe25c7c97cda36**. The working tree
was clean throughout qualification. A fresh Release build used pinned Cosmos 3.0.89,
local .NET 10 and QEMU 11.1.2 with TCG. No signing or production keys were needed.

Command: `ZENITH_VALIDATION_ACCELERATOR=tcg python3 -B deploy/distributor/validate.py --keep obj/network-qualified-faecc72`
(with the local dotnet toolchain on PATH). Retained artifacts/logs are ignored
workspace files; the durable reproduction contract is this commit and command.

| Gate | Result | Seconds |
|---|---|---:|
| Common controller regressions | 211 passed | separate pre-build gate |
| Host tests | 708 passed | 9.616 |
| Clean kernel build / package tool | Passed | 24.906 / 1.201 |
| Login/session | 71 passed | 197.324 |
| Signed online repository | 94 passed | 316.254 |
| Native network | 72 passed; TLS, same-kernel upgrade, cleanup and ext2 audit | 257.222 |
| Native CLI | 100 passed; raw-input/fault/cancel restoration | 224.864 |
| Native process | Passed; all 64 pipelines plus graphical/local input | 741.905 |
| Kernel update/recovery | 169 passed; independent ext2/EFI audits | 506.115 |

SHA256:

```text
Zenith.iso bfd68ca860fccb8b8cdcb6ddd99a21b5d2745904d3e71947ae8deac20335110d
Zenith.elf e3f79d59fe0555964f8f7d430da979bd0cf580095132422b831bbcf3dbdb66e3
```

Release targets are kernel **0.0.23**, zenith-net-tools **0.1.0** and manuals
**0.1.5**, all pointing to that exact tested source. A later documentation commit
must not become the kernel tag target. Distributor signing/delivery and installed
VMware/home-path acceptance remain separate. Follow the [VM checklist](../../NETWORK-VM-TEST.md).
