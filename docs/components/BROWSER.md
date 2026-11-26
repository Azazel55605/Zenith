# Nebula (browser)

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md) · [Concept](../design/browser-concept.svg) · [Application scope](../APPLICATIONS.md#netsurf-browser)

Nebula is Zenith's browser. Its About credits the engine as "powered by NetSurf". The concept sheet describes the target design. Stages B1–B6 expand the optional browser entry (X1/X5) in the master roadmap. Regenerate the sheet with `python3 tools/design/browser-concept.py`.

## Current port

The tagged `zenith-netsurf/v0.1.0` package supplies Nebula's native window, built on NetSurf 3.11. Kernel 0.0.81 limits that version to six connection attempts per process. The updated browser requires `zenith-native-browser-network-abi` 1.0 and the standard `zenith-icons` package for its navigation and page-security controls. A live interactive window can keep opening connections as it browses. Requests run one at a time, with an 8 MiB response limit.

In an installed QEMU guest, the same HTTP page displayed four of its twelve external images on kernel 0.0.81 and all twelve on the development build. The browser then followed a verified HTTPS link in the same window. A separate public-site check on Google’s German homepage showed the logo, search field and accented button text. Submitting a search in that guest led to Google’s `/httpservice/retry/enablejs` page. JavaScript is disabled in this build. NetSurf’s CSS support also limits how closely some pages match their intended layout.

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
- Connection attempts in the current session.
- How long cookies are kept.
- **Load again**.

**Honest states.** Each limit is reported once, where it happens, with the one action that helps:
- **Untrusted certificate:** an error page with Go back and Details. There is no override in the first stage.
- **Link to a file:** the engine can't show the file, so a card offers Copy address. With N3 the card becomes Save to Downloads.
- **Find on page (Ctrl+F):** a bar that slides up over the bottom of the page and shows the match count.

**Start page.** A packaged HTML page styled with the Zenith theme passed in as CSS. It shows a greeting, bookmark tiles and recent pages.

## Implemented browser controls

The frontend draws the window controls in `userland/netsurf/frontend.c`. The host frontend tests cover navigation, menus, address input, find, zoom and page rendering.

- **Toolbar:** Back, Forward and Reload/Stop as icon buttons. The address field shows "Not secure" for HTTP, the host in bold and the path quieter. Ctrl+L or a click shows the whole address, selected. The ellipsis button opens the menu.
- **Loading and links:** a 3 px accent segment travels under the toolbar while a page loads. A pill at the bottom left shows a hovered link's address or a loading message. It clears when the pointer leaves the page, and the core's "Done" message is not shown.
- **Menu:** Zoom (−, value, +; Ctrl+Plus, Ctrl+Minus, Ctrl+0), Find on page, Copy page address, Page info, Using Nebula (the packaged guide).
- **Page info:** the displayed document’s connection security, host, script support, connection attempts, zoom and Copy address. A failed HTTPS navigation never earns a verified lock.
- **Find bar (Ctrl+F):** searches as you type, highlighting every match. Enter and Shift+Enter go to the next and previous match, and Esc closes the bar. It says "No matches" when there are none. The core reports no count.
- **Navigation menus:** right-click Back or Forward for recent entries in that direction. Right-click a link to open it or copy its address.
- **Bookmarks and history:** the address-field star and Ctrl+D toggle a bookmark for the current document. Alt+Home or the browser menu opens the private start page. The address field offers local matches from the user’s bookmarks and recent pages as they type. Each account can keep 24 bookmarks and its 24 most recent distinct pages under `~/.nebula`. Stored links exclude URLs with embedded credentials. The internal page rejects requests from external pages and embedded resources.
- **File card:** a link the engine cannot show opens a card with the file's name, type and size, plus Copy address and Close. The transfer stops at its first data.
- **Copying:** Ctrl+C copies the selected page text, and Copy address copies the address. Both go through the kernel clipboard.

Differences from the concept:
- Page info leaves out cookies.
- Find has no match count.
- Error pages are still the engine's. Page source, a second window, tabs and saving downloads remain open.

The window may open at 640×480 at most (kernel limit) and grows after. The kernel refuses a program file that differs from its installed package, so test each build by installing it.

## Stages

| ID | Content | Depends on |
|---|---|---|
| B1 | Chrome: icon toolbar, host/path address with lock, hover pill, progress line, material band | zui only |
| B2 | States: error pages from packaged HTML, connection security, file-link card, Page info | B1 |
| B3 | Menu, find bar, zoom, page source; link menu (Open, Open in new window, Copy address) | B1 |
| B4 | Per-user bookmarks and history; start page and suggestions use them | B3 |
| B5 | New window as a second browser process; decide whether to add tabs | kernel |
| B6 | Save to Downloads and a downloads list | N3 |

## Open decisions

- Windows only, or tabs with independent navigation and focus.
- A default search engine, or addresses only.
- Keep cookies between sessions, or only while the window is open.
- Certificate override: never, or behind Details.
