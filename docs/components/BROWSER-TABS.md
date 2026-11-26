# Nebula tabs (plan)

[Nebula](BROWSER.md) · [Tabs concept](../design/browser-tabs-concept.svg) · [First concept](../design/browser-concept.svg) · [Master roadmap](../ROADMAP.md)

This plan answers the B5 question "a second window, or tabs" (requested 2026-11-26): tabs. It has not been accepted yet. Regenerate the sheet with `python3 tools/design/browser-tabs-concept.py`.

## Implemented (not yet released)

`userland/netsurf/frontend.c` implements T0–T3 and most of T4.

**State and layout**
- The process has one `struct nebula_window` holding up to 16 tabs; each tab is a `struct gui_window`.
- The page renders into its own buffer only when the engine asks. Every frame lays out the workspace, the sidebar and the toolbar, then copies the page into the inset, keeping its rounded lower corners.
- The window opens at 640×480 with the rail. A window 820 px or wider shows the full sidebar, and Ctrl+Shift+S or the menu hides it.

**Tabs**
- New tab, close, switch, and all the keys in the table below.
- Middle-click a link to open it in a background tab. Ctrl+click and "Open in new tab" in the link menu do the same, and `target=_blank` opens a foreground tab.
- Middle-click a row to close it. Drag rows to reorder them. Right-click a row for the tab menu.
- The last 8 closed tabs can be reopened.
- Past 8 live tabs, the least recently used one sleeps and wakes at its address and scroll position.

**Testing**
- `tools/netsurf/test-frontend.py` covers creating, switching and closing tabs, reopening, the order, sleeping, the 16-tab cap, the rail and the hidden sidebar. It also runs the real-engine navigation through the inset.
- A disposable VM showed the rail, the full sidebar, the hidden sidebar, background loading from a middle-click, the tab menu, close, reopen and reordering.

**Not done**
- Active-tab-first fetch order.
- Session restore.
- Favicons: tiles show the host's initial.
- The "Could not load" row state: the frontend has no reliable signal for a failed load.
- Tile tooltips in the rail.

## Layout

Nebula takes the layout every Zenith app uses (Orbit, Beacon, Meridian). `zui_workspace_begin` gives the frame:
- a 12 px margin;
- a sidebar on the base material, continuing the title row;
- the content inset 24 px right of the sidebar, at radius 12.

**The sidebar**
- **New tab** (Ctrl+T) at the top.
- **Tabs · n**: one 46 px navigation row per tab, showing the site tile (favicon or initial), the page title, and the host underneath.
- **Bookmarks** and **History** at the bottom. They open those sections of the start page (B4) in the current tab.

**The inset**
- The toolbar from the first concept moves into the top row of the inset: Back, Forward, Reload/Stop, the address with the lock and star, and the menu.
- The page fills the rest of the inset, and the inset rounds its lower corners.
- The find bar, the link pill and the cards stay inside the inset.
- The 3 px loading line runs along the toolbar's lower edge.

**Row states**
- **Active:** accent tint with an accent bar, and the close mark visible.
- **Under the pointer:** a raised fill and the close mark.
- **Loading:** an accent arc replaces the tile, also for tabs in the background.
- **Sleeping:** a dimmed tile and "Sleeping · host".
- **Failed:** "Could not load" in the danger colour.

**Narrow windows**
- Under 820 px the sidebar folds to a rail of tiles. The rail is 56 px wide; the inset still starts 24 px after it.
- A tile's tooltip names the page.
- Ctrl+Shift+S, or the menu's Sidebar item, hides the sidebar entirely. The inset then spans the window, as `zui_workspace_begin(ui, 0)` does.

## Behaviour

| Action | Result |
|---|---|
| Ctrl+T, New tab | A tab after the current one with the start page, address focused |
| Ctrl+W, × on a row, middle-click a row | Closes it; the tab to its right (else left) becomes active. The last tab closes the window |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab, wrapping |
| Ctrl+1…8 / Ctrl+9 | That tab / the last tab |
| Ctrl+Shift+T | Reopens the last closed tab with its history position. The last 8 closed are kept and listed on the start page as "Recently closed" |
| Middle-click or Ctrl+click a link; link menu "Open in new tab" | A background tab right after the current one, after earlier ones opened from it |
| A link with `target=_blank` | The same, in the foreground (`nsoption target_blank` on) |
| Drag a row | Reorders the tabs |
| Right-click a row | New tab below, Duplicate, Reload, Put to sleep, Close, Close other tabs, Reopen closed tab |

