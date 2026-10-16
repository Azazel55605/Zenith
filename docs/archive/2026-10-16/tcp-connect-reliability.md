# Intermittent package TCP connects — 2026-10-16

## Operator evidence and boundary

The operator reports kernel 0.0.14 installed. The home gateway's stale negative
DNS cache was cleared, restoring distributor hostname resolution. A subsequent
Zenith screenshot shows intermittent `Failed to open TCP connection!` during both
`pkg update` and standalone `pkg refresh`: one run refreshes core generation 17
before extra fails. `nslookup` returns the expected proxy address 10.0.10.3.
Sunbeam tools 0.2.0 and the login/resource packages appear installed.

The original claim that only the combined update path fails was corrected by the
operator. No stale-mirror configuration or update-planner defect is established.
The initial handshake-loss cause on VMware/proxy/VPN remains unproven; a targeted
vmnet8 capture of TCP port 443 was requested. Do not equate DNS recovery with
TCP acceptance or claim the source fixes prove the actual network cause.

## Operator packet capture after initial local validation

On vmnet8 the operator captured:

```text
172.16.43.141:49158 -> 10.0.10.3:443 SYN seq=2921633224
10.0.10.3:443 -> 172.16.43.141:49158 ACK ack=2969694321
172.16.43.141:49158 -> 10.0.10.3:443 RST seq=2969694321
```

The ACK arrives 19 microseconds after the SYN, has no SYN bit, and acknowledges
2969694321 rather than the expected 2921633225. This is not a successful handshake;
Zenith's reset for the unacceptable ACK is appropriate (RFC 9293 section 3.10.7.3).
The fast response and unrelated ACK are consistent with stale flow state in the
NAT/proxy path, but a guest-side capture cannot identify which device generated it.
The fresh-socket/fresh-local-port retry addresses this failure pattern without
accepting an invalid ACK or bypassing TLS. Actual installed-path acceptance of the
new patch remains pending; 0.0.14 has not acquired the local changes.

## Source findings and changes

Pinned Cosmos SocketPlug.ConnectTcp sends a SYN and waits for ESTABLISHED with a
5000 ms timeout. Its generic `Exception("Failed to open TCP connection!")` does
not qualify for Fetch's IOException retry. CloseTcp has no SYN_SENT removal
branch, so closing a timed-out socket leaves its registration/local port behind.

PatcherFixup marks the timed-out state machine CLOSED before throwing, enabling
explicit owner Close to remove it. Adaptation checks the released failure literal,
Tcp local/status setter/enum and throw shape; repeated adaptation is byte-identical.
It only edits build copies, retaining package-cache and sibling-upstream bytes.

TcpConnector retries that exact connect failure once with a fresh socket after
250 ms. Failed sockets are closed before another is created, cancellation is
checked between attempts and after connect, and unrelated exceptions retain their
identity. TLS and application writes are outside this retry. Exhaustion reports
hostname, port and resolved IPv4. Existing Fetch has at most two transfer attempts,
so a completely unavailable endpoint can incur four five-second connect waits.
Refresh also names the repository before downloading its index.

## Local verification

- Four host connector regressions: transient success with cleanup ordering, bounded
  exhaustion/endpoint diagnostic, post-connect cancellation cleanup, and unrelated
  exception propagation without retry.
- Full host suite: **628/628 passed**.
- Nine real-dependency adaptation tests: TCP timeout, existing IDT and quiet-trace
  adapters; includes immutable dependency bytes, repeat idempotence and drift refusal.
- Release ISO/ELF build succeeded with user-local .NET 10.0.401 and pinned Cosmos
  3.0.89. System SDK 10.0.113 had an unrelated missing workload manifest/runtime pack;
  no system installation was changed. DOTNET_CLI_HOME used a disposable /tmp path.
- Disposable TCG guest regression: an unreachable address in the isolated slirp
  subnet exercises both failed connects and the final endpoint error, then a
  successful HTTP fetch/trust import and **12 sequential verified HTTPS fetches**
  pass, followed by clean poweroff.

Commands:

```sh
python3 -B -m unittest tools/test_tcp_connect_fixup.py tools/test_quiet_fixup.py tools/test_idt_fixup.py
dotnet test tests/Zenith.Tests
python3 -B tools/tcp-connect-smoke.py --iso /tmp/zenith-tcp-build/Zenith.iso --keep NEW_DIRECTORY
```

Local guest evidence: /tmp/zn-tcp-20261016-v2.log and
/tmp/zn-tcp-20261016-v2/guest/serial.log. These paths may expire.
An initial fault fixture using a closed host port did not reliably exercise a
failed handshake through slirp and was replaced with the unreachable subnet
address; it is not counted as successful fault coverage.

- Installed repository gate: **92/92 passed in 317.023 s** on the final rebuilt
  candidate, including signed combined data/kernel update, actual activation/reboot,
  rollback-floor/freshness checks, partial-download recovery, non-root rejection,
  persistent state and independent clean ext2 audit. Evidence:
  /tmp/zn-tcp-repo-20261016.log and /tmp/zn-tcp-repo-20261016/.
  Generated output-x64 ISO/ELF now match the candidate; previous generated artifacts
  were preserved under /tmp/zenith-before-tcp-generated/.

## Full pre-tag acceptance and release handoff

Exact tested source: `f6bf89389904aab1249bea6ed03c36a66cf36700`.
The common unsigned `validate.py --keep` run rebuilt the candidate from that clean
commit (not --existing), passed all gates, checked unchanged artifact hashes and
confirmed the source stayed clean. Evidence: /tmp/zenith-tcp-release-20261016-v2/.

| Gate | Result |
|---|---|
| Controller regressions | 109 passed |
| Release host tests | 628 passed; phase 9.4 s |
| Release build | Passed; 23.5 s |
| Package tool | Passed; 1.2 s |
| Login/session/styling | 69 checks passed; 211.8 s |
| Installed repositories | 92 checks passed; 350.4 s |
| Protected native programs | Passed; 228.2 s |
| Kernel update/recovery | 169 checks passed; 545.8 s |

The nine dependency adapter regressions also passed against the rebuilt outputs.
The targeted TCP failure/recovery plus 12 verified HTTPS fetches was repeated on
the exact final artifact (/tmp/zn-tcp-final-20261016.log); it passed.

Final candidate SHA256:

```text
ISO 70d51d43628c9e75c9e264465260bc5bcd5532674025c8f5e0f51bc4eb85f520
ELF e08fe1eb01a54e1c8b68c7e35b5134ab18627921b11b59bb70d245350d90cd90
```

The first full-run attempt stopped during host compilation because an explicitly
overridden NuGet package root lacked the separator required by the test project's
reference paths. The validation environment was corrected and the entire common
run restarted; no test was omitted and no kernel source change was needed.

Release handoff: kernel/v0.0.15 targets the exact tested code commit `f6bf893`.
The distributor must build, sign, publish and verify delivery from that tag. These
local results do not claim server publication or installed VMware acceptance.
Repeated installed refresh/update is the remaining real-path test. Preserve TLS,
signatures, catalog generation/freshness and explicit boot confirmation.

## Operator follow-up

After the 0.0.15 handoff, the operator reports: "the issue seems to be gone".
Record this as improvement on the actual VMware path, not a claim of exhaustive
network acceptance. The follow-up does not separately specify the installed
version, boot confirmation or individual distributor delivery results. The source
fix, local validation and pushed tag are confirmed independently above.
