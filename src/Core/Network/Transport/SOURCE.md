# Local Cosmos 3.0.89 TCP receive adaptation

`tools/PatcherFixup/Program.cs` adapts only the intermediate build copy of
`Cosmos.Kernel.System.Network.TCP.Tcp` from NuGet 3.0.89. It does not change the
NuGet cache, dependency version, or sibling Cosmos source checkout.

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
this is not a whole-kernel memory budget, and protocol/packet/UDP queue behavior is
still upstream. Concurrent multiple readers of one socket are not supported.

Host tests cover partial reads, snapshots, overflow and concurrent ordered transfer.
The local network QEMU suite covers stream/retry/cancel/fault cleanup on the actual
NativeAOT bridge. Keep this narrow adaptation version-pinned and propose the storage
fix upstream separately; do not silently remove it on a package upgrade.
