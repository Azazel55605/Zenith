# Orbit — software, as a plan (U12)

[Master roadmap](../ROADMAP.md) · [Applications](../APPLICATIONS.md) · [Packages](PACKAGES.md)

Work IDs below are detailed steps of the master tracker (U12), not a separate schedule.

Orbit is Zenith's graphical package and update manager, `zenith-orbit`, built from the approved
[concept](../design/orbit-concept.svg). Requested 2026-11-19 as the last of the concept apps. Orbit is a front end to
`pkg`: the same signatures, planner, transactions and kernel staging, and no second database.

## O1 — plan, details, history and sources (local, kernel 0.0.70, zenith-orbit 0.1.0)

- **A plan, not a button.** You tick updates, add packages from Browse or a package page, and mark installed
  packages for removal. The plan panel shows each change, what else is needed (dependencies the planner pulls in),
  that every signature is verified, the download (archives not yet cached), the disk space it uses or frees and what
  stays free, whether a restart is needed (a new kernel), what happens if it fails, and the exact `pkg` commands.
  Apply runs the data changes as one transaction (installs, upgrades and removals together: `PackageManager.Change`),
  then stages a kernel when one is chosen. Nothing changes until all of it is ready; a failed apply changes nothing.
  Updates start in the plan; take out what you do not want. Narrow windows show the plan as a bar and its own page.
- **Overview:** up to date or how many updates, when the lists were checked and from which source and generation,
  Check for updates, installed packages, the running kernel (and a pending restart), the signed sources and key,
  free space, and the recent changes.
- **Updates:** each update with the versions; the kernel is marked Restart. **Not offered** lists every package
  whose newest version cannot be installed, with the reason in words ("0.4.0 needs a newer kernel than this one
  (zenith-native-tomorrow-abi 1.0)", "conflicts with X, which is installed", "needs X 2.0, which no source offers").
  When an older version can be installed, that one is offered and the reason names the newer one.
- **Installed** and **Browse** (search by name or description) list packages with their state; a row opens the
  package page.
- **A package, in plain words:** title, summary and description from the package's own about file, chips (Signed,
  source, size), the action (add to plan, add update, remove), **What it can do** (read from what the package
  declares: the kernel interfaces it requires and whether it carries programs, including what it cannot do, such as
  "Use the network: No" and "Run as root: never by itself"), **What installing changes** (needs, conflicts, disk,
  replaces, files, SHA256) and **History** (its changes and plans from the about file, and every transaction that
  touched it here). The description of a package that is not installed comes from the package itself: Read
  description downloads it (verified against the signed index) and keeps its about file.
- **History:** every transaction, whoever started it: Orbit, the shell or the installer, with the time, who, the
  changes, the result and the `pkg` command behind it. Failed applies are recorded with why, and that nothing changed.
- **Sources:** each source's mirror, priority, index generation and entries, when it was checked and how long it
  stays valid, the trusted key's SHA256 and the cache cleanup setting, with the commands that change them.
- **Permission:** reading needs nothing. Applying needs root or the Desktop Portal grant `org.zenith.packages.manage`
  ("install, update and remove software", until the program closes); Orbit asks when a change is refused. Checking
  for updates is open to every user: the lists are signed and rollback-protected.
