# Native Settings and Appearance

Work owners: **S1/D1/D3** in the [master roadmap](ROADMAP.md). This page is a contract/procedure, not a separate work tracker.

[Shared UI](USERLAND-UI.md) · [Design language](DESIGN-LANGUAGE.md) ·
[System settings/session behavior](SETTINGS-SESSIONS.md)

The independently compiled `zenith-settings` package supplies the first functional
Appearance page plus typed keyboard-layout, time-zone and default-session controls.
The GUI and trusted `settings` command share captured-identity preference services. This is not a kernel-linked Settings window.
Meridian extends it into the [Settings shell and app migration](components/SESSIONS-DESKTOP.md#native-app-migration). Most pages retain explicit WIP banners until their typed backends are implemented.

## Use and visible behavior

```sh
native run settings
native run settings --page style.colors
native run settings --show
native run settings --set light teal
settings show
settings set appearance dark blue
settings set translucency 80
native run settings --page input.keyboard
native run settings --page locale.time
native run settings --system-show
```

Modes: `dark`, `light`. Accents: `blue`, `violet`, `rose`, `amber`, `green`, `teal`.
The GUI previews edits locally. **Save changes** persists them; simply selecting a
mode/accent does not change other windows. The desktop applies a successful save
on its next tick, without logging out. The page polls status and distinguishes
unsaved preview, saved/pending and saved/active. Headless saves become active on the
next graphical login; live-system changes are explicitly volatile.

All accounts may read system defaults; only captured UID 0 may save them. Ordinary
accounts can save their own personal appearance and reset it to inherited defaults.
`USER=root` grants no authority. System keyboard/time-zone/session controls remain
read-only to ordinary accounts. An elevation dialog is not implemented.

Failed saves retain edits and the active snapshot. Concurrent service edits use a
revision check: stale saves fail rather than overwrite. Reloading over edits needs
an explicit discard choice. Escape, the Close button and a dirty title-bar close
request offer **Keep editing / Discard changes**. Session logout refuses unsaved
windows. Forced process cancellation or host revocation still terminates the client
without saving. Current source opts into the [owned interactive profile](GRAPHICAL-CLIENTS.md#interactive-and-metadata-contracts); the published Appearance-only client retains the original lifetime budgets.

## Persistence and resources

Local administrator-owned `/etc/zenith/appearance.conf` has exactly these keys:

```ini
VERSION=2
MODE=dark
ACCENT=blue
MATERIALS=0
```

Schema 1 (MODE/ACCENT only) is read with solid materials. Schema 2 adds a strict
mask: transparency=1, blur=2, valid values 0..3. Schema 3 adds Whole app
background=4 (valid values 0..7); new saves use schema 3 only when that bit is set.
Schema 4 adds `TRANSLUCENCY=0..100`; it is written when the level differs from
the default 40. Schemas 1–3 load with level 40. Legacy saves preserve both newer
material bits and the level. Zero gives a solid base; 100 is the maximum readable
translucency for the active palette, not unrestricted alpha. The renderer maps the
whole range onto its contrast-safe range.
Blur-only (2) is retained but
inactive. Mode/accent/materials save together through one revision/replace.
Legacy 611 saves preserve material bits. A kernel that does not understand the saved schema uses its safe fallback and
refuses to overwrite the file. Disable Whole app background before rolling back
to a schema-2 kernel if appearance compatibility is wanted; restore an older schema
explicitly only when that preference rollback is intended.

Reads are bounded to 4096 UTF-8 bytes; invalid/duplicate/unknown keys and future
versions fall back at boot with a diagnostic. Saves refuse to overwrite an invalid
or future schema. Root must repair it explicitly. Revisions are monotonic during
one boot, not durable IDs or filesystem watchers; reboot revokes all old clients.
Direct out-of-band root file editing requires restart/reload through the normal
boot route. Service/CLI edits share the same in-memory revision authority.

Writes use the existing staged replacement, flush, mode and filesystem sync path.
This preserves its existing ext2/power-loss limitations; it is not a journalled
transaction. A post-replacement sync/metadata failure can leave new on-disk content
while the service reports an error and retains its old active snapshot.

Shipped defaults remain separate at
`/usr/share/zenith/appearance/default/native.conf` in `zenith-theme-default`.
The trusted host verifies package ownership, digest, mode and bounded regular-file
content before loading resources at graphical login. Example:

```ini
VERSION=1
light.Surface=f6f7f9
dark.blue=8baaff
```

Keys are `dark|light.ROLE` or `dark|light.ACCENT`. Roles are `Surface`, `Raised`,
`Subtle`, `Border`, `Text`, `Secondary`, `OnAccent`; accent names are the six above.
Values are six hex RGB digits. Omitted values use the accepted neutral baseline.
Unknown/duplicate keys, oversize/future schemas or insufficient contrast reject
the whole resource, using built-in safe tokens. Normal text must reach 4.5:1 on
all three surfaces; accents 3:1 on surfaces and accent labels 4.5:1. The complete
immutable table is shared with clients, so previews use the actual loaded resource.

Legacy `theme.conf` remains supported. On systems with neither explicit appearance
preferences nor the new signed resource, existing legacy host themes remain intact.
An explicit appearance save or the new resource opts the host into the shared
palette. Resource/package changes load on next graphical login; preference saves
apply live. The current native text atlases remain compiled into each client;
installed font replacement, font selection, Unicode shaping and scalable layout
are future work. The Colors page now saves transparency/blur separately, in the
same draft as mode/accent. Unsupported/headless renderers report them unavailable;
saved, effective and available state are distinct.

## Native wire contract (settings ABI 1)

Use `userland/include/zenith_settings.h`. Calls use protected service yield,
full readable/writable range validation and captured credentials; no arbitrary
path, command string, managed pointer or privileged callback crosses this API.

| Call | Arguments | Result |
|---|---|---|
| 610 read | writable snapshot pointer, 448, 0 | 0, complete snapshot |
| 611 save | readable request pointer, 32, 0 | 0 after persistence, or error |

Snapshot, little endian: version u32=1, flags u32, saved revision u64,
saved mode/accent u32 each, active mode/accent u32 each, active revision u64,
resource generation u64, `RGB[2 modes][6 accents][8 roles]` u32, two reserved u64=0.
The header is 48 bytes, table 384 bytes, reserved tail 16 bytes. Table order is
Surface/Raised/Subtle/Border/Text/Secondary/Accent/OnAccent, matching `zui_theme`.

Flags: persistent=1, writable=2, saved waiting for active=4, verified packaged
semantic palette=8. No bit claims that blur, translucency or user elevation exists.
Mode 0=dark/1=light; accent indexes follow the names above. Resource generation
changes on graphical resource reload; client polling need not redraw unchanged data.

Save request: version u32=1, flags u32=0, expected saved revision u64, mode/accent
u32, reserved u64=0. Invalid version/size/reserved fields return -22, invalid memory
-14, unauthorized save -13, stale revision -11, revision exhaustion -75 and storage/
configuration failure -5. Invalid requests and failed saves never publish a new
runtime selection. Calls share existing per-invocation service limits.

## Material preference extension

- **608 read** `(writable snapshot, 48, 0)`: u32 version=1, u32 flags
  (persistent=1/writable=2), u64 saved revision, u32 saved mask, u32 effective mask,
  u32 available mask, u32 reason, u64 active revision, u64 reserved=0.
- **609 save** `(readable request, 32, 0)`: u32 version=1, u32 flags=0,
  u64 expected saved revision, u32 mode, u32 accent, u32 material mask,
  u32 reserved=0. Captured UID 0 only; same atomic persistence/error rules as 611.

Calls 608/609 retain their original version-1 mask (transparency=1, blur=2).
Their snapshot masks the newer bit out, and a legacy save preserves that bit.
Version-1 requests still reject values above 3.

**616 read** and **617 save** use the same 48/32-byte layouts with version=2.
They add Whole app background=4 (masks 0..7). The shared helper negotiates these
calls and can fall back to the legacy surface-only service. New packages declare
`zenith-native-background-abi 1.0`; older clients continue reading/saving their
surface controls without resetting the newer preference. Effective reports renderer-enabled features; the
reason explains per-surface reductions. Reasons: 0 normal, 1 blur needs
transparency, 2 no graphical renderer, 3 cache budget/oversize, 4 per-frame blur
budget, 5 contrast. Headless reports effective/available=0 while retaining saved
bits. Fallback never modifies the saved selection. Legacy 448-byte appearance
snapshots and 32-byte saves retain their reserved fields and semantics.

CLI: `settings set materials solid|transparent|blur|background|background-blur`; native diagnostics:
`native run settings --materials-show`. GUI toggles share Save/Discard/stale-edit
and root-only controls with mode/accent. Turning transparency off retains the
blur and background drafts. Whole app background is a system-wide default for
supported shared-toolkit clients; it is not whole-bitmap opacity. Existing ABI 1
clients and protected credential surfaces stay opaque. Per-app overrides are deferred. Legacy calls retain system scope; scoped calls below add personal profiles. Elevation is not implemented.

## Owned window state ABI 1

Call 604 takes invocation-local surface handle, dirty Boolean (0/1), 0. It returns
0 or the existing bad-handle/closed/malformed errors. It cannot alter another
client's window. Legacy clients that never call it retain their previous behavior.
A dirty title-bar close keeps the surface alive and queues close-intent event 9;
it survives input overflow. The client renders confirmation and either stays open
or explicitly clears dirty state and closes. Forced revocation/exit still reclaims
all resources and emits terminal close, regardless of dirty state. The host also
uses dirty state to block ordinary logout. No global input or window enumeration
is exposed.

`zenith-settings` packages require `zenith-native-settings-abi 1.0`,
`zenith-native-window-state-abi 1.0` and the existing graphics/native capabilities.
Older kernels must reject installation until upgraded and boot-confirmed.

## Build and validation

```sh
python3 -B tools/test_ui_library.py
python3 -B tools/build-userland.py /tmp/settings-payload --program settings
dotnet test tests/Zenith.Tests
PATH="$PATH:/usr/sbin:/sbin:$HOME/.dotnet/tools" ZENITH_VALIDATION_ACCELERATOR=tcg \
  python3 -B tools/native-settings-smoke.py /tmp/zn-settings-NEW
```

Python Pillow is required by the UI font atlas build; Debian uses `python3-pil`.
The new gate joins the common kernel acceptance plan and CI. It creates disposable
signed packages/disks and tests CLI parity, memory/format bounds, permission denial,
packaged palette loading, preview/close confirmation/save, live host activation,
same-kernel package upgrade, reboot persistence and independent ext2 integrity.
`--prepared DISK` replays the desktop/reboot sequence on a **copy** of an existing
setup fixture; it does not substitute for the full fresh-install gate.

Host tests separately cover failed storage, malformed/future config and resources,
concurrent/stale editors, dirty close overflow/revocation, readonly controls and
save/discard state transitions with ASan/UBSan. Local proof does not establish
server publication or operator-installed acceptance.

Meridian is the chosen application name for the next shell. Its [implementation
plan](MERIDIAN-PLAN.md) and [full page inventory](MERIDIAN-PAGES.md) describe the
future scope; this document continues to describe the implemented Appearance API.


## Translucency control ABI

The `zenith-native-material-control-abi 1.0` capability adds:

- **618 read** `(writable snapshot, 48, 0)`: the material snapshot layout with
  version=3; offsets 40/44 contain u32 saved/active translucency (0..100).
- **619 save** `(readable request, 40, 0)`: u32 version=3, flags=0, u64 revision,
  u32 mode, accent, materials, translucency, then u64 reserved=0.

Mode/accent/materials/level are persisted in one revisioned replace. Captured UID 0
is required. Old 608/609 and 616/617 keep their exact versions, masks, lengths and
reserved fields; their saves preserve the level. Foreground text and controls are
never globally faded. Meridian provides keyboard/pointer minus/plus controls in
10-point increments; the CLI accepts every integer 0..100.

Terminal, Editor, non-credential Sunrise steps and the trusted Welcome/System
fallbacks use the same owned bounded background material as the native workspaces
when Whole app background is enabled. Hidden terminal reads and Sunrise account
credentials force a solid body/chrome. Files stays outside this design pass.

## Shared system-default settings ABI

`zenith-native-system-settings-abi 1.0` exposes existing `SettingsService`
operations without shell callbacks, paths or authority derived from page names.
These controls are explicitly **system defaults**; personal appearance uses the
separate captured-UID service below.

- **620 read** `(writable snapshot, 4096, 0)`: little-endian version=1, flags
  (persistent=1, captured UID 0 writable=2), u64 boot-local revision; u32 keyboard
  count/zone count/current keyboard index/current zone index at offsets 16/20/24/28.
  Current time-zone text is NUL-terminated UTF-8 in bytes 32..95. Reserved bytes
  96..127 are zero. Eight 32-byte keyboard-name slots start at 128; forty-eight
  64-byte zone-name slots start at 384. Bytes 3456..4095 are reserved/zero.
  Only the counted slots are populated. A fixed-offset zone outside the built-in
  catalogue uses current zone index UINT32_MAX while retaining its actual label.
- **621 save** `(readable request, 32, 0)`: u32 version=1, field (keyboard=1,
  time zone=2), u64 expected revision, u32 catalogue choice index, u32 reserved=0,
  u64 reserved=0. Root-only; validates before indexing. No arbitrary config writes.

CLI/service changes increment the same boot-local revision. Stale requests return
-11, ordinary-user writes -13, malformed fields/indices -22, storage/apply errors
-5. A successful choice saves through the existing configuration store and applies
through the existing keymap/clock callback. Read-only users see actual defaults;
spoofing USER cannot grant write access. All native pointers are checked over the
full declared range before copying. Catalogues are bounded to implemented layouts
and the built-in zone table, not a complete device or tz database.

Meridian's Keyboard and Date & Time pages protect drafts, support save/discard,
and refuse stale saves without replaying operations. WIP banners remain for
model/repeat/Num Lock/shortcut and clock-edit/sync-policy features. The time-zone
control changes local display/DST rules, not UTC or authenticated time sync.


## Captured-UID appearance and public shared data

`zenith-native-personal-appearance-abi 1.0` adds:

- **622 read** `(scope, writable snapshot, 512)`: scope 0=system, 1=own personal.
  Bytes 0..447 retain the appearance snapshot; 448..495 contain material-control
  version 3. u32 version=1/scope/captured UID/inherited are at 496/500/504/508.
  Revisions and writable/persistent flags belong to the selected scope. Effective
  renderer availability still describes the current desktop.
- **623 save** `(readable request, 48, 0)`: u32 version=1/scope, u64 revision,
  u32 mode/accent/materials/level/inherit, u32 reserved=0, u64 reserved=0.
  System writes require root and inherit=0; personal writes target only the captured
  caller UID. inherit=1 resets the personal profile. No target UID/path is accepted.

Personal profiles are service-owned `/etc/zenith/preferences/UID.conf`. The final
ancestor is root-owned 0700; all ancestors and existing/staged records are validated
before privileged I/O. Records must be single-link root-owned regular files with no
unprivileged write permission. Raw filesystem access remains denied to ordinary
accounts. Missing profiles inherit live system defaults; explicit reset writes
`VERSION=1` / `INHERIT=1`. Overrides use the existing bounded appearance schemas.
Malformed/future records are preserved and saves refused; the renderer safely uses
system defaults. Live boot profiles are volatile. Revisions are boot-local; changes
apply when that identity owns the desktop, not through another account's invocation.
The existing staged-replace/power-loss limitations above also apply here.

`zenith-native-shared-settings-abi 1.0` adds:

- **624 information** `(kind, writable snapshot, 4096)`: kind 1=primary network
  status, 2=public account information. Four u32 values (version=1, kind, UTF-8
  byte length, truncated) precede 4080 bytes of NUL-terminated text. Payload is at
  most 4079 bytes, never a partial UTF-8 sequence. Network values come from the
  existing network service. Root sees at most 24 public account rows; ordinary
  users see only themselves. Password/hash fields are never serialized.
- **625 system read** `(writable snapshot, 4096, 0)`: version 2 keeps 620's layout,
  adds current session name at 96..111, u32 session count/index at 3456/3460,
  then three 16-byte registered session names at 3464. Remaining bytes are zero.
  UINT32_MAX means the saved selection is unavailable. Only registered hosts appear.
- **626 system save** `(readable request, 32, 0)`: version 2 retains 621's format
  and adds field 3=default session. Fields 1/2 remain keyboard/time-zone. Root-only;
  revision and actual catalogue membership are checked. 620/621 remain version 1
  and refuse session writes. Default-session changes take effect on the next boot.

Meridian offers searchable bounded keyboard/time-zone/session pickers, explicit
Personal/System scope and confirmed Use defaults. Network/account pages are
read-only information with WIP notices for unsupported edits. Examples:

```sh
settings personal show
settings personal appearance light violet
settings personal inherit
native run settings --personal-show
native run settings --personal-set light violet
native run settings --personal-set inherit
native run settings --system-set session headless
native run settings --settings-info network
native run settings --settings-info accounts
```
