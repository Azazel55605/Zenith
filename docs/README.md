# Zenith documentation

Start with [Status](STATUS.md) to see what exists, [Progress](PROGRESS.md) for the
current task, and [Roadmap](ROADMAP.md) for the order of future work.
Reviewed **2026-10-15**, against source through `f502085` and operator reports.

## Components

Each component page owns its implementation checklist, remaining steps, source
locations and acceptance criteria. Detailed technical guides remain linked from it.

| Component | Start here | Technical reference / use |
|---|---|---|
| Shell and terminal | [Shell workflows](components/SHELL.md) | [Input/session contract](SETTINGS-SESSIONS.md), [Bash feasibility](BASH-PORT.md) |
| Storage, accounts and access | [Storage and security](components/STORAGE-SECURITY.md) | [Accounts](ACCOUNTS.md), [architecture](ARCHITECTURE.md) |
| Install, boot and recovery | [Installation and updates](components/BOOT-RECOVERY.md) | [Install](INSTALLATION.md), [kernel updates](KERNEL-UPDATES.md) |
| Network and trust | [Networking](components/NETWORKING.md) | [Commands, configuration and bounds](NETWORKING.md) |
| Packages and distribution | [Package delivery](components/PACKAGES.md) | [Client](ONLINE-PACKAGES.md), [format/transactions](PACKAGES.md), [server](PACKAGE-SERVER.md) |
| Login, desktop and applications | [Sessions and desktop](components/SESSIONS-DESKTOP.md) | [Login](LOGIN-SESSIONS.md), [settings](SETTINGS-SESSIONS.md), [app plans](APPLICATIONS.md) |
| Independent programs | [Native userland](components/USERLAND.md) | [ABI and limitations](NATIVE-PROGRAMS.md), [migration plan](USERLAND.md) |
| CPU scheduling | [Multicore](components/MULTICORE.md) | [Worker contract](MULTICORE.md) |

## Other reading paths

| Purpose | Document |
|---|---|
| Boot and use Zenith | [Project README](../README.md) |
| Understand current code boundaries | [Architecture](ARCHITECTURE.md) |
| Build, test or debug | [Development](DEVELOPMENT.md) |
| Understand product requirements | [System plan](SYSTEM-PLAN.md) |
| Understand package decisions and unfinished work | [Package design](PACKAGE-MANAGER.md) |
| Research whole-system rollback | [Snapshot/recovery design](UPDATE-RECOVERY.md) |
| Understand Bash feasibility | [Bash port assessment](BASH-PORT.md) |
| Find past decisions and validation | [History](HISTORY.md), [archive index](archive/README.md) |
| Read pinned upstream documentation | [Cosmos](cosmos/index.md), [source revision](cosmos/SOURCE.md) |
| Maintain this repository | [Shared instructions](../INSTRUCTIONS.md), [documentation rules](MAINTAINING.md) |

## Reading status correctly

**Implemented** means source exists. **Locally verified** names a passing local
scenario. **Operator confirmed** records an actual installed/server observation.
**Pending acceptance** means implementation exists but the named environment has
not been confirmed. **Planned** means no completed implementation is claimed.
Checkboxes on component pages track implementation; their evidence section states
what has actually been tested. Historical test totals are snapshots, not promises
about the current release. A pushed tag is not a published or installed package.

Current CLI release testing: [kernel-first VM handoff](CLI-VM-TEST.md).

Text/traversal release testing: [VM handoff](CLI-TEXT-VM-TEST.md).

Native network release testing: [kernel-first VM handoff](NETWORK-VM-TEST.md).

Protected graphical-client API: [surfaces, events and lifecycle](GRAPHICAL-CLIENTS.md).
