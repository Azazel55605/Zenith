# Horizon

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md) · [Applications](../APPLICATIONS.md)

Work IDs below are detailed steps of the master tracker (A1), not a separate schedule.

Horizon is the file manager, in the spirit of KDE Dolphin: places, a breadcrumb, icons and
details views, an information panel, tabs, split panes and transfers that show honest progress.
[Concept](../design/files-concept.svg). It is a native package, `zenith-horizon`, built from the
shared toolkit (`userland/ui`). The built-in Files window stays for testing and recovery; the
launcher lists Horizon beside it once the package is installed.

Requested 2026-11-15: overhaul Files as Horizon (name signed off), add tabs to the concept, then
implement.

## Tabs

Added to the concept on 2026-11-15, between the title bar and the toolbar.

- Each tab keeps its own location, back/forward history, view mode, sort, zoom, scroll position and
  (from HZ2) split. Selection and the focused item come back when you return to a tab.
- The window title and the taskbar follow the active tab: `Documents — Horizon`.
- Up to 8 tabs. **Ctrl+T** opens a tab at the current location, **Ctrl+W** closes one (the last
  tab closes the window), **Ctrl+Tab** and **Ctrl+Shift+Tab** cycle, a click on a tab switches to
  it, its × closes it, + adds one.
- **Open in new tab** on a folder (context menu, Ctrl+Enter, or a middle click from kernel 0.0.67) opens it
  behind the current tab. A middle click on a tab closes it.
- A tab that shows a split view is labelled with both folders (`Documents | Archive`).
- Tabs are saved when Horizon closes and restored at the next start (HZ4).

## The boundary

The kernel owns every rule; Horizon shows and asks.
- Listing, free space and transfers go through **zenith-native-files-abi 1.0** below. Ordinary
  native file calls are limited to 1 MiB of reads and 4096 calls a minute. That is enough for
  `cat`, not for copying a folder of photos.
- Rename, new folder and single-item permanent deletes use the existing path call (405).
- Everything runs with the person's own identity and the ordinary VFS checks: search permission on
  ancestors, write permission on the parent, the sticky bit, read-only mounts.
- Horizon never becomes root on its own. Changing files that belong to someone else goes through the Desktop
  Portal's Authorization (Unlock, HZ4): the kernel then serves that Horizon's file calls as root until it closes.

## Native ABI: `zenith-native-files-abi` 1.0 (kernel 0.0.58)

[Header](../../userland/include/zenith_files.h). Older kernels answer -38. Horizon then says it
needs a newer Zenith.

| Call | Arguments | Result |
|---|---|---|
| 654 list | request (1040 bytes), 1040, page buffer (32768 bytes) | One page of a directory: entries with name, size, mode, owner, group, modification time, links, and for folders the number of items in them. Returns the count in this page. |
| 655 filesystem | path, path bytes, 128-byte buffer | The filesystem holding the path: total and available bytes, block size, read-only flag, mount point and type. |
| 656 start a transfer | request, request bytes (24..8192), 0 | Copy, move or delete up to 64 items. Returns a job id. |
| 657 transfer status | job id, 512-byte buffer, 512 | The job's state, counts, bytes, current item, message and any question. Returns the state. |
| 658 transfer control | job id, command, value | Cancel, answer a question, forget a finished job, or (kernel 0.0.59) wait for it. |

**654 list.**
- The request is `version` 1, `flags`, `offset`, `path_bytes`, then the path (1..1024 bytes).
  Flag 1 asks for folder item counts.
- The page starts with a 48-byte header:
  - `version`;
  - `count` (in this page) and `total` (in the folder, at most 4096);
  - `next` (offset of the next page, 0 at the end);
  - the folder's own modification time and mode;
  - `flags`, where bit 0 means the caller may create and remove entries here;
  - `now` (UTC seconds) and `zone`, the local time zone's offset in seconds now.
