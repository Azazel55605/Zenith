# Local Cosmos 3.0.89 network and clock adaptations

`tools/PatcherFixup/Program.cs` adapts intermediate build copies from NuGet 3.0.89. It does not change the
NuGet cache, dependency version, or sibling Cosmos source checkout.

VMware testing exposed zero destination MACs in DHCP broadcasts. The released
OutgoingBuffer bypasses ARP for limited IPv4 broadcasts without setting their
Ethernet destination. The enqueue adaptation now sets MACAddress.Broadcast only
when DestinationIP.IsBroadcastAddress is true; unicast routing/ARP is unchanged.
Method signatures and broadcast accessor contracts are checked before injection.
The native network suite captures live and installed traffic and requires both
DHCP Discover and Request to use ff:ff:ff:ff:ff:ff with matching Ethernet/BOOTP source
MACs. Slirp previously accepted the invalid zero destination and hid the defect.

The E1000E driver also returns early when RAL0/RAH0 contain a MAC, without ensuring
RAH0 bit 31 (address valid) is set. All ReadMacAddress returns now activate that
exact-match filter while preserving address bits. The explicit x64 build target
adapts the copied HAL reference after RunPatcher and before CheckIlcCache: the SDK's
plug-filtered list excludes HAL.X64, and applying after cache hashing would leave
the old driver in the compiled ELF. No promiscuous receive mode is enabled.
`ip` reports the initial validity bit, NIC/RX interrupts, driver-delivered/accepted
frames and DHCP replies/rejections. IRQ instrumentation duplicates the already-read
cause register value without clearing it again or changing dispatch. The user's
VMware screenshot confirms the filter was already valid, with zero NIC interrupts
and zero frames; the filter flag does not explain that failure. The driver now
registers its receive drain as a delegate for the timer-driven kernel loop, which
masks interrupts through the restoring scheduler scope to serialize poll/IRQ ring
consumption. Explicit catch/dispose also restores the mask on native exceptions.
Both drains are bounded to 32 descriptors per call; polling does not clear ICR or
disable normal interrupts. The precise VMware interrupt-routing cause is pending.
The native DHCP replay fixture
uses the observed 172.16.43.0/24, TTL/TOS and option profile with valid checksums,
testing both unicast and broadcast Ethernet Offer/ACK delivery through E1000E,
plus deliberate PCI INTx disable and successful DHCP reacquisition by polling.

The released receive storage subtracts `_dataOffset` from `_dataLength` a second
time while appending. Reading a snapshot of `Socket.Available` concurrently with an
incoming segment makes a full intended read partial; the next append can lose data
or pass a negative count to Buffer.BlockCopy in the NIC callback. QEMU reproduced
an unhandled exception during a retry after the first successful streamed download.

The adaptation adds a managed `_zenithReceive` field, replaces `get_Data`,
`AppendToData` and `AdvanceDataOffset` with calls into `Zenith.Network`, and releases
the field at Dispose. Method parameter/return signatures and CLOSED enum value 10
are checked; changes fail the build rather than guessing another layout. The injected
assembly has no Cosmos dependency, so no initializer cycle/private UnsafeAccessor
binding is required. No private object offsets are assumed.

`BoundedTcpBuffer` uses immutable byte-array snapshots and compare/exchange. One
consumer can advance while NIC appends without losing unread bytes or invalidating
the span being copied. Pending data is capped at 128 KiB per connection. Overflow
retires data and sets the owning connection closed, so HTTP completion fails rather
than allocating without limit. Temporary snapshot copies are managed allocations;
this is not a whole-kernel memory budget. TCP connection table behavior remains upstream. Concurrent multiple readers of one socket are not supported.

Host tests cover partial reads, snapshots, overflow and concurrent ordered transfer.
The local network QEMU suite covers stream/retry/cancel/fault cleanup on the actual
NativeAOT bridge. Keep this narrow adaptation version-pinned and propose the storage
fix upstream separately; do not silently remove it on a package upgrade.


M3 completion adds a signature-checked ingress guard before NetworkStack.HandlePacket.
Only bounded standard Ethernet ARP and unfragmented IPv4 ICMP/UDP/TCP pass. Header,
payload and option lengths plus IPv4/ICMP checksums are checked before construction;
IPv6/VLAN/jumbo/fragment traffic is outside this client profile. UDP/TCP checksum
verification stays in Cosmos. DHCP delivery constructs the UDP envelope directly,
bypassing the legacy fixed-offset unchecked DHCP parser; Zenith's bounded DHCP codec
handles it afterward. Upstream DHCP clients are not used by Zenith.

TCP's unused Options list is no longer materialized: its loop incorrectly traverses
payload length and treats EOL padding as an allocation size. Header length/flags and
payload offsets remain unchanged. The protocol FSM did not consult Options; ingress
validates their shape. This does not add MSS/window-scale/timestamp negotiation.

UDP and ICMP queues are adapted to BoundedPacketQueue<T> (32 packets, drop newest).
Immutable snapshots and compare/exchange avoid IRQ lock deadlocks and concurrent
Queue<T> resize/dequeue corruption. The corresponding derived DNS/DHCP queue call
sites are rebound too; Zenith still uses its own codecs. ARP caches, the outgoing
queue, TCP sequence/reassembly/tables and connection retirement are not globally
bounded by these adaptations. They need future Internet/server hardening.

DateTimePlug.GetCurrentTicks returns ClockCorrection.Adjust(rawTicks), so BCL
DateTime/DateTimeOffset and ext2 timestamps share NTP correction. Zero/uninitialized
RTC remains invalid for TLS. The correction does not persist or write RTC.

The post-patcher/wrapper have explicit cache invalidation: changing them removes
Cosmos's patch cache stamp before SetupPatcher. All imports are rebound again before
write to remove the patcher's redundant self-reference. A contract mismatch fails
rather than applying a guess. No dependency cache or sibling checkout is edited.

P3 kernel delivery exposed an additional Cosmos 3.0.89 TCP issue: ACK-only payload
is appended without acknowledging it, so a large ordinary HTTP response exhausts
the peer's receive window. The signature-checked build copy now routes all nonempty
ESTABLISHED payload through the existing acknowledge path. Its receive-range check
uses TcpSegmentPolicy: exact next sequence and payload length within the window,
including sequence wrap, rejecting gaps/duplicates without assembling corrupt data.
ACK packets advertise Zenith's receive window rather than the peer's send window.
The advertised 8 KiB window shrinks with unread bytes and reopens with an ACK when
the application drains data. This prevents a response from filling the buffer while
the download loop writes to disk. The patch checks the upstream ACK flag value and
the original branch structure before replacing them; it does not add TCP reassembly.
Upstream source remains unchanged. Host policy tests and native real ELF-sized HTTP
transfers cover this adaptation; the network lab remains a regression gate.
