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
- **Open in new tab** on a folder (context menu, Ctrl+Enter) opens it behind the current tab. Middle
  click does the same once the desktop sends middle-button presses.
- A tab that shows a split view is labelled with both folders (`Documents | Archive`).
- Tabs are not saved between sessions yet. Restoring them is part of HZ4.

## The boundary

The kernel owns every rule; Horizon shows and asks.
- Listing, free space and transfers go through **zenith-native-files-abi 1.0** below. Ordinary
  native file calls are limited to 1 MiB of reads and 4096 calls a minute. That is enough for
  `cat`, not for copying a folder of photos.
- Rename, new folder and single-item permanent deletes use the existing path call (405).
- Everything runs with the person's own identity and the ordinary VFS checks: search permission on
  ancestors, write permission on the parent, the sticky bit, read-only mounts.
- Horizon never becomes root. Changing files that belong to someone else is a later Desktop Portal
  interface, not a reason to elevate the whole app.

## Native ABI: `zenith-native-files-abi` 1.0 (kernel 0.0.58)

[Header](../../userland/include/zenith_files.h). Older kernels answer -38. Horizon then says it
needs a newer Zenith.

| Call | Arguments | Result |
|---|---|---|
| 654 list | request (1040 bytes), 1040, page buffer (32768 bytes) | One page of a directory: entries with name, size, mode, owner, group, modification time, links, and for folders the number of items in them. Returns the count in this page. |
| 655 filesystem | path, path bytes, 128-byte buffer | The filesystem holding the path: total and available bytes, block size, read-only flag, mount point and type. |
| 656 start a transfer | request, request bytes (24..8192), 0 | Copy, move or delete up to 64 items. Returns a job id. |
| 657 transfer status | job id, 512-byte buffer, 512 | The job's state, counts, bytes, current item, message and any question. Returns the state. |
| 658 transfer control | job id, command, value | Cancel, answer a question, or forget a finished job. |

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

## Input extension (kernel 0.0.58)

Call 627 `(handle, bits, 0)` opts in to pointer motion with bit 1, as before.
- Bit 2 adds **secondary presses**: event 12, code 2 (right button), content-local x and y. Pointer
  presses, releases and drags then also carry the Shift, Control and Alt state in `modifiers`.
- Bit 4 adds **function keys**: key extras 14..25 for F1..F12.
- For every client, Ctrl+digit now arrives as the digit (code), like Ctrl+letter already did.
- Older kernels refuse any value but 1 with -22. Clients retry with 1 and work without either.

## Stages

- **HZ1 (local, kernel 0.0.58, zenith-horizon 0.1.0):**
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
    `tools/horizon-smoke.py` (91 checks, passing locally on 2026-11-15): listing, every transfer outcome,
    permissions for another account, a byte-for-byte host comparison of a copied 3 MiB file, and the
    desktop flows by keyboard plus a right click.
  - Icons come from `zenith-icons` (0.3.0 adds Horizon's); without it, Horizon draws simple shapes so
    its icon-only buttons stay usable.
  - New windows open at 640x480 (the desktop's limit for a new surface). Places and the information
    panel appear from 720 and 860 pixels wide, after a resize or maximize; the gate does not yet
    capture that wide layout. A client-requested opening size is a desktop change for later.
- **HZ2:**
  - split view;
  - zoom;
  - drag and drop between panes and tabs;
  - Properties with permissions and owner for files you own;
  - opening files with an app through an `OpenWith` Desktop Portal interface (Aurora/Editor for
    text);
  - associations.
- **HZ3:**
  - Trash, with a per-user location, capacity limit and restore;
  - removable drives in Devices ([S6](REMOVABLE-STORAGE.md));
  - search below the current folder (bounded), then indexed search ([S5](../ROADMAP.md)).
- **HZ4:**
  - archives;
  - thumbnails;
  - tabs and windows restored at sign-in;
  - administrator file operations through a Desktop Portal interface.