- Entries follow, each 8-byte aligned:
  - `record_bytes`, `name_bytes`, `mode`, `uid`, `gid`;
  - `size`, `mtime` (seconds since 1970, UTC), `links`, `items` (-1 when not counted), `target_mode`;
  - `zone`, the local offset at that time, so programs show local times without a zone database;
  - the name, NUL-terminated.
- `target_mode` is the mode of a symbolic link's target, or 0 when it cannot be followed.
- Item counts are given for at most 128 folders a page, so a large folder of folders stays quick.
  Entries `.` and `..` are never listed.
- Errors: -2 missing, -13 denied, -20 not a folder, -22 malformed.

**656 start.**
- The request is `version` 1, `operation` (1 copy, 2 move, 3 delete), `flags`, `count` (1..64)
  and `text_bytes`, then the text: the destination folder and then each source, all
  NUL-terminated. Delete has an empty destination.
- Flags answer questions before they are asked: 1 replace all, 2 skip all.
- Refused before anything starts:
  - -2: a source is missing;
  - -22: a folder copied or moved into itself, or a malformed request;
  - -16: eight jobs are already running;
  - -24: this program already runs two.
- The job counts first, then works.
  - A copy that cannot fit the destination's free space fails before writing anything.
  - Moves within one filesystem rename, so they finish at once. Across filesystems they copy, then
    remove each source once its copy is complete.
  - Folders merge into an existing folder of the same name only after the person says so.
  - Symbolic links are copied as links. Device files, sockets and FIFOs are skipped and counted as
    errors.
  - A cancelled or failed copy removes the file it was writing. Files already finished stay.
- Jobs belong to the program that started them. They are cancelled when it exits.

**657 status.**
- Header: `version`, `state`, `operation`, `errors`, `items_done`, `items_total`, `bytes_done`,
  `bytes_total`.
- Then three texts: `current` (the item being worked on), `message` (why it ended, or the last
  error) and `question`, the name a question is about.
- `question_kind`: 1 a file of that name exists, 2 a folder of that name exists.
- States:
  - 1 counting;
  - 2 working;
  - 3 waiting for an answer;
  - 4 done (with or without skipped errors);
  - 5 failed;
  - 6 cancelled.

**658 control.**
- Command 1 cancels the job.
- Command 2 answers a question. The value is 1 skip, 2 replace, 3 keep both (`name (2).ext`) or
  4 merge (folders), plus 256 to apply the answer to every later question of this job.
- Command 3 forgets a finished job.
- Command 4 (kernel 0.0.59) waits up to `value` milliseconds (1..1000) until the job ends or asks a
  question, and returns its state. A program without a window has no other way to wait: a spin
  between status calls spends its CPU budget (10 seconds) and the processor the job's own thread
  needs. Kernel 0.0.58 answers -22; `horizon --job` then falls back to spinning.

## Input extension (kernel 0.0.58)

Call 627 `(handle, bits, 0)` opts in to pointer motion with bit 1, as before.
- Bit 2 adds **secondary presses**: event 12, code 2 (right button), content-local x and y. Pointer
  presses, releases and drags then also carry the Shift, Control and Alt state in `modifiers`.
- Bit 4 adds **function keys**: key extras 14..25 for F1..F12.
- Bit 8 (kernel 0.0.67) adds **middle presses**: event 12, code 3.
- For every client, Ctrl+digit now arrives as the digit (code), like Ctrl+letter already did.
- Older kernels refuse any value but 1 with -22. Clients retry with 1 and work without either.

## Stages

