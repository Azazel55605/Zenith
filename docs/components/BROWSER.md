# Nebula (browser)

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md) · [Concept](../design/browser-concept.svg) · [Application scope](../APPLICATIONS.md#netsurf-browser)

Nebula is the chosen name (2026-11-26) for Zenith's browser, the frontend around the NetSurf engine. Its About credits the engine as "powered by NetSurf". The concept is a proposal and has not been accepted. Its stages B1–B6 refine the NetSurf entry (X1/X5) in the roadmap and are not a separate schedule. Regenerate the sheet with `python3 tools/design/browser-concept.py`.

## Starting point

The first port (`userland/netsurf/frontend.c`, package `zenith-netsurf` 0.1.0) has:

- a 52 px zui toolbar with **Back**, **Forward** and **Reload/Stop** as text buttons;
- an address field with Ctrl+L, Ctrl+R and Ctrl+Q;
- a permanent 24 px status line;
- one window with in-memory history and cache;
- no JavaScript.

Kernel 0.0.81 allows one live connection and six connection attempts per browser run. Pages and their images share that budget. Zenith has no download service yet (N3).

## The proposal

The engine draws the page with the site's own CSS. Zenith draws everything around the page.

**The chrome band**
- One band continues the title row. With transparency on it is a material navigation surface; the page itself stays opaque.
- Back, Forward and Reload are 36 px icon buttons. Reload becomes Stop while a page loads.
- A secondary click on Back or Forward lists that window's history.

**The address field**
- Shows the host in full text and the path in quieter text.
- The lock sums up security and opens Page info.
- Focus (Ctrl+L or a click) shows the whole address, selected.
- Suggestions come from bookmarks and history, followed by "Go to …" and "Open a local file…". Enter navigates and Esc restores the address.
- An address without a scheme becomes `https://`. There is no default search engine.

**Bookmarks, menu and status**
- A star in the field shows and toggles the bookmark (Ctrl+D).
- One menu holds New window, Zoom (− 100 % +), Find on page, History, Bookmarks, Page source and About.
- A link's address appears in a hover pill at the bottom left. A 3 px accent line under the toolbar shows loading progress. The status line goes away, so the page gains 24 px.

**Page info**
- Connection security and certificate.
- Scripts: not supported.
- Images loaded out of the total.
- Connections used out of the budget.
- How long cookies are kept.
- **Load again**.

**Honest states.** Each limit is reported once, where it happens, with the one action that helps:
- **Connection limit:** a bar under the toolbar saying how many images were skipped, with Load again.
- **Untrusted certificate:** an error page with Go back and Details. There is no override in the first stage.
- **Link to a file:** the engine can't show the file, so a card offers Copy address. With N3 the card becomes Save to Downloads.
- **Find on page (Ctrl+F):** a bar that slides up over the bottom of the page and shows the match count.

**Start page.** A packaged HTML page styled with the Zenith theme passed in as CSS. It shows a greeting, bookmark tiles and recent pages.

## Implemented (B1–B3, not yet released)

`userland/netsurf/frontend.c` draws the chrome; `tools/netsurf/test-frontend.py` checks find, zoom, the limit bar and the pill on the host. A disposable installed VM showed each state below with the material toolbar.

- **Toolbar:** Back, Forward and Reload/Stop as icon buttons. The address field shows "Not secure" for HTTP, the host in bold and the path quieter. Ctrl+L or a click shows the whole address, selected. The ellipsis button opens the menu.
- **Loading and links:** a 3 px accent segment travels under the toolbar while a page loads. A pill at the bottom left shows a hovered link's address or a loading message. It clears when the pointer leaves the page, and the core's "Done" message is not shown.
- **Menu:** Zoom (−, value, +; Ctrl+Plus, Ctrl+Minus, Ctrl+0), Find on page, Copy page address, Page info, Using Nebula (the packaged guide).
- **Page info:** what kind of page it is, the host, scripts (not supported), connections used out of 6, requests not loaded, zoom, and Copy address.
- **Find bar (Ctrl+F):** searches as you type, highlighting every match. Enter and Shift+Enter go to the next and previous match, and Esc closes the bar. It says "No matches" when there are none. The core reports no count.
- **Limit bar:** once the kernel refuses a request, a bar under the toolbar says how many requests were not made. It tells you to restart Nebula, because the budget belongs to the run and Load again cannot help. The page moves down under it, and × closes it until more requests are refused.
- **File card:** a link the engine cannot show opens a card with the file's name, type and size, plus Copy address and Close. The transfer stops at its first data.
- **Copying:** Ctrl+C copies the selected page text, and Copy address copies the address. Both go through the kernel clipboard.

Differences from the concept:
- No bookmark star yet (B4).
- No history list on a secondary click of Back or Forward.
- Page info shows requests not loaded, not images out of a total.
- Page info leaves out cookies.
- The limit bar asks for a restart instead of offering Load again.
- Find has no match count.
- Error pages are still the engine's.

The window may open at 640×480 at most (kernel limit) and grows after. The kernel refuses a program file that differs from its installed package, so test each build by installing it.

## Stages

| ID | Content | Depends on |
|---|---|---|
| B1 | Chrome: icon toolbar, host/path address with lock, hover pill, progress line, material band | zui only |
| B2 | States: error pages from packaged HTML, connection-limit bar, file-link card, Page info | B1 |
| B3 | Menu, find bar, zoom, page source; link menu (Open, Open in new window, Copy address) | B1 |
| B4 | Per-user bookmarks and history; start page and suggestions use them | B3 |
| B5 | New window as a second browser process; tabs only after a larger kernel connection budget | kernel |
| B6 | Save to Downloads and a downloads list | N3 |

## Open decisions

- Windows only, or tabs once the connection budget allows them.
- A default search engine, or addresses only.
- Keep cookies between sessions, or only while the window is open.
- Certificate override: never, or behind Details.
