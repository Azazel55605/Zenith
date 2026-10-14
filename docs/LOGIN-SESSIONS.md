# Login, session selection and packaged appearance

User-selected next direction, 2026-10-14: login through a proper session manager,
choose desktop or headless, and manage styling through a metapackage. Headless
means a local text console with keyboard input and serial fallback, as explicitly
confirmed by the user. This document is an implementation plan, not shipped behavior.

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

Today `SessionRuntime` starts one host at boot; `ISessionHost` has Start/Tick only.
The desktop immediately opens Welcome/Terminal. Account authentication exists,
and a session generation change closes old desktop windows, but that is not a
complete manager for worker cancellation, disposal or switching hosts. Headless
currently means COM1 only. These are the integration points for the new lifecycle.

Start with a trusted kernel-side lifecycle/authentication service, a graphical
greeter and a local text host. Introduce explicit state transitions through login,
authentication, starting, running, stopping and return-to-login. Serialize transitions;
do not swap hosts while a command worker is still running or while a package/boot
mutation owns the maintenance gate. Authentication work must not freeze input or
painting; successful activation must bind credentials before apps/workers start.

An independently executable graphical manager needs controlled graphics/input/IPC
and a resumable process interface. Native ABI 1 currently supplies only bounded
foreground output/exit/identity. Therefore the initial trusted host integration
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

The current builder and manifest parser reject zero files, and signed repository
entries currently support data/kernel kinds only. True dependency-only packages
need an explicit compatible format/runtime capability, builder/index/planner/DB
support and install/upgrade/remove/rollback validation. Preserve old signed package
readers and fail clearly on unsupported versions. Do not disguise an empty package
as an arbitrary dummy payload. Removing the metapackage should not silently remove
its dependencies; automatic orphan removal remains a separate policy decision.

## Appearance contract

Existing `Theme` values are compile-time constants. Merely distributing theme files
cannot change the current UI. Introduce a shared validated resource loader and
replace those constants with a runtime theme snapshot before claiming independent
appearance updates. Preserve bounded dimensions, readable contrast and compiled
fallbacks when files are malformed, missing or incompatible. Themes are declarative
data, never arbitrary privileged code.

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
