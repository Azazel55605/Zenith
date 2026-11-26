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

During a separate HTTPS mirror transfer, 151 of 417 sampled guest instruction pointers were in memory-mapped device register access, 79 in the TLS byte-copy routine and 78 in GCM multiplication. The figures above time file delivery and commit through the built-in downloader. A package update also verifies the archive and performs a transaction.

For the local four-way comparison, run `python3 -B tools/transfer-bench.py obj/transfer-bench --https --runs 3`. For an installed check, use Polaris's command duration for `fetch --kernel URL FILE` on a fresh destination with sufficient space. Record archive byte count, total seconds and destination filesystem. Calculate Mbit/s as bytes × 8 ÷ seconds ÷ 1,000,000. Time package verification and installation separately. [Benchmark procedure](../DEVELOPMENT.md#toolchain)

### Transfer timing and TLS acceleration (2026-11-26)

| Median of three transfers, decimal Mbit/s | HTTP direct to nginx | Verified HTTPS through the update proxy |
| --- | ---: | ---: |
| Linux laptop, WireGuard route | 35.4 | 22.0 |
| Installed kernel 0.0.80, four-vCPU QEMU TCG/e1000e | 18.7 | 8.4 |
| TLS acceleration candidate, four-vCPU QEMU TCG/e1000e with timing enabled | 13.3 | 12.2 |

Each transfer wrote the same 21,075,879-byte kernel 0.0.80 archive to ext2. Both guest destinations matched the host SHA-256 and passed an independent filesystem audit. HTTP varied from 8.2 to 17.7 Mbit/s on the candidate. These measurements include a different route from the November 25 home-LAN run.

For candidate HTTPS the median timings were 4.94 s receiving socket data, 1.96 s waiting for input, 1.25 s processing TLS records, 3.08 s writing to ext2 and 0.98 s committing the file. These are elapsed times, so they include any preemption during the call. Socket input and disk writes still cost more than record decryption in this guest.

With the mirror's signing key pinned, a four-vCPU guest also fetched and verified both signed catalogs through `pkg refresh` and installed `zenith-man-pages`. The profiler reported a separate transfer for each catalog and for the 47,665-byte package. The installed volume passed `e2fsck -fn`.

The candidate uses the x86 AES and carry-less multiplication instructions when available. To isolate the crypto change, two single-vCPU TCG guests fetched the same 8 MiB from a local authenticated TLS server, using the same kernel and differing only in the advertised instructions. The median record-processing time fell from 5.09 s to 0.59 s and delivery rose from 6.5 to 11.2 Mbit/s. Both instruction configurations returned identical bytes. All four live/installed packet captures showed zero backwards pure ACKs.