- **Running programs:** Orbit itself does not keep pkg from running (like a terminal client, it holds no package
  files). Other windowless or graphical programs still do, as for `pkg`; Orbit names them ("Close these programs
  first: ...").
- Keys: Ctrl+1..6 pages, Ctrl+R check for updates, Ctrl+F search, Ctrl+Enter apply the plan, Esc back or close.
- Without a window: `native run orbit --status | --list | --details NAME [--fetch] | --plan CHANGES | --apply CHANGES
  | --refresh | --history | --sources`, where CHANGES are `install NAME`, `upgrade NAME`, `remove NAME` and `kernel`.

### About files

A package may carry `/usr/share/zenith/about/NAME.about` (`ZENITH-ABOUT 1`: title, summary, up to eight
description lines, category, icon, replaces, `change VERSION TEXT` newest first, `planned TEXT`). The package tool adds
`packaging/about/NAME.about` from the checkout to every package it builds (meta packages have no files), so it is
signed with the package. Older kernels see an ordinary data file. Packages published before 0.0.70 have none until
their next release; Orbit then shows the package name and its declared facts.

### Native ABI: `zenith-native-package-center-abi` 1.0 (call 668, kernel 0.0.70)

[Header](../../userland/include/zenith_packages.h). Request `(request, 64, 0)` like call 667: version 1, operation,
output buffer and capacity (up to 64 KiB), value, input (up to 4 KiB), reserved zero. Older kernels answer -38.
Answers are line-based text: a header line, then tab-separated records.

| Operation | Value / input | Output |
|---|---|---|
| 1 overview | | kernel (running, staged, pending), counts, key, sources, cache, system (free, installed root, busy, may apply, clock), `scheduled` (enabled, settings revision) |
| 2 list | | `pkg` records: name, kind, source, state (current, update, available, not offered, local), versions, sizes, files, programs, title, summary, category, icon, reason |
| 3 details | name; value 1 downloads a package that is not installed | state, about fields, `can`, `needs`, `conflicts`, `file`, `change`, `planned`, `mine` (its transactions), digest, signed |
| 4 plan | `install`/`upgrade`/`remove NAME`, `kernel` lines | `action` (verb, name, from, to, source, bytes, also needed), `blocked`, sizes, restart, `command` |
| 5 apply | the same | a job id; -13 without permission, -16 busy, -5 with the reason |
| 6 job | | id, kind, state (running, done, failed, cancelled), bytes done/total, restart, step, message |
| 7 cancel | | before the changes are being written |
| 8 history | | `transaction` records (time, uid, user, source, ok, command, message), each with its `change` lines |
| 9 sources | | key, sources, cache |
| 10 refresh | | a job id (every user) |
| 11 wait | milliseconds (up to 5000) | 1 when no job runs any more, so a program without a window does not spin |
| 12 schedule | value 0/1, input is the observed little-endian u64 settings revision | Saves the installed system's schedule. Returns 0 on success, -13 for missing permission or -11 for a stale revision. |

Calls run outside the filesystem service gate and count with the interactive calls. Jobs hold the package service
(the same one `pkg` uses), so the shell and Orbit never change packages at the same time.

### Code and checks

`src/Core/Packages/PackageCatalogView.cs` (states, reasons, plans and commands, host-tested), `PackageHistory.cs`
(the transaction history in `/var/lib/zenith/pkg/history.tsv`, written by every commit), `PackageAbout.cs` (about
files and capabilities), `NativePackageCenter.cs` (call 668 and the jobs), `PackageManager.Change`, the 668 route in
`Process.s`, `userland/orbit/main.c`.

- `OrbitPackageTests`: history round trip, bounds and words; every commit recorded with its source; one transaction
  that installs and removes, refused as a whole when a removal is still needed; about files; capabilities; package
  states and reasons; plans with also-needed packages, cached downloads, blockers and the exact commands; the grant.
- `tools/orbit-smoke.py` (gate `y`): a signed HTTPS mirror; the signed package requires the ABI and carries its about
  file; headless, as root: overview, list with a not-offered reason, details downloaded for a package that is not
  installed, capabilities, a plan with an also-needed package and its command, a blocked plan, an ordinary user
  refused a change but allowed to read, apply, pkg seeing the result, the shared history, a blocked removal, refresh
  and the offered update; on the desktop the window, the update in the plan, Ctrl+Enter applying it, search opening
  a package page and the history page.

## Daily update checks

**Updates → Check daily and notify about updates** controls the installed system's schedule. The default is on. An administrator can also use `native run orbit --schedule off` or `--schedule on`. Orbit checks two minutes after desktop startup and then daily. Offline or busy systems retry after half an hour. When the offered packages or versions change, a notification’s **Show updates** button opens the list. Installation requires a separate action.

The first check after a reboot can remind the user about pending updates again. Run the Orbit gate with `--scheduled` to exercise the startup timer and the actionable notification against its signed test repository.

## Next

- Exercise staging and confirming a kernel from the desktop. Package screenshots and changelogs need distributor metadata.