The window title follows the active tab ("Page title — Nebula").

## Engine and state

The NetSurf core already supports several browser windows. It calls `gui_window_table.create(bw, existing, flags)` with `GW_CREATE_TAB` and `GW_CREATE_FOREGROUND`, and `browser_window_create` accepts `BW_CREATE_TAB | BW_CREATE_FOREGROUND`. Today `frontend.c` has one static `struct gui_window` holding both the window and the page.

**T0** splits that state in two:
- **`struct nebula_window`**, once per process:
  - the zui, the handle, the materials and the popovers;
  - the profile (bookmarks, history), the suggestions and the clipboard;
  - the tab array (at most 16) and its order, the active index, the closed-tab ring and the sidebar mode.
- **`struct gui_window`**, one per tab:
  - the `browser_window`, the URL, the title, the status and the loading flag;
  - scroll and extent, the caret, the zoom, the find text and state, and the file card;
  - the address being edited, and the tile (favicon bitmap or initial and colour);
  - its last-used time and whether it sleeps.

Core callbacks address a tab:
- `invalidate`, `set_scroll` and `place_caret` on a background tab only update that tab's state. Only the active tab is drawn.
- `get_dimensions` reports the inset's page size for every tab, so a tab that becomes active needs no reformat when the window size has not changed.
- `set_title`, `set_url`, `set_icon` and the throbber events redraw the tab's row, and the window title when the tab is active.

**Sleeping**
- A sleeping tab keeps its `gui_window` and its URL, and records its scroll position in its local history.
- Its `browser_window` is destroyed. Selecting the tab creates a new one at the saved URL and restores the scroll once the content arrives.
- The core cannot save a tab's back/forward list, so a tab woken from sleep starts with a one-entry history. Rows say "Sleeping" so this is not a surprise.

## Network and memory

**Network**
- `zenith-native-browser-network-abi` 1.0 lets a live interactive window open sockets throughout its session, one at a time.
- `userland/netsurf/fetch.c` runs one fetch and leaves the rest queued in the core. Tabs therefore share one socket.
- T4 moves the active tab's queued fetches ahead of background ones when a fetch slot opens. The core does not record which window a fetch belongs to, so T4 starts by measuring whether its queue can be reordered, or the fetcher has to pick from it.
- A second live socket per process is a kernel question for later. Do not count on it.

**Memory**
- At most 16 tabs.
- Past 8 live tabs, the least recently used one sleeps.
- Native programs get no memory-pressure signal today. If one is added, every background tab sleeps when it fires. Until then, the 8-live-tab rule is the only limit.
- T0 measures one tab's memory (layout plus decoded images) on the fixture pages to confirm these numbers.

## Stages

| ID | Content | Exit check |
|---|---|---|
| T0 | Window/tab split; workspace layout with the browser in the inset; one tab still | Host fixtures pass unchanged. The guest smoke test still renders local, HTTP and HTTPS pages inside the inset. Materials valid |
| T1 | Sidebar tabs: New tab, close, switch, Ctrl+T/W/Tab/1–9, rows with title, host and loading arc, per-tab scroll, zoom, find and address | Host fixture: 3 tabs, switch keeps each tab's scroll and find. Guest: open, switch and close by keyboard and pointer |
| T2 | Open in a new tab: middle-click, Ctrl+click, link menu item, `target=_blank`; background loading | Guest: a background tab finishes loading while another is shown; its row shows the arc, then its title |
| T3 | Reorder by drag, Duplicate, Close other tabs, Reopen closed (8), rail under 820 px, hide the sidebar, favicons as tiles | Host fixture for the order and closed-tab ring; guest screenshot of rail mode |
| T4 | Sleeping tabs, the 16-tab cap, active-first fetch order, optional session restore (`~/.nebula/session`, 16 URLs) | Guest: open 10 tabs, verify 2 sleep and wake at their position. Fetch order test on the host |

Not in scope: tab groups, pinned tabs, tabs moved between windows, and private tabs.

## Open decisions

- Restore the last session at start: always, ask once, or never. The plan offers it as a menu setting, off by default.
- Whether closing the last tab closes the window (the plan) or leaves an empty start page.
- Favicons: NetSurf fetches them with `set_icon`, which uses the one shared socket. Fetch them only for the active tab, or not at all, and use initials.
