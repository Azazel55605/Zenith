# Native input/write slice and normal ISO builds

Completed local acceptance on **2026-10-16**. This record owns execution evidence;
[current status](../../ROADMAP.md#state-and-evidence) owns server/operator acceptance.

## Result and release source

Fully tested source: `2a515e8d8f23e0066275a4917daa615866e2fb8c`.
The following tags were pushed atomically with main after acceptance:

- `kernel/v0.0.16`
- `sunbeam-tools/v0.3.0`
- `zenith-man-pages/v0.1.1`

These are source release triggers for the existing distributor. Server builds,
signing/publication and actual operator installation were not observed in this
run. Update/confirm the compatible kernel before upgrading the native tools.

## Implemented boundary

The protected foreground ABI now accepts a finite UTF-8 shell stdin snapshot and
caller-scoped regular-file write/create/truncate/append/flush services. Full user
ranges are checked before effects, writable opens pin target identity and reject
final symlinks/devices/directories, and handles are cleaned up on exit/fault/cancel.
Sunbeam adds stdin `cat` and `tee [-a] FILE`. New native bundles require
`zenith-native-io-abi 1.0`. Older read/directory flags and ABI-1 binaries remain
supported. Exact flags, errors, budgets and partial-write limits belong to the
[native reference](../../NATIVE-PROGRAMS.md#input-and-writable-descriptors).
Interactive TTY, streaming byte pipes, seek and general process scheduling remain
future work. This slice does not unbundle the desktop or every linked builtin.

## Normal extension build repair

The Cosmos VS Code extension invokes `cosmos build -p PROJECT -a x64 -c Debug`
or `Release`. The system SDK 10.0.113 still selected workload set 10.0.112.1,
whose required manifests were removed by an SDK package update; MSB4242 occurred
before kernel compilation. The operator ran
`sudo dotnet workload config --update-mode manifests`. Both exact extension build
configurations then produced `output-x64/Zenith.iso`, and the operator confirmed
that extension builds work again. The native I/O Debug build also passed.
Cosmos remains pinned at 3.0.89; no SDK-root/environment workaround was added.
Original system workload state was backed up at
`/tmp/zenith-dotnet-workload-state-before.json` (ephemeral).

## Validation

Full clean-source acceptance used normal system dotnet, the pinned Cosmos tools
and explicit TCG, without production signing keys or server settings:

```sh
PATH="$HOME/.dotnet/tools:/usr/sbin:/sbin:$PATH" \
ZENITH_VALIDATION_ACCELERATOR=tcg \
python3 -B deploy/distributor/validate.py \
  --keep /var/tmp/zenith-native-io-release-20261016-v4
```

| Gate | Result | Phase seconds |
|---|---|---:|
| Common controllers | 109 passed | 1.302 test time |
| Release host tests | 632 passed | 9.006 |
| Release ISO build | Passed | 24.503 |
| Package-tool build | Passed | 1.401 |
| Login/session/styling | 71 checks passed | 216.786 |
| Online repository | 92 checks passed | 340.808 |
| Protected native programs, expanded I/O | Passed | 295.820 |
| Kernel recovery | 169/169 checks passed | 580.811 |

Separately, all 11 package-tool Python tests passed, including the new capability
dependency; the standalone Sunbeam 0.3.0 build passed. The descriptor suite covers
finite UTF-8/EOF/close, invalid flags before effects, limits, short-write accounting
across descriptors and exceptional cleanup. The native gate exercises actual
pipe/redirection input, create/truncate/append, invalid copyin/copyout ranges,
read-only copyin, stale descriptors, write limits, fault/cancellation and subsequent
shell use, ordinary-user ownership and denied writes, same-kernel executable
upgrade, exact independent ext2 file contents, clean filesystem audit and desktop
login afterward. The common gate also covers real full-sized signed kernel
activation, interrupted updates, external ISO recovery and broken-kernel fallback.

Built artifact SHA256:

```text
Zenith.iso 442344c5624d32d50297c4c6e73e0d7700a8d6cd83ac7f31bf99ee5ebe7dd540
Zenith.elf 68f78e86e23281e4e7f40794edea64affef6eaf0d9483cd549e14031e4108f5e
```

Evidence includes phase-times.json, artifacts.json, phase logs and retained
native disks/transcripts under that acceptance directory. Local scratch paths may
be pruned; the commit, harnesses and result descriptions above are durable.

## Failures found during acceptance

An exploratory native I/O run passed in 453.166 seconds before the final common
run. Common attempt v1 stopped after the unsaved-editor logout refusal: two timed
Ctrl+Q commands left the editor open. The login harness now uses the existing
complete-response QEMU monitor helper, observes explicit non-content editor
confirmation/closed events, and retains a failure screenshot. Both editor phases
and logout passed in subsequent runs.

Removing the old monitor helper's 40 ms delay unintentionally increased typing
speed. Attempt v2 received a truncated keyboard-layout command. Restoring the
previous overall 90 ms cadence preserved transport timing, but v3 still received
`OGIN_ROOT=0` instead of `LOGIN_ROOT=0` in the local framebuffer console with
legacy PS/2 input and virtio GPU. These failed guests were stopped after capturing
their logs; no command/password was automatically replayed and no timeout or
expected status was relaxed.

The long graphical gate now uses the supported virtio keyboard, matching the
existing desktop smoke profile. That full sequence passed in v4, including local
history, German Shift+7 and package/theme upgrades. The separate standard-VGA
recovery boot retains PS/2 input coverage. Sustained PS/2 input loss in the affected
QEMU graphical profile remains open; this profile change is not a PS/2 driver fix
or broader hardware certification.
