# Functional Appearance settings — 2026-10-27

## Source and delivery boundary

This slice extends the shared UI foundation with a root-authorized appearance
service, versioned native read/save requests, owned unsaved-window state and an
independent `zenith-settings` client. It adds signed semantic palette defaults to
the theme package. Existing legacy themes/clients retain their original behavior
until explicit preferences or new verified resources select the new palette.
[Contract](../../NATIVE-SETTINGS.md) · [Operator checklist](../../testing/SETTINGS-VM-TEST.md).

Release tags are pushed as recorded below; distributor publication and operator
installation are not yet confirmed. Other apps have not been migrated. Native font atlases remain compiled;
blur/transparency, per-user overrides, elevation and scalable text remain pending.

## Focused proof before Release qualification

- Initial host suite: 733 tests pass after adding persistence, schema/revision,
  wire/resource, authority and close-guard regressions. A concurrent-editor
  regression was added afterward; the final common run records its current total.
- UI tests pass under ASan/UBSan, including failed save, conflicting revision,
  retained edits, explicit reload/discard, readonly controls and chrome-close intent.
- Controller suite: 238 pass, including the actual C renderer/state tests.
- Independent client builds under GCC and Clang; ManualBuilder builds successfully.
- Debug kernel/ISO builds using installed Cosmos 3.0.89. The build initially lacked
  `$HOME/.dotnet/tools` on PATH; restoring the existing tool path resolves patcher
  discovery without changing SDK/workload state.

Focused kernel ELF SHA256:
`412f862159f76a019058b4339a1875d88fdb2a401be2dba5f0b113acea338136`.
ISO SHA256:
`4445ee244077c2ea53e38f03560a08827569ff22d0ef607fc763684915c52240`.
Settings 0.1.1 disposable fixture ELF SHA256:
`7eb2bd3c365ceafd2e7f34cf5c2a6b5665a867c28cd5c990f01f6bdaa0257d5e`.

Local QEMU 11.1.2 / explicit TCG, with fresh signed Settings 0.1.0/0.1.1 and theme
fixtures. The fresh setup phase passes CLI parity, strict request/memory bounds,
root save, spoofed `USER=root` denial and saved-versus-active headless state. Its
prepared disk was then replayed on an independent copy to complete:

```sh
PATH="$PATH:/usr/sbin:/sbin:$HOME/.dotnet/tools" ZENITH_VALIDATION_ACCELERATOR=tcg \
python3 -B tools/native-settings-smoke.py /tmp/zn-settings-native-4 \
  --prepared /tmp/zn-settings-native-3/setup/disk.img
```

Passed real keyboard preview; title-bar close retains the window and shows explicit
discard confirmation; Keep editing retains the draft; GUI save updates the shared
service and host theme; CLI shows saved/active agreement; Settings package upgrades
without kernel changes; guarded invalid calls still fail; clean poweroff and an
independent installed reboot reload the preference; `e2fsck -fn` returns 0.

The graphical observation helper waits for the actual client frame, identifying
the selected mode control rather than a similarly colored host title bar. Host
chrome clips the client body's outer corners, so full-width-row geometry alone
is not a valid content-origin detector. No input or save mutation is replayed.
Artifacts/logs remain in `/tmp/zn-settings-native-4`; preview images in
`obj/appearance-review/`. These are disposable outputs, not tracked source.

## Broader qualification

Clean-source common Release acceptance **passes** at exact implementation commit
`12ded91d993a26190f68cf1bd3be535731cf88aa`, with no gate omitted:

```sh
PATH="$PATH:/usr/sbin:/sbin:$HOME/.dotnet/tools" ZENITH_VALIDATION_ACCELERATOR=tcg \
python3 -B deploy/distributor/validate.py --keep /tmp/zn-settings-qualified
```

| Gate | Result |
|---|---|
| Controller/UI regressions | 238 pass, including sanitizer/state tests |
| Release host tests | 734 pass, including concurrent-editor revision checks |
| Release ISO/ELF and package tool | Build passes |
| Login/session/styling | 71 checks pass |
| Signed online repository/update | 94 checks pass |
| Fresh-install native Settings | Service bounds/permissions, signed resources, preview/dirty-close/save, live apply, package upgrade, reboot and ext2 audit pass |
| Existing graphical clients | 48 checks and independent ext2 audit pass |
| Native network | 72 checks, full kernel download, TLS/cleanup and ext2 audit pass |
| CLI extraction | 100 checks and raw input/fault/cancel restoration pass |
| Native processes/streams | All checks pass, including 64 exact-output large pipelines and desktop/local terminal paths |
| Kernel update/recovery | 169/169 checks and independent filesystem audits pass |

Release ELF SHA256:
`426a074076296809afc2bace1b71c4460b1cd9721e693b8da3e2166c556b0a24`.
Release ISO SHA256:
`1ee15b254beefbc0f736c8f5f2dfcd4dff2cc921fa63fe91d91e71a44f63b684`.
Settings 0.1.0 fixture ELF SHA256:
`cd4a0ed170beaaac0cd0228ba12a2b10cc1a09a56c67a6e8a45139141380cc67`.
The 0.1.1 fixture matches the client digest listed above. These are test fixture
versions; no production release was signed, tagged or published.

Top-level evidence remains at `/tmp/zn-settings-qualified`. Guest disks/logs are
retained at `obj/settings-qualified-native/zn-fsjlk2wv/` through its `native` symlink,
keeping multi-gigabyte images on disk-backed storage instead of the RAM-backed
`/tmp`. The new Settings phase uses distinct workspace `a`; login keeps `s`.
The final validator verifies both unchanged artifact hashes and a clean source
revision. The following qualification-record commit changes documentation only.

Remaining after qualification: confirm distributor publication, install/confirm the
compatible kernel before Settings, and complete the actual installed-VM checklist. The distributor VM needs
Python Pillow (`python3-pil` on Debian) for font atlas generation and the new gate.

## Authorized release tags

The operator authorizes publication on 2026-10-27. `main` and these annotated tags
were pushed atomically to `Azazel55605/Zenith`; a remote lookup verifies every peeled
release tag points to qualified commit `12ded91d993a26190f68cf1bd3be535731cf88aa`:

- `kernel/v0.0.25`
- `zenith-settings/v0.1.0`
- `zenith-theme-default/v0.1.1`
- `zenith-man-pages/v0.1.6`

The build VM needs `git pull --ff-only` in its controller checkout for the new
Settings recipe, and Python Pillow (`sudo apt-get install python3-pil`). Tags trigger
the existing build/sign/publish workflow; this push does not prove its completion.
Install and boot-confirm kernel 0.0.25 before the new Settings package. Actual
server publication and installed checklist acceptance remain pending.
