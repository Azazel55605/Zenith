# Zenith documentation

This is the project documentation entry point. Status reviewed on **2026-10-11**
against implementation commit `8389113`. Plans below describe intended work, not
features already available. Milestone version labels are targets, not release dates.

## Reading paths

| You want to understand… | Start here |
|---|---|
| What works, how to boot and use it | [Project README](../README.md) |
| How the current system actually works | [Architecture](ARCHITECTURE.md) |
| Desktop, headless, DE, tiling WM, settings and Files direction | [System plan](SYSTEM-PLAN.md) |
| Updating an installation and distributing applications | [Package manager design discussion](PACKAGE-MANAGER.md) |
| Moving desktop/apps outside the kernel | [Userland direction](USERLAND.md) |
| Serving packages from home | [LAN repository plan](PACKAGE-SERVER.md) |
| Saved-system boot, snapshot trials and restore | [Upgrade recovery](UPDATE-RECOVERY.md) |
| What happened and why | [Project history](HISTORY.md), then [slice evidence](PROGRESS.md) |
| What comes next and when a milestone is complete | [Roadmap](ROADMAP.md) |
| Building, testing, debugging and working with Cosmos | [Development guide](DEVELOPMENT.md) |
| Whether Bash can run here | [Bash portability assessment](BASH-PORT.md) |
| Earlier measurements and engineering background | [Technical notes](TECHNICAL-NOTES.md) |
| What the upstream framework documents | [Cosmos reference](cosmos/index.md), [pinned source](cosmos/SOURCE.md) |
| Rules for contributors and agents | [Shared instructions](../INSTRUCTIONS.md) |

## Status vocabulary and maintenance

- **Implemented** means code exists; **verified** means the stated scenario was tested.
- **Planned requirement** records requested product behavior without committing its API.
- **Proposed** means an architecture or scope recommendation awaiting discussion.
- **Deferred** means deliberately outside the next slice, not silently completed.

The architecture describes current code; the system plan describes product requirements;
the package document owns update design questions; the roadmap owns milestone exits;
PROGRESS records dated execution and evidence. Keep these roles separate so a proposal
cannot become a shipped claim through repetition. Historical test counts are snapshots.
Local validation does not establish that hosted CI passed after an unpushed commit.

M0/M1 are complete. The bounded ext2 implementation and installed-root validation are
complete; **M2 remains open for users/authentication and permission enforcement**.
The next implementation slice should follow the decisions recorded during this planning
discussion, rather than beginning networking or package installation implicitly.
