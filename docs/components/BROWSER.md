# Nebula (browser)

[Master roadmap](../ROADMAP.md) · [Documentation](../README.md) · [Concept](../design/browser-concept.svg) · [Application scope](../APPLICATIONS.md#netsurf-browser)

Nebula is Zenith's browser. Its About credits the engine as "powered by NetSurf". The concept sheet describes the target design. Stages B1–B6 expand the optional browser entry (X1/X5) in the master roadmap. Regenerate the sheet with `python3 tools/design/browser-concept.py`.

## Current port

The `zenith-netsurf/v0.2.0` source tag adds sidebar tabs and per-user bookmarks and history to the NetSurf 3.11 port. The matching kernel tag is `kernel/v0.0.82`. The browser requires `zenith-native-browser-network-abi` 1.0 and the standard `zenith-icons` package for its navigation and page-security controls. A live interactive window can keep opening connections as it browses. Requests run one at a time, with an 8 MiB response limit.

In an installed QEMU guest, the same HTTP page displayed four of its twelve external images on kernel 0.0.81 and all twelve on the development build. The browser then followed a verified HTTPS link in the same window. A separate public-site check on Google’s German homepage showed the logo, search field and accented button text. Submitting a search in that guest led to Google’s `/httpservice/retry/enablejs` page. That measurement used the script-disabled 0.2.0 browser. NetSurf’s CSS support also limits how closely some pages match their intended layout.

## Design

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
- Script permission for the displayed document, including site policy restrictions.
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
- **Menu:** Zoom (−, value, +; Ctrl+Plus, Ctrl+Minus, Ctrl+0), Find on page, Copy page address, Page info, Using Nebula (the packaged guide), and a per-tab JavaScript permission for the displayed page.
- **Page info:** the displayed document’s connection security, host, script support, connection attempts, zoom and Copy address. A failed HTTPS navigation never earns a verified lock.
- **Find bar (Ctrl+F):** searches as you type, highlighting every match. Enter and Shift+Enter go to the next and previous match, and Esc closes the bar. It says "No matches" when there are none. The core reports no count.
- **Navigation menus:** right-click Back or Forward for recent entries in that direction. Right-click a link to open it or copy its address.
- **Bookmarks and history:** the address-field star and Ctrl+D toggle a bookmark for the current document. Alt+Home or the browser menu opens the private start page. The address field offers local matches from the user’s bookmarks and recent pages as they type. Each account can keep 24 bookmarks and its 24 most recent distinct pages under `~/.nebula`. Stored links exclude URLs with embedded credentials. The internal page rejects requests from external pages and embedded resources.
- **File card:** a link the engine cannot show opens a card with the file's name, type and size, plus Copy address and Close. The transfer stops at its first data.
- **Copying:** Ctrl+C copies the selected page text, and Copy address copies the address. Both go through the kernel clipboard.

Differences from the concept:
- Page info leaves out cookies.
- Find has no match count.
- Error pages use the engine’s templates. Saving downloads remains open under B6/N3.

The window opens at 640×480 with the tab rail. At 820 px wide it shows the full sidebar. The kernel refuses a program file that differs from its installed package, so test each build by installing it.

## Stages

| ID | Content | Depends on |
|---|---|---|
| B1 | Chrome: icon toolbar, host/path address with lock, hover pill, progress line, material band | zui only |
| B2 | States: error pages from packaged HTML, connection security, file-link card, Page info | B1 |
| B3 | Menu, find bar, zoom, page source; link menu (Open, Open in new window, Copy address) | B1 |
| B4 | Per-user bookmarks and history; start page and suggestions use them | B3 |
| B5 | Sidebar tabs with independent navigation, focus and scroll. Remaining work is listed in the [tab plan](BROWSER-TABS.md). | B1, B4 |
| B6 | Save to Downloads and a downloads list | N3 |

## Page compatibility after 0.2.0

Nebula 0.2.1 displays JPEG images and sizes the document to the available page inset as the sidebar changes. Ctrl+U or **View page source** displays the cached text of the active document. Escape returns to the page at its previous scroll position. The browser keeps its private start page and internal error documents out of the source viewer.

The signed candidate rendered twelve PNGs and one JPEG from the same HTTP page in an installed QEMU guest. Opening the source displayed an HTML comment that was invisible on the page. Returning to the page issued no new requests. HTTPS navigation, saved bookmarks across restart and the independent ext2 audit passed.

### Responsive layouts

Hiding the rail, resizing the window or zooming can cross a stylesheet breakpoint. Nebula updates the open document and its live background tabs at their new viewport sizes. The browser retains form edits, caret position, decoded images and navigation history. Find refreshes its matches against the visible text. A sheet can express conditions in `@media`, an HTML `media` attribute or nested `@supports` groups. Viewport width, height, orientation and aspect ratio use CSS units.

Grid supports explicit and implicit tracks, fractional sizes, percentages, `minmax()`, `fit-content()`, fixed and automatic repetition, named lines and areas, spans, gaps, row/column auto-placement and dense packing. `place-content`, `place-items` and `place-self` set alignment on both axes. Flexbox handles wrapping, gaps, reverse directions and main/cross-axis alignment. CSS custom properties inherit through the document and can supply colors and track lists. Widths, their minimums and maximums, and Grid tracks can mix lengths and percentages with `calc()`, `min()`, `max()` and `clamp()`. RGB, HSL and hex colors retain alpha when painted over other content.

The host rendering tests compare exact item positions and sizes and exercise repeated breakpoint crossings with an edited form. At 90 DPI, both `240px` and `calc(120px + 120px)` paint 225 device pixels. At 144 DPI both paint 360. The published MDN mobile-first example showed one, two and three cards per row as the same page moved through 400, 920 and 1440 CSS pixels, then returned to one at 400. To repeat this check, save the unmodified HTML and pass its local path to `python3 -B tools/netsurf/test-frontend.py --mdn-responsive-page PATH`. [MDN responsive example](https://github.com/mdn/css-examples/blob/main/learn/media-queries/grid.html)

A second host check uses the published Simple.css stylesheet unchanged on an article. Its full-width header picks up the inherited theme color. The article’s middle Grid track measures 360 px in a 400 px viewport and 720 px in a 1200 px viewport with a 16 px root font. A local copy of the full Simple.css demo, with both of its stylesheets, produced the same measurements when resized in both directions. Pass the downloaded main sheet to the fixture runner with `--simplecss-stylesheet PATH`. [Simple.css demo](https://simplecss.org/demo/) · [Stylesheet](https://cdn.simplecss.org/simple.css)

The installed QEMU smoke test types into an HTTP form, hides and shows the rail, maximizes and restores the native window, counts the rendered Grid columns and submits the original draft. It also opens Find, zooms across a breakpoint and resets zoom while measuring the size of a `rem` element. The fixture server checks that these actions issue no new HTTP requests.

### Compatibility work still open

Subgrid, container queries, vertical-writing Grid layout, CSS transforms, rounded borders and box shadows remain open. CSS math outside the supported dimensions needs separate layout integration. Broader JavaScript compatibility on public sites is tracked below.

### Opt-in JavaScript

Nebula 0.3.0 uses Duktape 2.7.0 and NetSurf’s DOM bindings. A new tab starts with scripts off. **Allow JavaScript and reload** grants either the exact local file or an HTTP(S) origin (scheme, hostname and port) in that tab. A new origin needs its own grant. Closing the tab forgets the permission. Navigation creates a fresh global environment, and closing or leaving a document cancels its timers. **Block JavaScript and reload** revokes the grant and reloads the page. Page info shows whether the displayed document can run scripts.

Open **Using Nebula → click counter** to compare the same page with scripting off and on. The command-line equivalent is `native run netsurf --javascript /usr/share/zenith/netsurf/javascript.html`. A URL can replace the path. The existing Zenith certificate verifier handles HTTPS page and script downloads.

The current target is classic inline and external scripts, including blocking, deferred and async loading, DOM events and text/class changes that update layout and paint. Each callback has a 500 ms execution budget. Each document can allocate up to 4 MiB in its interpreter, with a 10 MiB process-wide interpreter budget.

In the host frontend, the unchanged Selenium JavaScript example handled a click on its child paragraph, removed the paragraph and left its sibling visible. Find and the rendered pixels both reflected the change. This test replayed the published HTML and image from the original URL. [Selenium example](https://www.selenium.dev/selenium/web/javascriptPage.html)

The signed QEMU candidate ran the packaged local click counter, interrupted a looping handler, and ran HTTP and verified HTTPS scripts with independent permissions. On the live Google homepage it rendered the logo and navigation. A direct search still opened Google’s enable-JavaScript interstitial.

An enforced Content Security Policy keeps scripts off for the document until Nebula can apply individual directives. HTTPS pages skip HTTP scripts. Script requests that require subresource integrity verification or cross-origin CORS checks are skipped. Dynamically inserted scripts, modules, JavaScript network APIs and persistent browser storage remain open. Compatibility on framework-driven sites needs further work.

## Open decisions

- A default search engine, or addresses only.
- Keep cookies between sessions, or only while the window is open.
- Certificate override: never, or behind Details.
