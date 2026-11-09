# Primary display settings (M2/D2)

Meridian's Display Configuration page and the trusted `display` command use the
same root-only revisioned service. The desktop owner performs device changes;
protected applications never receive framebuffer addresses or driver access.
See the [master roadmap](ROADMAP.md) for qualification and remaining scope.

## Use and recovery

Choose **Resolution and scale**, then Apply. A host-owned 15-second preview offers
Keep and Revert. Only Keep persists the choice. Timeout, requesting client exit
and desktop teardown restore the previous layout and retained buffers. Open
window objects and application drafts remain in place; windows are clamped into
the new logical work area. Maximize/restore geometry follows the new bounds.

The CLI exposes the same transaction for the active graphical desktop:

```text
display
display test 1600 1200 2
display test 1920 1200 150
display keep
display revert
```

Use a listed resolution; the example requires sufficient framebuffer memory.
Scale is 1 or 2 (100% or 200%) or a percent: 125, 150 or 175 on VMware SVGA (x64).
`display` lists the percents each mode supports. The minimum logical desktop is
800x600. Scaling magnifies the completed UI and maps physical pointer coordinates
back into logical coordinates. 200% doubles pixels; 125-175% resample the logical
frame bilinearly while it is presented (`zenith_svga_present_filtered`), so text is
soft. This is a stopgap: sharp scaling needs the shell to draw at the device's
resolution, a scale in the client ABI and scaled toolkit atlases (roadmap D2). The
pointer is drawn sharp at any scale when the device draws it (VMware).

Kept system choices are stored atomically in `/etc/zenith/display.conf`:

```text
ZENITH-DISPLAY 1 1600 1200 2
ZENITH-DISPLAY 2 1920 1200 150
```

Integer scales keep schema 1, which earlier kernels read; schema 2 holds only a
fractional percent (an earlier kernel treats it as unknown and keeps its boot mode).
Malformed or unknown schemas disable writes instead of being overwritten. An
unsupported saved mode falls back to the boot display. Add `display=safe` to the
boot command line to ignore the saved choice without deleting it. Serial/no-GPU
recovery remains available; display mutation requires an active graphical owner.

## Driver and transaction boundaries

Pinned Cosmos 3.0.89 VMware SVGA canvases have real mode setters. Choices are
bounded by the device's maximum width/height, framebuffer offset and memory for
two 32-bit frames, plus a conservative 1920x1200 limit. A 16 MiB device can fit
1600x1200 double buffering, but not 1920x1200. Mode application verifies actual
SVGA width, height, bit depth and pitch registers. Logical desktop and scaled
output buffers are allocated before switching; the previous buffers survive
until confirmation. An unrecoverable hardware restoration error propagates.

The pinned Virtio GPU and GOP setters ignore requested dimensions, so those
profiles advertise only their current physical mode. A fixed display can still
use a supported integer scale. Orientation, refresh rate, brightness, tearing,
sharp fractional scaling, multiple outputs and night light remain WIP.

## Protected ABI

`userland/include/zenith_display.h` defines calls 640 (512-byte version-1 snapshot)
and 641 (40-byte request), requiring `zenith-native-display-abi 1.0`. Snapshot
contains current physical/logical size, scale, bounded driver name, up to 24
modes, revision, preview token, remaining seconds, permissions and error state.
Requests carry action, revision, token, mode index and scale. Snapshot flag 8
means the 32-bit percent at offset 320 (formerly reserved tail) is current and a
request's scale may be a percent (125/150/175); 1 and 2 remain 100% and 200%, and
the legacy scale field stays 1 or 2 (1 under a fractional percent) so earlier
Settings releases keep working. All buffers and
reserved fields are validated. Preview ownership is the captured PID; callers
cannot confirm or revert another application's trial. Root authority is checked
at request admission; persistence runs in an explicitly restored trusted scope.
Managed service/device work stays on the BSP, including opt-in `smp=native` boots.

The mandatory `tools/native-display-smoke.py` gate uses disposable installed
disks. It checks GUI Keep/Revert, native client exit, independent timeout,
scale/resolution, persistence, fixed-mode refusal, safe boot, permission denial
and independent ext2 audit. Local QEMU proof does not establish installed VMware
or physical-device acceptance.

## Installed VM acceptance

After installing a qualified candidate, open Meridian and Terminal together.
Choose a smaller supported resolution and verify that both remain accessible;
let the first preview expire. Repeat and Keep, then reboot and confirm the saved
choice. At 200%, check pointer clicks, wheel navigation, window dragging and the
panel against their visible targets. Revert must restore the prior geometry.
Test `display=safe` from the boot menu before relying on persistent mode changes.
Record the actual driver and result under Q2/H1 in the master roadmap.
