# Functional Appearance settings — 2026-10-27

## Source and delivery boundary

This slice extends the shared UI foundation with a root-authorized appearance
service, versioned native read/save requests, owned unsaved-window state and an
independent `zenith-settings` client. It adds signed semantic palette defaults to
the theme package. Existing legacy themes/clients retain their original behavior
until explicit preferences or new verified resources select the new palette.
[Contract](../../NATIVE-SETTINGS.md) · [Operator checklist](../../SETTINGS-VM-TEST.md).

No release tags have been pushed and no distributor/operator installation is
claimed. Other apps have not been migrated. Native font atlases remain compiled;
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

Pending the clean-source common Release acceptance run. The new Settings gate is
registered in that plan with a distinct native workspace (`a`), alongside every
existing login, repository, graphics, network, CLI, process and recovery gate.
