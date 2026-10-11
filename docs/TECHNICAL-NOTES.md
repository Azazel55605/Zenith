# Zenith technical notes

Historical display measurements retained; architecture sections reconciled on 2026-10-11.
Use [ARCHITECTURE](ARCHITECTURE.md) for current facts and [SYSTEM-PLAN](SYSTEM-PLAN.md)
for future design. Upstream availability must be verified against installed APIs.

Findings behind the [roadmap](ROADMAP.md): what Cosmos Gen 3 (3.0.89) gives us, what we measured,
and what each larger step costs.
Paths like `articles/user/filesystem.md` refer to the vendored Cosmos docs in `docs/cosmos/`.

## 1. Display and input: what was wrong, what changed

Measured with a serial-log probe (1280×800, one window open):

| Configuration                       | Full recompose | Copy to screen |
|-------------------------------------|---------------:|---------------:|
| std-vga, TCG (old VS Code task)     | ~44 ms         | ~5.7 ms        |
| std-vga, KVM                        | 5–11 ms        | ~0.6 ms        |
| virtio-gpu, KVM                     | 5–10 ms        | ~0.6 ms        |
| virtio-gpu, KVM, after the fixes    | ~3 ms (rare)   | ~0.3 ms        |

The slow, uneven mouse came from four things stacking up:

1. **No KVM.** The run task used pure emulation (TCG), roughly 8× slower for this workload.
2. **Every pointer move recomposed the whole desktop.** Now the desktop keeps a cached scene,
   and a pointer move only restores the pixels under the old cursor and draws it at the new
   position. A full recompose happens only when the hovered control changes, on clicks and
   keys, or while dragging.
3. **Text was drawn one virtual `DrawPoint` call per pixel.** `FontFace` now rasterizes each
   glyph once (through the public API) and blits it from a cache.
4. **`Thread.Sleep(10)` paced the loop**, and in this build every sleep also writes several
   lines of scheduler logging to the serial port. The loop now calls `Power.Halt()`, which
   wakes on the next interrupt (mouse, keyboard or timer).

`tools/run.sh` now boots with KVM (falling back to TCG), **virtio-gpu** (Cosmos picks
`VirtioGpuCanvas` automatically when the device is present), and **virtio mouse/keyboard**.
In testing, 150 synthetic moves of (3, 2) landed exactly 450×300 px away, with nothing lost.
`grab-on-hover` keeps host and guest pointer motion in step; release the grab with Ctrl+Alt+G.

What is left in this area:

- **Absolute pointer (virtio-tablet / usb-tablet).** This makes the guest cursor track the host
  cursor exactly, with no grab needed. Cosmos only handles relative input (`EV_REL`); a tablet
  needs `EV_ABS` handling in `VirtioMouse` (a small upstream change, see section 4 for why it
  has to be upstream).
- **Hardware cursor.** virtio-gpu has a cursor queue (Cosmos creates it but sends nothing).
  With it, pointer moves would cost nothing at all.
- **Damage rectangles.** `Display()` transfers the whole frame to the host. Transferring only
  changed rectangles needs the driver's private `TransferToHost2D(x, y, w, h)` exposed.
- **Debug log noise.** `CosmosEnableUART=false` in the `.csproj` removes all serial output at
  compile time. That makes the loop faster, but it also silences `dmesg`'s serial copy and
  the boot log, so it's best kept for release builds.

## 2. Current architecture and earlier conclusions

The initial analysis predated M2. Its FAT-only root, generic `/proc`/`/dev` wishlist,
experimental ext2 status and incomplete installed-root statements are superseded by
[ARCHITECTURE](ARCHITECTURE.md). New roots are bounded ext2; proc/dev, metadata,
symlinks, directory moves and check-only fsck are verified. Users/enforcement are pending.

There are still no independent application processes: all built-ins and GUI applications
are kernel-linked. WASM/WASI is a candidate runtime, not a settled decision. Native
ELF/POSIX userland requires much deeper framework work. The [Bash assessment](BASH-PORT.md)
explains why a basic WASI filesystem API alone does not provide Bash compatibility.
Avoid treating the initial effort estimates as schedules or framework guarantees.

## 3. Filesystem direction

Bounded ext2 read/write is the implemented foundation, with Linux tooling interoperability
and independent e2fsck verification. The [driver source record](../src/Core/Storage/Ext2/SOURCE.md)
explains upstream adaptation, licensing and local fixes. No journal, broad ext4 feature
support or guest repair is implied. Mounted filesystems remain unclean until clean unmount.

Ext4 read-only remains an optional follow-up requiring explicit extent/feature/checksum
coverage and native fixtures. Safe ext4 writes need a journaling/recovery design. Btrfs
would introduce a substantially different allocation/tree/transaction implementation and
is deferred. These are architectural assessments, not implemented capabilities.

## 4. Installer and update implications

The verified installation path is live boot → guest install (GPT, FAT32 ESP, ext2 root)
→ host `make-bootable.sh` → disk-only UEFI boot. Root discovery uses os-release;
UUID selection and copying boot artifacts without host assistance remain M4 work.
GPT completion is still a local adapter. See [DEVELOPMENT](DEVELOPMENT.md) for reproduction.

Package updates need additional boot activation and recovery semantics. Ext2 file rename
and FAT ESP replacement are not assumptions of crash-safe system activation. The
[package design](PACKAGE-MANAGER.md) evaluates coordinated release generations versus
image/A-B roots before committing to one layout.

## 5. Framework and driver work

Cosmos device plumbing is often internal. Implementing device drivers or boot protocol
extensions may need an explicit upstream/local-framework change; inspect the installed
3.0.89 APIs rather than assuming host .NET APIs work. Use [local package switching](DEVELOPMENT.md)
without casually running the sibling checkout's bootstrap.

The [vendored networking reference](cosmos/articles/user/network.md) describes framework
interfaces; Zenith network configuration/fetch/TLS integration remains future M3 work.
Likewise QEMU graphics/storage/input validation does not establish physical hardware
support. M7 requires a documented UEFI hardware matrix and measured input/storage/network
behavior. Audio, Wi-Fi, suspend, multi-monitor and advanced GPU acceleration are not
promised by the current product plan.

The historical display measurements above are useful context, not fresh benchmarks.
Re-measure on named hardware/configuration before making new performance claims.
