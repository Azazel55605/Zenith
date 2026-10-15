# Login, session selection and packaged appearance

> Implemented login/resource contract. Operator confirms 0.0.12 login; future graphical isolation remains open. Work steps: [Sessions/desktop](components/SESSIONS-DESKTOP.md).

## On this page

- [Current use and acceptance](#current-use-and-acceptance)
- [User experience](#user-experience)
- [Implementation boundary](#implementation-boundary)
- [Package structure](#package-structure)
- [Appearance contract](#appearance-contract)
- [Delivery order and acceptance](#delivery-order-and-acceptance)

User-selected next direction, 2026-10-14: login through a proper session manager,
choose desktop or headless, and manage styling through a metapackage. Headless
means a local text console with keyboard input and serial fallback, as explicitly
confirmed by the user. The initial trusted host and packaged resources are implemented
in the 0.0.11 source; protected graphical executables remain a later ABI slice.

## Current use and acceptance

Configured installed ext2 accounts reach a graphical greeter before apps open.
Type username, Tab to the masked password, then choose Desktop or Headless by
clicking the session selector (or Tab and Left/Right). Enter signs in. A configured
ordinary user with locked root still requires login and cannot regain bootstrap
root access. Live/fresh installs retain their setup/recovery workflows.

Ctrl+Alt+T opens Terminal; Ctrl+Alt+L or the launcher requests logout. Busy commands,
updates and unsaved editor documents block logout with a visible reason. Save or
explicitly close your work first. `logout` and `session switch de|headless`
return to login; switching closes the old session rather than retain its apps.
`exit` closes a desktop Terminal window; in the local console it ends the session.
Headless uses a local keyboard/text framebuffer and history with the configured
layout, without a desktop/window manager. No supported display retains COM1 ASCII
recovery. COM1 does not inject commands into an active local session. The greeter
provides synced local restart/poweroff before login; desktop power actions retain
the administrator policy.

After booting and confirming the new kernel, root installs the initial resources:

```text
pkg refresh
pkg install zenith-session zenith-style
```

`zenith-session` owns greeter branding, not an independent executable yet.
`zenith-style` is a true zero-payload metapackage requiring `zenith-theme-default`
and existing `zenith-fonts`. Icons/wallpaper packages remain future work; the current
procedural wallpaper uses shared colors. Palette version 1 changes colors, not
compiled widget layout. Appearance/fonts reload at the next login. Loaded resources
must match installed ownership, modes and digests; corrupt/missing/incompatible or
low-contrast resources use embedded fallbacks. Per-user preferences and Appearance
settings UI remain future work.

Manifest 3 supports dependency-only `kind=meta`; old data manifests still require
files. Removing a meta retains dependencies; removing a required dependency fails.
Definitions use `requires.tsv` and `build-meta`. The server signs legacy `index.zri`
without unsupported manifests and extended `index-v3.zri` containing all packages.
The new client falls back only on HTTP 404 before its extended cache exists.
Signature/freshness errors never trigger fallback. Initial migration inherits the
legacy generation floor; extended cache/snapshot checks refuse later downgrade.
Index signing uses the current controller reader even for an older tagged build.

Old kernels cannot parse installed manifest-3 records after a boot rollback, so
their package operations can fail clearly. Account/boot recovery remains available;
return to the retained compatible kernel before further package operations. This
is not a matched kernel/root snapshot.

`tools/login-session-smoke.py` checks installed authentication, user credentials,
busy/unsaved logout refusal, repeated DE/local-console login, history/German input,
visible signed styling upgrade under the same kernel, meta removal and independent
ext2 audit. It checks that credentials are absent from serial logs. The native ELF
gate authenticates before checking font rendering, and the server includes the
login harness when present in the pinned tag. See PROGRESS for exact results and
operator acceptance limits. The sections below retain the design/delivery context.

## User experience

- Installed systems with configured accounts present a login surface before
  opening desktop applications. Username/password authentication uses the existing
  account service; passwords are hidden, cleared after submission and absent from
  logs. Failed authentication does not create an authenticated session.
- The login surface offers Desktop and Headless only when their hosts are available.
  WM remains unavailable until its implementation exists. Saved session defaults
  select the initial choice; explicit boot arguments retain their documented
  headless/recovery behavior for unattended diagnostics and serial acceptance.
- Desktop opens under the authenticated user's credentials. A terminal is an app,
  not the login surface. The welcome/terminal windows should not be the installed
  login experience. Live installation and first-account setup remain explicit
  workflows, including recovery when the account database cannot be loaded.
- Headless uses a local text console on the VM display with keyboard input, history
  and hidden password entry. It does not start desktop windows or a window manager.
  Its display transport may use a framebuffer without making it a DE session.
  COM1 remains the fallback when no supported local display exists.
- Logout returns to the login surface and permits choosing a different mode.
  The first implementation closes the old session; preserving running apps across
  a DE/headless switch is outside this slice. Unsaved documents need a decision,
  and active installation/package/boot mutations must complete or cancel safely
  before shutdown of their owning session.
- Desktop supplies visible logout/restart/poweroff controls. Restart/poweroff use
  the existing synced shutdown path. Logging out must not restore bootstrap root
  privilege or leave queued commands, worker credentials or windows accessible to
  the next user. Serial/local login cannot create competing uncontrolled sessions.

## Implementation boundary

`SessionRuntime` now owns host start, stop readiness, cancellation and logout
selection. Welcome/Terminal bootstrap windows remain for live/unconfigured systems;
configured installed sessions authenticate before apps. Headless has both the local
text host and COM1 fallback. Session generations and explicit worker identities
continue to prevent inheriting another user's windows, input or history.

The implemented trusted lifecycle/authentication service, graphical greeter and
local text host use explicit transitions through login, authentication, starting,
running, stopping and return-to-login. Transitions must remain serialized;
do not swap hosts while a command worker is still running or while a package/boot
mutation owns the maintenance gate. Authentication work must not freeze input or
painting; successful activation must bind credentials before apps/workers start.

An independently executable graphical manager needs controlled graphics/input/IPC
and a resumable process interface. Native ABI 1 supplies bounded foreground execution and read-only file services;
it does not provide graphical/input/IPC services. Therefore the initial trusted host integration
requires a kernel update. Its validated resources/defaults can be independently
packaged first. Later move greeter rendering into a protected process while the
privileged authentication/session authority stays behind a narrow broker. A
configuration/assets package must not be advertised as an independent executable.

## Package structure

Working names are proposals; familiar labels such as Login and Appearance remain
the user-facing names. Sunrise remains the installer and Sunset the proposed
session/power controls, avoiding reuse of existing application names.

| Package | Role |
|---|---|
| `zenith-session` | Session-suite defaults/resources initially; future separately executable manager when its ABI exists |
| `zenith-style` | Styling metapackage selecting a coherent set of compatible resource packages |
| `zenith-theme-default` | Versioned color, spacing and widget tokens for greeter and existing test desktop |
| `zenith-fonts` | Existing packaged fonts, shared by both login and applications |
| `zenith-icons` | Future bounded icon assets used by session/desktop interfaces |
| `zenith-wallpapers` | Future wallpaper assets; safe fallback if absent or invalid |

Add only real available dependencies to the first published metapackage. Do not
publish placeholder icon/wallpaper packages to make the proposed table appear done.
The metapackage owns dependency policy, not application/theme files. Update its
dependency constraints when changing a styling family; the package resolver must
produce a visible, coherent plan and retain ordinary conflict/local-edit checks.

Manifest 3 and the `zenith-package-meta-abi` capability support zero-file meta
packages. The signed transport keeps data/kernel categories; embedded manifests
distinguish meta from data. Legacy catalogs omit unsupported records. Automatic
orphan removal remains a separate policy decision.

## Appearance contract

`Theme` colors now use a shared validated runtime snapshot; layout dimensions remain
compiled. Preserve bounded resources, readable contrast and compiled fallbacks for
malformed, missing or incompatible files. Themes are declarative data, never
arbitrary privileged code.

Ship defaults under `/usr/share/zenith/appearance/`; keep administrator selection
under `/etc/zenith/` and later per-user preferences in their homes. Package upgrades
replace shipped resources rather than overwrite local preferences. Initially reload
appearance at the next login, with that behavior visible to the user. A new layout,
widget implementation or graphics protocol may still require code/ABI updates;
the styling metapackage does not remove that dependency.

## Delivery order and acceptance

1. Establish authenticated session lifecycle and the greeter; retain explicit live,
   first-account and serial recovery paths. Verify hidden/wrong/locked-account
   authentication, credential binding and unavailable-session refusals.
2. Add the local text console and safe logout/relogin switching. Verify keyboard
   layout, history, repeated desktop/headless transitions, stale queued input,
   unsaved-window handling, mutation exclusion and no-GPU serial fallback.
3. Load shared theme data, introduce true metapackages and publish the first valid
   `zenith-session` resources plus `zenith-style` and its existing dependencies.
   Verify a styling package upgrade changes greeter/desktop appearance after login
   under the same running kernel, and malformed assets retain readable defaults.
4. Extend native process/graphics/input contracts to move the manager's renderer
   and later desktop/apps into separate executable packages. Keep the existing
   test DE while these interfaces mature; proper DE/WM/Files/Editor remain later work.

Native acceptance must include installed users, repeated logout/relogin, serial
fallback, package upgrades and independent filesystem auditing. Host state-machine
tests alone cannot establish visible login, correct keyboard handling or isolation.
