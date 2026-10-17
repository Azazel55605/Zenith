# Installed TCP stale-flow recurrence — 2026-10-17

Investigation, not a completed fix. Operator reports replacement distributor
restored at its original address, all three release records green after delivery
verification, and a full guest update successful. Repeated refresh/update still
fails intermittently; waiting later allowed the operation to succeed.

## Packet evidence

The failed Zenith flow captured on vmnet8 is:

```text
11:59:51.286991 172.16.43.141:49159 -> 10.0.10.3:443 SYN seq=3055019512
11:59:51.287033 10.0.10.3:443 -> 172.16.43.141:49159 ACK ack=2943468050
11:59:51.296398 172.16.43.141:49159 -> 10.0.10.3:443 RST seq=2943468050
```

The reply has no SYN bit and does not acknowledge 3055019513. This is the
previously observed stale-flow pattern, before TLS/application data, with a
42-microsecond response. Resetting the unacceptable ACK is appropriate. Later
successful handshakes/data in the same capture belong to 172.16.43.130, not
Zenith's 172.16.43.141. They do not establish Zenith recovery; recovery after
waiting is a separate operator report. A guest-side capture cannot identify
whether NAT/proxy/server generated the reply. Waiting is consistent with old
connection state expiring but does not prove the exact state holder.

## Dependency inspection

Read-only Mono.Cecil inspection of the actual NuGet Cosmos.Kernel.System 3.0.89
DLL confirms Tcp's static initializer sets s_nextPort=49152. GetDynamicPort
increments it, wraps at 65535 and excludes locally active connection ports;
it has no per-boot random seed or knowledge of previous-boot external mappings.
Zenith's current adapter does not change this allocator. Fresh-socket retry
therefore moves to another sequential port in the same boot sequence, not a
fresh randomly selected range after reboot. This is a collision risk, not yet
proof of the cause of every reported failure. Runtime dependency bytes were not
modified. Temporary inspection project/output are outside the repo.

## Next evidence

Operator clarification: failures also occur during uninterrupted operation,
independent of reboot or waiting. Per-boot reuse is therefore not established as
the general cause. The operator authorized pursuing the stale-flow hypothesis;
port allocation changes are a mitigation candidate, not a located root cause.
If needed, compare vmnet8 with physical-interface SYN/reply traffic to locate
where the stale response is generated. The laptop's current route to 10.0.10.3
is direct via enp6s0f3u2 with source 10.0.10.4; vmnet8 is 172.16.43.1/24.
The prior capture included another VM: future guest-only filters should include
host 172.16.43.141 to avoid early packet-limit exhaustion.

## Mitigation candidate and local validation

`TcpPortSequence` uses a hardware-entropy-seeded offset and odd stride over the
16,384 dynamic ports. Interlocked candidate advancement avoids repeats within a
cycle; the pinned transport's active-port checks and collision budget remain.
Initialization is once-only; missing entropy does not fall back to a fixed seed.
The existing retry/timeout cleanup, invalid-ACK rejection, TLS and package checks
remain in force. This does not fix or identify external stale state, and source
port randomization alone is not a guarantee of transport security.

- Host suite: 639/639 passed, including full-range/no-repeat/wrap, seed effects,
  concurrent candidates, skipped candidates and fail-closed/once-only initialization.
- Release ISO/ELF built in `/tmp/zenith-tcp-port-build` using user-local .NET
  10.0.401, pinned Cosmos 3.0.89. Initial helper compilation emitted an unrelated
  read-only NuGet vulnerability-cache warning; compilation succeeded.
- 12 real-dependency tests passed: timeout/port adapters, IDT and quiet tracing.
  Original NuGet DLL bytes are unchanged; repeated adaptation is byte-idempotent;
  changed failure or port contracts are refused. Tests can use exact candidate
  dependencies via ZENITH_FIXUP_REFERENCES. CI now runs the adapter suite after
  its Release kernel build.
- Targeted guest TCP/HTTPS regression passed in 100.486 s under TCG: failed
  connect exhaustion, subsequent HTTP recovery, TLS trust import, 12 consecutive
  verified HTTPS fetches and clean poweroff. Evidence is in
  `/tmp/zn-tcp-ports-20261017`, including wire.pcap. Observed successful SYN ports
  start 59506, 52899, 62676, 56069, 49462; no change to TLS verification.
- Full clean-source acceptance is pending; source is committed before running it.

No new kernel tag or actual VMware-path success is claimed at this stage.
