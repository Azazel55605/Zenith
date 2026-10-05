# MyOS technical notes

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

## 2. The Unix model: what exists and what doesn't

**Working now:**

- One root filesystem at `/` with mounts on top: an installed FAT32 partition, or a RAM disk
  in live mode. `/tmp` is always a RAM disk.
- FHS-style layout (`/bin /boot /dev /etc /home /mnt /proc /root /tmp /usr /var`), with
  `/etc/os-release`, `hostname`, `passwd`, `group`, `motd`, `profile` and `fstab`.
- A POSIX-flavored shell: quoting, `$VAR`/`${VAR}`/`$?`, `~`, globs, `|`, `>`, `>>`, `<`,
  `;`, `&&`, `||`, history, tab completion and `/etc/profile`.
- About 45 commands: coreutils-style tools plus `mount`, `df`, `lsblk`, `dmesg`, `free` and
  `install`.

**The big gap: there are no processes.** Every command is C# compiled into the kernel and
runs in kernel mode, in one address space. Cosmos has no program loader and no ring-3
userland. Its own roadmap lists "Userland WASM VM" as future work. Two ways forward:

| Option | What it takes | Verdict |
|---|---|---|
| **WASM + WASI runtime** in the kernel | An interpreter (or later a JIT), plus WASI's POSIX-like file/clock/args API mapped onto our VFS. Programs are compiled from C/Rust/Zig/C# to `.wasm` and live in `/bin`. | Recommended. It's sandboxed by design, follows Cosmos's own direction, and WASI maps directly to what we have. |
| Native ELF processes | Per-process page tables, ring-3 transitions, a syscall interface, an ELF loader, and a libc port. All of it lives below what Cosmos exposes; most of it is in Cosmos internals. | Months of work against internals; not worth it before WASM. |

**Other Unix pieces, roughly in order:**

- **`/proc` and `/dev`** as virtual filesystems: implement `IVfsFilesystemType` with
  synthesized inodes (`/proc/meminfo`, `/proc/mounts`, `/dev/null`, `/dev/sda`). Moderate
  work; the interface is public.
- **Users and permissions:** FAT stores no owner or mode bits, so real permissions need a
  Unix filesystem (section 3). `login`/`su` can come once permissions exist.
- **Background jobs, signals, `Ctrl+C` of a running command:** these need processes or at
  least threads per command. Cosmos has a preemptive scheduler and threads, so threads come
  first.
- **A text editor** (`nano`-like) inside the terminal, and file open/save in Notes.

## 3. How hard is ext4 or btrfs?

Cosmos has a clean seam for this. A filesystem driver implements `IVfsFilesystemType`,
`IVfsSuperblock`, `IVfsInode` and `IVfsOpenFile` over an `IBlockDevice`
(`articles/user/filesystem.md`, "How it works"). Mount it with `VfsManager` and every
`System.IO` call (and every shell command) works on it unchanged. The FAT driver in the
Cosmos source is the reference implementation. Development can happen on a
`MemoryBlockDevice` loaded from an image you build on the host with `mkfs.ext4`, and checked
with `e2fsck`/`debugfs`.

| Filesystem | Read-only | Read-write | Notes |
|---|---|---|---|
| **ext2** | ~1–2k lines, about a week | +1.5k lines (block/inode bitmaps, allocation), 2–3 weeks | The realistic first step. Gives permissions, owners, symlinks and case-sensitive names. |
| **ext4** | ext2 + extent trees + 64-bit + flex_bg: about 2–3 more weeks | Hard. You need metadata checksums (crc32c on almost everything), htree directories, and a **journal** (jbd2). The alternative is mounting only clean, journal-less filesystems, which works but is fragile. | Read-only ext4 is very doable. Safe read-write ext4 is a multi-month project. |
| **btrfs** | Months: copy-on-write B-trees, chunk-tree logical→physical mapping, checksums, often zstd/lzo/zlib compression | A team-scale project (CoW allocation, transactions, multi-device) | Not recommended. |

**Recommendation:** ext2 read-write first, which is enough for a native Unix root
filesystem. Then ext4 read-only, so MyOS can read Linux disks. Revisit ext4 writes only
after a journal design.

