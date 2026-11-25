# Zenith documentation

The [master roadmap](ROADMAP.md) lists current priorities, estimated effort, dependencies, release evidence and installed acceptance. Use the component pages below for implementation details.

## Implementation details

These expand the roadmap's IDs; they do not define competing priorities.

| Component | Master work | Technical contracts |
|---|---|---|
| [Independent programs / UI](components/USERLAND.md) | U1–U6, D1 | [Native ABI](NATIVE-PROGRAMS.md), [graphics](GRAPHICAL-CLIENTS.md), [toolkit](USERLAND-UI.md) |
| [Sessions / desktop / apps](components/SESSIONS-DESKTOP.md) | D1–D3, S1–S5, W1–W3, A1–A2 | [Design](DESIGN-LANGUAGE.md), [Motion](components/MOTION.md), [Meridian contracts](MERIDIAN-PLAN.md), [Meridian behavior design](MERIDIAN-BEHAVIOR.md), [44-page inventory](MERIDIAN-PAGES.md), [app requirements](APPLICATIONS.md) |
| [Observatory](components/OBSERVATORY.md) | U7 | [Concept](design/observatory-concept.svg), [app requirements](APPLICATIONS.md) |
| [Equinox](components/EQUINOX.md) | U8 | [Concept](design/equinox-concept.svg), [app requirements](APPLICATIONS.md) |
| [Audio devices and mixer](components/AUDIO.md) | H2 | HDA transport, native PCM ABI and Meridian Sound |
| [sudo and the Desktop Portal](components/PORTAL.md) | W4 | [Accounts](ACCOUNTS.md), [native ABI](NATIVE-PROGRAMS.md) |
| [Horizon](components/HORIZON.md) | A1 | [Concept](design/files-concept.svg), [app requirements](APPLICATIONS.md), [native ABI](NATIVE-PROGRAMS.md) |
| [Sunset](components/SUNSET.md) | U10 | [Concept](design/sunset-concept.svg), [login sessions](LOGIN-SESSIONS.md) |
| [Aurora](components/AURORA.md), [Prism](components/PRISM.md), [Beacon](components/BEACON.md), [Orbit](components/ORBIT.md) | U2, U9, U11, U12 | [Application requirements](APPLICATIONS.md) |
| [Appearance](components/APPEARANCE.md), [removable storage](components/REMOVABLE-STORAGE.md) | U14, S4, S6 | [Appearance contract](NATIVE-SETTINGS.md), [storage](components/STORAGE-SECURITY.md) |
| [Shell / terminal](components/SHELL.md) | U3, U13, X2 | [Settings/input/session](SETTINGS-SESSIONS.md), [developer ports](DEVELOPER-TOOLS.md) |
| [Storage / security](components/STORAGE-SECURITY.md) | R1–R3 | [Accounts](ACCOUNTS.md), [architecture](ARCHITECTURE.md), [future recovery](UPDATE-RECOVERY.md) |
| [Install / boot / hardware](components/BOOT-RECOVERY.md) | U4, H1, R2–R3 | [Install](INSTALLATION.md), [kernel updates](KERNEL-UPDATES.md) |
| [Networking](components/NETWORKING.md) | N1–N4, PK2 | [Network contract](NETWORKING.md) |
| [Packages / distribution](components/PACKAGES.md) | PK1–PK6 | [Format/activation](PACKAGES.md), [online client](ONLINE-PACKAGES.md), [server](PACKAGE-SERVER.md), [runbook](../deploy/distributor/README.md) |
| [Multicore](components/MULTICORE.md) | C1–C5 | [Worker/safety contract](MULTICORE.md) |

## Use, build and verify

- [Project README](../README.md): boot/use commands and repository layout.
- [Development](DEVELOPMENT.md): toolchain, build, debugging and native qualification.
- [Testing procedures](testing/README.md): kernel-first operator checklists; results
  are linked from the master roadmap, not separately tracked here.
- [Architecture](ARCHITECTURE.md): actual runtime boundaries.
- [Primary display settings](DISPLAY-SETTINGS.md): resolution, integer scale, Keep/Revert and recovery.
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