- **HZ1 (released: kernel 0.0.58, zenith-horizon 0.1.0):**
  - the files ABI and the input extension;
  - Horizon with tabs, places and devices, breadcrumb and typed location, back, forward and up;
  - icons and details views, sorting by name, size, modification time or type (folders first);
  - multi-selection with Ctrl and Shift, keyboard operation and a filter for the current folder;
  - the information panel, item counts and free space in the status bar;
  - new folder, rename, permanent delete with confirmation;
  - copy, cut and paste through transfer jobs, with the job strip, Cancel and the shared conflict
    question;
  - a context menu.
  - Checked by host tests (job engine, list and status encoding, Ctrl+digit keys) and
    `tools/horizon-smoke.py` (91 checks, passing locally on 2026-11-15; the distributor run of
    kernel 0.0.58 failed when the spinning `--job` test mode ran out of CPU budget under load, fixed in
    kernel 0.0.59 and zenith-horizon 0.1.1 by command 4): listing, every transfer outcome,
    permissions for another account, a byte-for-byte host comparison of a copied 3 MiB file, and the
    desktop flows by keyboard plus a right click.
  - Icons come from `zenith-icons` (0.3.0 adds Horizon's); without it, Horizon draws simple shapes so
    its icon-only buttons stay usable.
  - Places and the information panel appear from 720 and 860 pixels wide. Since kernel 0.0.66 the desktop
    opens resizable windows sized to the screen (the `windows` setting,
    [opening size](../GRAPHICAL-CLIENTS.md#owned-viewport-extension)), so on a 1920x1080 screen Horizon opens
    with both. HZ5's gate checks Places in a window opened that way; the information panel needs a wider
    screen than the gate's.
- **zenith-horizon 0.1.2 (with kernel 0.0.60):**
  - Horizon draws on the desktop's window material when the desktop offers it: with whole-app transparency
    (Meridian › Appearance › Materials, background) one blurred material covers the window and the content
    and information panels are light tints over it; otherwise Places is a navigation material. Modal dimming
    goes through zui, so material frames stay valid premultiplied pixels (a plain RGB mix had made the desktop
    refuse the frame, which ended Horizon and cancelled its job).
  - The Places/Devices highlight covers the whole row; a device row's usage bar and free space no longer hang
    below it. Devices can show as the open folder; when two entries share a path (Root and the system disk)
    the first one is highlighted.
  - The gate (92 checks) runs its desktop part with whole-app transparency on.
- **HZ2 (released: kernel 0.0.65, zenith-horizon 0.2.0), requested 2026-11-17 ("complete Horizon"):**
  - **Opening files** goes through the Desktop Portal's new `org.zenith.portal.OpenWith` interface
    ([portal](PORTAL.md#orgzenithportalopenwith-1)): Enter or a double click opens a file with the person's
    remembered app, else the app that declares its type (the Editor, Aurora when installed, declares text
    and source types); otherwise the desktop asks which app, with "Always use this app for .EXT files".
    **Open with...** in the menu always asks. Horizon never starts an app itself.
  - **Associations:** apps declare the extensions they open (`OPENS=` in package `.app` files, built-in for the
    Editor); "always" choices are kept per account in `/etc/zenith/preferences/UID.open-with`.
  - **Split view** (F3, the toolbar's two-column button, or the folder menu): two panes per tab, each with its own
    folder, history, view, sort, zoom and selection; Tab switches the active pane, which carries the accent
    outline and takes the toolbar, keys and information panel. A split tab is labelled `Documents | Archive`.
  - **Zoom** per pane: Ctrl+= or Ctrl+keypad plus, Ctrl+- , Ctrl+0, or Ctrl+wheel; icons 32, 48, 64 or 96 pixels
    and detail rows 26 to 48. Kernel 0.0.65 sends Ctrl+minus/equals as characters and wheel events with
    their modifiers.
  - **Drag and drop:** the selection onto a folder, the other pane or another tab's button; a menu then offers
    Move here, Copy here or Cancel (Shift moves and Ctrl copies without asking). Transfers run as jobs.
  - **Properties** (Alt+Enter or the menu): kind, location, size or contents, modification time, owner, group
    and a permission grid that the owner (or root, or an unlocked Horizon) changes through the path call (405).
- **HZ3 (released: kernel 0.0.65, zenith-horizon 0.2.0):**
  - **Trash** in the freedesktop layout under `~/.local/share/Trash` (`files/` and `info/NAME.trashinfo`
    with the original path and deletion date). Delete moves there by rename (instant); Shift+Delete deletes
    for good after asking. Items on another filesystem than the home folder have no Trash: Horizon says so and
    asks before deleting them for good. In the Trash (a place in the sidebar): Restore puts items back
    (making missing folders again, and refusing when the name is taken), Delete permanently, and Empty
    Trash; the information panel shows where an item came from and when it was deleted. The Trash keeps
    at most 1,000 items: the oldest are deleted for good when more go in.
  - **Search below this folder** (Ctrl+Shift+F, then Enter): a bounded walk (up to 2,000 folders and
    4,096 results) a few folders per event-loop pass, so the window stays responsive. Results are relative
    paths (`Notes/a.txt`) in the pane, so they open, copy and delete like any item; Esc ends the search.
    Indexed search is S5.
  - **Removable drives** in Devices wait for [S6](REMOVABLE-STORAGE.md), which is still design-only and has
    decisions that need the operator's sign-off.
- **HZ4 (released: kernel 0.0.65, zenith-horizon 0.2.0):**
  - **Archives as kernel jobs** (operations 4 extract and 5 compress of call 656, with progress and Cancel):
    Extract here unpacks .zip, .tar, .tar.gz/.tgz and .gz into a new folder beside the archive; Compress to
    .zip packs the selection. The kernel has its own DEFLATE decoder and encoder, CRC-32, zip and ustar/GNU/pax
    tar readers and writers. Entry names are checked (no absolute paths, no `..`); links, devices,
    encryption, zip64 and other compression methods are refused per entry; output is limited to 64 GiB.
    Permissions inside archives are restored from HZ5.
  - **Thumbnails** (call 660): PNG (not interlaced), 24/32-bit BMP and baseline JPEG shrink to the icon size in
    the icons view, one image per event-loop pass, cached by size. Large JPEGs decode only the DC value of each
    block. Files over 32 MiB are not previewed.
  - **Tabs at sign-in:** Ctrl+Q (or the last tab closing) writes `~/.config/zenith/horizon-tabs`; Horizon
    started without a folder restores its tabs, splits, views, sorts and zoom, skipping folders that are gone.
  - **Administrator:** after a refusal (a folder or job denied, a permission change refused) the status bar
    offers **Unlock**, which asks the Desktop Portal's Authorization for `org.zenith.files.manage`. While
    granted, the kernel serves this Horizon's listing, transfer, path and metadata calls as root until it
    closes; the title says "(administrator)" and deletions are permanent (the Trash is the person's own).
  - Checked by host tests (portal, privileges, archives against .NET's zip/gzip/tar in both directions,
    thumbnails of Pillow/libjpeg fixtures, job parsing) and `tools/horizon-smoke.py`.
- **HZ5 (local, kernel 0.0.67, zenith-horizon 0.3.0), requested 2026-11-18 ("finish Horizon"):**
  - **Middle click** (input bit 8): on a folder it opens a new tab behind the current one; on a tab it closes it.
  - **Wide layout:** with windows sized to the screen (kernel 0.0.66) Horizon opens with Places, and the
    gate checks it (`HORIZON_LAYOUT WxH places=1 info=0|1` after each resize).
  - **Permissions in archives:** Compress records each file's and folder's mode (zip external attributes from
    "Unix", tar mode field); Extract restores them without setuid, setgid and sticky. Folder modes are set last,
    deepest first, so a read-only folder still receives its files.
  - **Zip64** archives (over 4 GiB or 65,535 entries, from other tools) are read: the zip64 end record and
    locator, and per-entry zip64 sizes and offsets. Compress still writes plain zip and refuses past its limits.
  - **Thumbnails** of interlaced PNG (Adam7) and progressive JPEG (spectral selection and successive
    approximation; large images keep only the DC scans and skip the AC data). Host tests compare each
    progressive fixture with the same image converted losslessly to baseline by jpegtran: identical pixels.
  - **Copies keep permissions** (also moves between filesystems, which copy): each file and new folder gets
    the source's permission bits, never setuid, setgid or sticky; a folder's mode is set after its contents.
  - Still not read: .tar.xz, .tar.bz2, .7z and .rar, encrypted zip entries, and lossless or arithmetic-coded
    JPEG. Removable drives stay deferred (S6).