## 4. What a self-contained installer needs

**What works today** (tested end to end in QEMU):

```
boot ISO (live, RAM root) → `install sata0 --yes` → tools/make-bootable.sh disk.img
→ boot disk.img with UEFI (no ISO) → root found on sata0p1 → files persist
```

`install` writes a GPT with a 64 MiB EFI System Partition and a FAT32 root. It lays out the
Unix tree, copies `/etc /home /root /usr /var`, writes `fstab`, and puts `limine.conf` on the
ESP. Cosmos's GPT writer leaves the CRCs at zero and writes no backup header; that's
documented as intended for test images, but firmware rejects such a disk.
`GptChecksums.Complete` fixes it up, after which `parted` and OVMF accept the disk.

**Gaps before it's a real installer:**

1. **The kernel can't copy itself or the bootloader.** That's the only reason the host step
   (`make-bootable.sh`) exists. Two fixes:
   - **Limine modules (preferred):** add `module_path: boot():/boot/MyOS.elf` and the EFI
     binary to the ISO's `limine.conf`, and Limine hands them to the kernel in memory. Cosmos
     doesn't expose Limine's *module request* yet; `Cosmos.Kernel.Boot.Limine` has
     framebuffer, memory map, cmdline and others, but not modules. That's a small upstream
     addition.
   - Or read the CD itself: ATAPI support plus an ISO9660 driver. More work.
2. **Root selection:** scanning partitions for `/etc/os-release` works. Passing `root=UUID=…`
   on the Limine `cmdline:` is the robust version (`Environment.GetCommandLineArgs()` already
   works).
3. **BIOS boot:** needs a BIOS-boot partition plus Limine's `bios-install` logic. UEFI-only
   is reasonable for modern machines.
4. **UX:** a graphical installer window (disk picker, confirmation, progress) on top of
   `Installer.Install`, which already reports progress through a callback.
5. **A Unix root filesystem:** ext2 from section 3, once it exists.

## 5. Drivers

The rule that matters most: **Cosmos drivers live in its internal HAL.** `VirtioTransport`,
`Virtqueue`, device registration and the rest are `internal`. A kernel project can reach
single members through `[UnsafeAccessor]` (as `Log.cs` does), but it can't sensibly write a
whole driver that way. Real driver work means either contributing to Cosmos upstream, or
building Cosmos from source (`articles/dev/install-dev.md`) and consuming it from a local
NuGet feed. Set that up before starting driver work.

| Area | Cosmos has | Missing, in priority order |
|---|---|---|
| **Display** | UEFI GOP framebuffer (any UEFI PC), virtio-gpu, VMware SVGA II (+3D) | virtio-gpu hardware cursor and partial flush (VM smoothness). Native Intel/AMD/NVIDIA drivers are enormous; on real hardware GOP is the practical path (fixed resolution, CPU rendering). |
| **Keyboard** | PS/2, virtio, USB HID (boot protocol) via xHCI | Fine for now |
| **Mouse** | PS/2 (with wheel), virtio (relative) | **USB HID mouse**, which almost every real PC needs. **Absolute pointer** (tablet) for VMs. I2C-HID touchpads for laptops (hard). |
| **Storage** | AHCI (SATA), NVMe, USB mass storage; MBR/GPT; FAT | virtio-blk (faster VM disks), ATAPI + ISO9660 (installer), then ext2 |
| **Network** | Intel E1000E, virtio-net; ARP/IPv4/IPv6-link/UDP/TCP/DHCP/DNS | Realtek RTL8111/8168 (most desktop boards), Intel I219/I225. TLS is not implemented yet. **Wi-Fi is the hardest item on this list** (firmware blobs, 802.11 MAC, WPA2/3); skip it for a long time. |
| **Audio** | none | Intel HDA (real hardware), virtio-sound (VMs) |
| **Platform** | ACPI (LAI), APIC/GIC, timers, RTC, reboot/shutdown | SMP (not started in Cosmos), suspend, battery and thermal |

**Minimum set for running on a real PC:** UEFI GOP, xHCI with USB HID keyboard and **mouse**,
AHCI or NVMe, and one common NIC (RTL8168 or Intel I219). Only the USB mouse and the NIC are
missing.