A separate single-vCPU HTTPS run sampled the program counter and the immediate MMIO caller while the guest was paused. Of 724 samples, 148 were in SATA command handling, 52 in E1000E send, receive or IRQ handling, and 3 in APIC end-of-interrupt register access. These are samples of the whole transfer and commit. They identify storage as the largest MMIO contributor in that fixture. The remaining receive and file-write costs warrant measuring the ext2/SATA path before changing NIC ring parameters. [Transfer timing fields](../NETWORKING.md#transfer-timing)

### Installed VMware mirror comparison (2026-11-26)

Two disposable four-vCPU Workstation guests used e1000e, NAT and installed ext2 on the same laptop and WireGuard route. The second kernel included both TLS acceleration and aggregate TCP rejection counters. Each guest downloaded the same 21,075,879-byte archive three times per protocol. All 12 transfers completed. The final HTTP and HTTPS copies on both disks matched the host SHA-256, and both volumes passed an independent `e2fsck -fn`.

| Median Mbit/s | Stock kernel 0.0.80 | Combined candidate |
| --- | ---: | ---: |
| HTTP | 1.89 | 2.18 |
| Verified HTTPS | 1.78 | 2.19 |

The stock kernel printed 14,267–16,112 lines per download from the same TCP rejection branch. The candidate counts these events and continues to send the existing ACK, without the per-segment UART write. Candidate HTTPS spent a median 74.72 of 77.27 seconds explicitly waiting for data, 0.10 s processing TLS records and 1.25 s writing the file. It counted a median 14,680 segments starting ahead of the next expected byte and 623 overlapping that byte per HTTPS transfer. The receiver in those two kernels accepted only contiguous data that fit its advertised window. These measurements explain why faster TLS alone has limited impact on this path.

### VMware TCP window probe and overlap repair (2026-11-26)

Two disposable four-vCPU Workstation guests downloaded the same 21,075,879-byte kernel archive over the laptop’s WireGuard route. A header-only capture on VMnet8 covered all eight transfers. Both guests shut down cleanly. Their final HTTP and HTTPS files matched the archive SHA-256, and both installed ext2 volumes passed an offline audit.

| Kernel ELF | HTTP to update mirror, Mbit/s | Verified HTTPS, Mbit/s |
| --- | ---: | ---: |
| Before overlap repair, `3d93ade3`, one transfer each | 2.18 | 2.33 |
| With bounded reassembly, `ffcc6da9`, median of three (range) | 9.56 (4.53–10.89) | 14.18 (8.47–24.73) |

When Zenith advertised a zero-byte receive window, VMware NAT sent a one-byte probe. As the window reopened, a longer TCP segment arrived starting at that same byte. The old receiver accepted the probe and rejected the longer segment because its first byte had already been acknowledged. On baseline HTTP this happened 640 times, matching 640 guest overlap rejections. In 639 cases the guest sent at least three duplicate ACKs at the byte immediately following the probe. Waiting for the ACK to advance accounted for 75.76 of 77.19 seconds. Baseline HTTPS showed the same pattern for all 642 probes.

The updated receiver trims the already received prefix and delivers the new bytes. On the six candidate transfers, all 2,375 repeated-start segments cleared without a triple duplicate ACK at the probe boundary. Repeated payload dropped from approximately 21 MB per baseline transfer to 337–484 bytes per candidate transfer. First transmissions arrived in sequence at VMnet8 on all eight flows. The candidate’s out-of-order buffer stayed unused on this path. A separate four-vCPU QEMU/e1000e check against the same mirror measured 11.0 Mbit/s HTTP and 12.4 Mbit/s verified HTTPS, with matching file digests, a clean ext2 audit and zero retransmitted bytes.

VMware receive interrupts remain an N1 driver task. Boot diagnostics detected MSI and MSI-X, then registered legacy PCI IRQ 255 and attempted to route IRQ `FF` to vector `0x1F`. All 15,456–15,977 accepted frames per candidate transfer arrived through polling, with zero receive interrupts. The median time between the first data in a burst and the ACK catching up was 8.6–9.9 ms across the six runs. Route a supported interrupt or fall back explicitly when the PCI legacy line is invalid, then repeat the same transfer and measure interrupt delivery, polling delay and throughput. Direct home-LAN performance on the user’s usual VM remains unmeasured for this build.

A separate QEMU fixture swaps adjacent data segments between a local test server and a disposable guest. Twelve transfers across HTTP, HTTPS, RAM and installed ext2 completed on the first attempt with byte-identical output and a clean filesystem. An earlier RAM run reported one TLS record authentication failure. Replaying its captured TCP bytes through the receive buffer reproduced the complete ciphertext exactly across 128 read schedules. The cause of that failure remains open. The fixture now fails on any retry. [Controlled transfer benchmark](../../tools/transfer-bench.py)

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

N3 adds stdout, output files, HEAD, bounded redirects and chunked HTTP bodies to signed `fetch`. Web downloads use embedded public roots plus any configured local CA. Package operations continue using the configured mirror trust. The default file limit is 8 MiB, with an explicit ceiling of 32 MiB + 4096 bytes. The newer transfer ABI checks free space on the staging volume before writing a known-length file. The signed CLI and the recovery downloader both rejected a 20 MiB response on a 16 MiB guest mount and preserved the original file. Higher size limits, installed qualification and any desktop notifications remain open. [Current transport contract](../NETWORKING.md#transfer-and-transport-bounds)

The signed guest command fetched the 200,116-byte dated CA bundle from curl.se before any local CA import. Reading the file back from ext2 matched the published digest. The kernel build passed the 77-check native network suite, 183 network checks and 111 online repository checks. The native suite includes malformed bodies, redirects, failed replacement, cancellation and permission checks. [Native gate](../../tools/native-network-smoke.py) · [Dated public digest](https://curl.se/ca/cacert-2026-11-05.pem.sha256)

N4 now has an independent native CLI. A test-signed `zenith-smb` package on an installed QEMU disk discovered shares, listed directories and transferred a 1,179,915-byte binary file against Samba requiring signing. The signed guest also transferred exactly 32 MiB in both directions and refused a 32 MiB + 1 byte download. The extracted guest file matched the source and the ext2 filesystem passed its offline audit. The host interoperability suite also exercises wrong passwords, empty files, explicit replacement and connection loss. Remaining stages include installed-server qualification, Horizon browsing and read-only mounts with ownership, reconnect, open-file and unmount rules. [Usage](../NETWORKING.md#smb-file-transfers) · [Repeatable Samba and guest checks](../../tools/smb-smoke.py)

### N4 — Read-only SMB mounts in Horizon

**Proposed first release.** In Horizon choose **Network → Connect to server**, enter a host and account, select an advertised share or enter a share name directly, then choose **Mount read-only**. Mounting `//nas/media` as `Media` for UID 1000 would create `/mnt/network/1000/Media`. Horizon can navigate that path, Polaris can run `ls` there, and another app can open and seek a file there. Closing Horizon leaves the mount available for the rest of that login. The existing `smb get` and `put` remain independent commands. [Horizon file ABI and transfers](HORIZON.md#the-boundary)

#### The Network browser tab

Choosing **Network** in Places opens the Network browser in the active pane. Middle-click or **Open in New Tab** opens it beside the current work. The tab participates in the existing eight-tab limit and retains its own history and selection. A breadcrumb leads from **Network** to a server and its shares. Back returns to the server list. Once a share is mounted, its folder uses Horizon’s usual directory view, split panes and copy-out actions.

| Location | Contents and actions |
| --- | --- |
| Network | Show the current login’s **Connected shares** and **Saved servers**, with **Connect to Server** available even when both lists are empty. Saving an endpoint is explicit. A saved server can be offline. |
| Server | Request authentication through the portal when needed, then list the disk shares the server advertises to that account. Also accept a directly entered share such as `//nas/media` when enumeration is restricted. Selection alone does not mount a share. |
| Share | **Mount read-only** shows the resulting local path. **Open** on an already connected share reuses its mount. **Unmount** reports open files or copy jobs that keep it busy. A disconnected entry offers **Reconnect**. |

Keep server and share browsing as typed tab locations until the mount broker returns a real path. Support refresh, keyboard navigation, cancellation and the empty, offline, access-denied and busy states. Respond to late replies only if the pane still shows the same request and login generation. Restore the browser location at sign-in without storing credentials or silently reconnecting. The first version browses user-entered and saved endpoints. An optional **Discovered servers** section depends on a later service-discovery backend and must label its source separately.

Exercise the whole route with keyboard and pointer in the Horizon gate. Open Network beside a local tab, browse and mount once, navigate Back, reopen the same mount and verify that the original tab and its history survive. Cancel an authentication request, disconnect a server while its shares are loading, and switch tabs while a reply is pending. Repeat in a second login to verify isolation of saved endpoints and mounted shares.

**Transport prerequisite.** Today an open native socket holds the kernel's single network-operation slot until close and has a 900-second lifetime. An idle mount using that interface would block a package update and eventually time out. Give each mounted share a bounded, independently owned TCP connection. Serialize mutations of the shared connection table and preserve the existing exclusive TLS arena. Handle idle disconnects explicitly. Before connecting the GUI, keep one share mounted and idle past 15 minutes while another process downloads a package, then read from the share again. Also exercise two simultaneous mounts and cancellation of one during a read on the other. [Network operation and socket lifetime](../../src/Core/Network/NetworkService.cs)

1. **Run the signed SMB session in a protected worker.** The post-0.1.0 client library now keeps an authenticated disk share open across calls. A session owns eight file or directory handles. Each read uses a 64-bit offset; directory cursors deliver one bounded page per call and retain their position across requests. The local Samba tests kept two sessions active, sought beyond 4 GiB, traced a live file through a writer's append, truncation and rename, and paged a 600-file directory. Reconnecting invalidated earlier handles. The test-signed Zenith CLI still passed the 32 MiB transfer boundary in both directions on the 0.0.81 kernel. The CLI keeps its deny-write policy during a download. [Session interface](../../userland/smb/session.h) · [Samba and guest checks](../../tools/smb-smoke.py)

   Next, give each mounted share a supervised worker from the signed package. Its invocation owns its socket, cancellation and bounded request/reply buffers. Add lookup/stat metadata, per-request timeouts and a bounded cache. Treat server-side session or tree expiry as a disconnect and represent unsupported or reparse entries explicitly. The multi-session and live-writer checks currently cover the userland library through host sockets. The Zenith transport still needs the independent connection lifetime described above. [Invocation-owned network handles](../../src/Core/Processes/ProcessNetwork.cs)
2. **Bind a connection to the person who mounted it.** A typed portal request opens a trusted host dialog that identifies the server, port and account and distinguishes signing from encryption. The password goes to the designated signed worker through a single-use secret handle. Horizon receives an opaque connection ID and share names. An explicit credential file can reuse the existing owner-only 0400/0600, server/port-bound rules. Save only display names and endpoints as bookmarks. Keep credentials out of paths, argv, events, logs and saved tabs. Support the signed CLI dialects initially and report when encryption, SMB 3.1.1-only or Kerberos is required. The local Unlock dialog remains for Zenith account authorization. [Portal broker](../../src/Core/Portal/PortalBroker.cs) · [Credential file checks](../../src/Core/Processes/NativeProcessFiles.cs) · [SMB trust and limits](../NETWORKING.md#smb-file-transfers)
3. **Publish a real VFS mount.** Add a narrowly scoped kernel mount broker and a read-only `smb` VFS adapter. The broker creates the mountpoint beneath the reserved UID directory, owns its lifetime and refuses aliases containing separators, dot components, symlinks, existing files or another mount. Its registry binds each opaque ID to the original UID, login generation, server, share and worker. Recheck that binding for lookup, listing, open, read and control, including after a change of user or a local Unlock. Remote files are read-only, directories are searchable, and the backend refuses all mutations, device nodes, execution and escaping reparse/DFS targets. Set read-only, no-exec, no-device and no-setuid flags. Give `/proc/mounts` and call 655 the actual type and flags, filter other users’ private mount entries and report unknown capacity explicitly. Today these paths classify only `/proc` as read-only, and the generic guard gives unfamiliar inode types root-owned permissions. The adapter needs explicit metadata and ownership handling. [VFS mount registration](../../src/Core/Storage/Proc/ProcMount.cs) · [Mount policy](../../src/Core/Storage/SystemMounts.cs) · [Inode guard](../../src/Core/Security/GuardedFilesystem.cs) · [Native file service](../../src/Core/Processes/NativeFileService.cs)
4. **Keep remote waits off shared locks and the Horizon event loop.** Give mount operations a bounded work queue. Pin the mount generation and handle under a short lock, perform wire I/O outside native service, file-job and mount-table locks, then validate the generation before delivering results. Horizon currently reads its directory synchronously and requests child counts. For remote paths, load a page asynchronously, skip recursive counts, and discard replies for tabs that navigated elsewhere. Retain the normal file path for breadcrumbs, split panes, open-with and copy-out. Give Network places an owner-filtered mount list and show connecting, mounted/read-only or disconnected. Disable move from the share as well as paste, delete, rename and trash on it. The backend must enforce the same restrictions for other callers. [Horizon listings and places](../../userland/horizon/main.c) · [Native dispatch](../../src/Core/Processes/NativeProcess.cs) · [Transfer jobs](../../src/Core/Files/FileJobs.cs)
5. **Define detach and recovery before enabling the menu.** The owner can unmount without local admin credentials. Normal unmount reports busy while files, directories or copy jobs hold references. Horizon releases the relevant pane references when navigating away. On link loss or worker failure, fail outstanding reads promptly with an I/O error, invalidate cached listings and show the disconnected place. Explicit reconnect reauthenticates to the same endpoint and share and issues a new mount generation. Old handles stay stale even if a filename reappears. Logout or shutdown revokes the generation, cancels pending work, closes handles, wipes credentials and removes only broker-created mountpoints. Mounts do not reconnect automatically after reboot. [Login generation](../../src/Core/Security/Accounts.cs) · [Session teardown](../../src/Core/Sessions/SessionRuntime.cs)

**Acceptance.** Extend the Samba fixture and Horizon guest gate with a normal-user mount. Open the same file by path from Horizon and a second process, seek/read and compare bytes, copy out, and verify all write paths fail. Cover duplicate aliases, other UIDs, local Unlock, bad credentials, unsupported server policy and attempted path escapes. Drop the server during listing and copy, navigate another local pane, test busy unmount, reconnect with an old descriptor still held, then log out and reboot. Confirm the next user sees no share or credentials. Finish with the operator’s actual NAS configuration. Writable mounts require a separate contract for sharing locks, concurrent changes, flush, rename and recovery after an uncertain write. [Samba fixture](../../tools/smb-smoke.py) · [Horizon guest gate](../../tools/horizon-smoke.py)

## Source and evidence

[Services](../../src/Core/Network), [transport](../../src/Core/Network/Transport),
[native client](../../userland/net-tools), [contract](../NETWORKING.md).
[Network extraction/installed evidence](../archive/2026-10-26/native-network-clients.md).
Home DNS/proxy settings are deployment-specific, not public defaults.
