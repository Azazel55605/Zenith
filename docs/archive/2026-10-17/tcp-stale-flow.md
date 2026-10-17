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
## Clean-source acceptance and release candidate

Exact tested source: `db90ebc07929da5dbae9b5c113212ca8e1b50b34`.
`python3 -B deploy/distributor/validate.py --keep
/tmp/zenith-tcp-ports-release-20261017` rebuilt from that clean source under TCG,
verified unchanged source and artifact hashes at completion, and exited 0 with
"Unsigned acceptance passed". No gate was skipped and --existing was not used.

| Gate | Result |
|---|---|
| Common controller regressions | 112 passed |
| Release host tests | 639 passed; 9.6 s |
| Kernel build | Passed; 22.8 s |
| Package tool | Passed; 1.4 s |
| Login/session/styling | 71 checks; 205.3 s |
| Installed repositories | 92 checks; 348.0 s |
| Protected native programs | Passed; 291.5 s |
| Kernel update/recovery | 169 checks; 512.3 s |

Real-dependency adapter tests were repeated against the final generated outputs:
12/12 passed, including drift refusal, unchanged NuGet bytes and byte-idempotence.
The targeted TCP test was repeated on the exact final artifact at
`/tmp/zn-tcp-ports-final-20261017` (102.335 s): bounded failure/exhaustion, HTTP
recovery, trust import, 12 verified HTTPS fetches and clean poweroff all passed.
Its wire.pcap has 13 successful-flow SYN source ports, all distinct and in range:
54090, 59675, 65260, 54461, 60046, 49247, 54832, 60417, 49618, 55203, 60788,
49989, 55574. The failed unreachable-address fixture exercises ARP/connect timeout;
it does not reproduce the VMware stale-ACK generator. These temporary paths may
expire; source/tests and this evidence summary are the durable record.

Final artifact SHA256:

```text
ISO 112964c149fdc3900e004e5a4910248a52ff7139ecbd69652ce9a054ba851b38
ELF 511f672583bd7c537b7b9579c5e4e7414f6858aacdc2f8697a1dd713e8f79f0a
```

Proposed handoff: kernel/v0.0.17 at that exact tested source. No release has been
signed, published or tagged by this local acceptance run. Source/tag publication
requires operator authorization; the tag will trigger the distributor pipeline.
After server delivery, install/boot/confirm the kernel and repeat refresh/update
without manual DNS priming. If it still fails, compare vmnet8 with host egress to
locate the stale-flow generator. Actual VMware/proxy success remains unproven;
continuous-operation failures are not explained solely by reboot port reuse.


## Authorized release handoff

The operator explicitly authorized pushing source/validation notes and releasing
kernel/v0.0.17. On 2026-10-17 the atomic push advanced origin/main to `0293359`
and created the annotated tag at `db90ebc07929da5dbae9b5c113212ca8e1b50b34`.
Local full acceptance above remains tied to that exact code commit. This handoff
supersedes the proposed/pending publication wording above; it establishes Git
source/tag delivery only. The distributor must still build/sign/publish/verify,
and actual installed VMware/proxy reliability remains to be checked.


## Initial installed follow-up

After the release handoff the operator reports: "ok in and it seems to be
working". Record 0.0.17 as installed with initial improvement on the actual path;
explicit boot confirmation, repeated refresh/update counts and sustained acceptance
are not separately reported. This does not locate the stale-flow generator.
