# Zenith documentation

**Start with the [master roadmap](ROADMAP.md).** It is the single place for the
whole product: current focus, what works, every open track, dependencies, completion
criteria and release/installed acceptance. Audited **2026-10-27** against `a558f66`.

## Implementation details

These expand the roadmap's IDs; they do not define competing priorities.

| Component | Master work | Technical contracts |
|---|---|---|
| [Independent programs / UI](components/USERLAND.md) | U1–U6, D1 | [Native ABI](NATIVE-PROGRAMS.md), [graphics](GRAPHICAL-CLIENTS.md), [toolkit](USERLAND-UI.md) |
| [Sessions / desktop / apps](components/SESSIONS-DESKTOP.md) | D1–D3, S1–S5, W1–W3, A1–A2 | [Design](DESIGN-LANGUAGE.md), [Meridian contracts](MERIDIAN-PLAN.md), [43-page inventory](MERIDIAN-PAGES.md), [app requirements](APPLICATIONS.md) |
| [Shell / terminal](components/SHELL.md) | U3, X2 | [Settings/input/session](SETTINGS-SESSIONS.md), [developer ports](DEVELOPER-TOOLS.md) |
| [Storage / security](components/STORAGE-SECURITY.md) | R1–R3 | [Accounts](ACCOUNTS.md), [architecture](ARCHITECTURE.md), [future recovery](UPDATE-RECOVERY.md) |
| [Install / boot / hardware](components/BOOT-RECOVERY.md) | U4, H1, R2–R3 | [Install](INSTALLATION.md), [kernel updates](KERNEL-UPDATES.md) |
| [Networking](components/NETWORKING.md) | N1–N2, P2 | [Network contract](NETWORKING.md) |
| [Packages / distribution](components/PACKAGES.md) | P1–P4 | [Format/activation](PACKAGES.md), [online client](ONLINE-PACKAGES.md), [server](PACKAGE-SERVER.md), [runbook](../deploy/distributor/README.md) |
| [Multicore](components/MULTICORE.md) | C1–C4 | [Worker/safety contract](MULTICORE.md) |

## Use, build and verify

- [Project README](../README.md): boot/use commands and repository layout.
- [Development](DEVELOPMENT.md): toolchain, build, debugging and native qualification.
- [Testing procedures](testing/README.md): kernel-first operator checklists; results
  are linked from the master roadmap, not separately tracked here.
- [Architecture](ARCHITECTURE.md): actual runtime boundaries.
- [Native Appearance](NATIVE-SETTINGS.md): currently functional Colors service/client.
- [Accepted specimen](design/system-specimen.svg): visual target; compare real widgets
  using the [design parity/material rules](DESIGN-LANGUAGE.md).
- [Historical evidence](archive/README.md): dated investigations and old release results.
- [Pinned Cosmos reference](cosmos/index.md): upstream docs, not Zenith's roadmap.
- [Documentation rules](MAINTAINING.md) and [repository guide](../INSTRUCTIONS.md).

## How to read state

Implemented source, local native qualification, server publication and operator
installation are different observations. Unknown acceptance remains unknown; it
is not automatically an active bug. WIP pages preserve planned scope without
claiming devices/services exist. Historical versions and fixture results never
replace the current master state.
