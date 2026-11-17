# Aurora — the text editor (U2)

Aurora is Zenith's graphical text and code editor, `zenith-aurora`, built from the
[concept](../design/editor-concept.svg) (after KDE Kate). With the package installed, the
launcher's Editor opens Aurora and shows it as Aurora; without it, the built-in Editor
window stays. Files (and later Horizon's Open with) open files in it the same way.

## AU1 — a dependable editor (local, 2026-11-17)

- **Documents.** Up to 8 documents, each up to 1 MiB and 65,536 lines. Tabs and the
  sidebar's Documents list show the same documents; a dot marks unsaved changes in both.
  The sidebar appears from 820 pixels wide; tabs are always there. Closing the last
  document closes Aurora, like Horizon's last tab.
- **Text.** UTF-8 only: a file with NUL bytes ("binary") or invalid UTF-8 is refused with
  a message, never shown garbled or rewritten. CRLF files are recognised and saved with
  CRLF; a UTF-8 byte-order mark is kept. The status bar toggles LF/CRLF and cycles
  Spaces 4, Spaces 2 and Tabs (files that indent with tabs open in Tabs mode).
- **Editing.** Selection with Shift, the mouse (drag, double-click a word, triple-click
  or the line numbers for a line), word moves with Ctrl, smart Home, Page Up/Down, auto
  indent on Enter, Tab and Shift+Tab indent and outdent selected lines. Typing AltGr
  characters ({ [ ] } on German layouts) works; Ctrl alone is a shortcut.
- **Undo/redo.** Typed text is one step until a newline or another action; replace all is
  one step. The history holds 4,096 steps and 256 KiB per document; older steps drop off.
- **Clipboard.** Ctrl+C, Ctrl+X and Ctrl+V use the session clipboard through calls 639 and
  659 (below), shared with the built-in windows and the terminal.
- **Find and replace** dock above the status bar and never cover code: match count
  ("3 of 7"), case (Aa) and whole word (W), next/previous (Enter, Shift+Enter, F3), replace
  and replace all. Matches are outlined in the text.
- **Go to line** (Ctrl+G), and a status bar with line and column, the selection size, the
  save state, the language, line ends, encoding and indentation.
- **Syntax colours** from the accent palette only: C-family languages (C, C#, C++, Java,
  JavaScript, TypeScript, Rust, Go), Python, shell and configuration files, JSON and
  Markdown. Block comments and Markdown code fences carry across lines.
- **Open and Save as** dialogs list the folder (folders first) and take a name or a full
  path; Save as asks before replacing another file.
- **Saving never truncates.** Aurora writes `.NAME.aurora-save` beside the file, flushes
  it, copies the old file's permissions and renames it over the file. Out of space,
  permission or a read-only disk leaves the old file unchanged, removes the temporary
  file and keeps the text in Aurora, with the reason.
- **Changes on disk.** When Aurora gets focus back, a file another program changed shows a
  banner (Reload or Keep); a deleted file says saving creates it again. Saving over a
  changed file asks first.
- **Unsaved changes.** Closing a document or the window asks Save, Don't save or Cancel for
  each changed document; the taskbar knows the window has unsaved work (call 604).

Keys: Ctrl+N, O, S, Shift+S, W, Z, Y (or Shift+Z), A, C, X, V, F, H, G; Ctrl+Tab and Ctrl+1..8
switch documents. The menu button (top right) and a right click in the text offer the same.

### Materials

The window uses the desktop's material where the concept puts chrome: the whole window
when the person chose transparent app backgrounds (Meridian › Appearance › Materials), else
the sidebar as navigation material. Code is always on an opaque surface ("effects stay
off over code"). All blending goes through zui, so material frames stay valid premultiplied
pixels (the Horizon 0.1.2 lesson). Two guards keep it that way:

- `zui_material_repair` applies the desktop's own acceptance rule
  (`ProcessMaterials.ValidForeground`: every channel at most alpha; nothing translucent
  outside a declared material region) to each frame before it is presented. A pixel that
  breaks it is repaired and Aurora logs `AURORA_FRAME_REPAIRED`, instead of the desktop
  refusing the frame and the program ending.
- The host test draws every step of its flows in three modes (opaque, navigation material,
  whole-window material) at 640x480 and 1200x800 and checks each frame against that rule;
  it requires that no frame needs repairing. The guest gate runs with whole-app
  transparency on and fails on `AURORA_FRAME_REPAIRED`.

### Clipboard calls (kernel 0.0.64, `zenith-native-clipboard-abi` 1.0)

- **639** `(handle, text, 1..1 MiB)` sets the session clipboard to UTF-8 text, only from
  the window that has the keyboard (`-1` otherwise, `-22` for invalid UTF-8).
- **659** `(handle, buffer, 1..1 MiB)` returns the clipboard's UTF-8 bytes once for each
  Ctrl+V the person pressed in that window. Any other key, or losing focus, withdraws it:
  without the key `-1`, an empty clipboard `-61`, a larger clipboard than the buffer `-27`.
  So no program reads the clipboard behind the person's back.

### Code and checks

[`userland/aurora`](../../userland/aurora): `buffer.c` (gap buffer, line index, undo,
search, UTF-8 and CRLF), `editor.c` (documents, drawing, input, dialogs, saving) behind the
`au_sys_*` calls of `aurora.h`, and `main.c` (those calls over the native ABI, the window
loop). Kernel: `ProcessGraphics.CopyText/PasteText`, `ProcessSurface.Focused/KeyDelivered`,
`NativeClientHost.OnKey`, the 639/659 routes in `src/Native/x64/Process.s`.

- `tools/test_ui_library.py` (`test_aurora_with_sanitizers`): the text engine against a
  reference model (300 random edits, undo to the bounded history's start, redo), search,
  columns, UTF-8 checks, CRLF and BOM round trips; then the editor over a temporary folder:
  go to line, typing, undo/redo, select all, copy and paste, find, replace, save with CRLF,
  a save that runs out of space (the file unchanged, no temporary left), a file changed on
  disk (banner, and saving asks), a refused binary file, Save as of a new document, and
  quitting with unsaved changes. Every frame is checked as above. The program must fit the
  package file limit with 32 KiB to spare.
- `ProcessGraphicsTests.ClipboardCopiesFromTheFocusedWindowAndPastesOncePerPasteKey`.
- `tools/aurora-smoke.py` (release gate `t`): the signed package installs and requires the
  clipboard ABI; on the desktop with whole-app transparency, a CRLF C# file opens on the
  window material, go to line, typing and saving, copy and paste through 639/659, undo,
  find and replace, Save as, a refused binary file, and the unsaved-close prompt; the host
  reads both saved files from the disk (CRLF kept, edits present).

## Next

- **AU2:** Project (the document's folder as a tree) and Outline in the sidebar, the
  minimap, horizontal scroll bar, overwrite mode, bracket matching, regular expressions
  (`.*`) in find, and Open with from Horizon (HZ2).
- **AU3:** crash-recovery copies with a per-user privacy, cleanup and reboot policy;
  external-change detection without a focus change.
- Later: compiler output, diagnostics and language-server features once process/IPC and
  toolchain support exist. Aurora is not an IDE promise.
