# Primary display settings qualification (M2/D2)

The primary display service, protected 640/641 ABI, Meridian controls and trusted
CLI share driver-bounded revisioned transactions. The desktop owner retains old
buffers and window objects through the independent 15-second preview. Keep alone
persists; timeout, requesting process exit and session teardown revert. Integer
100/200% scales the completed UI, with an 800x600 minimum logical desktop.

The actual pinned Cosmos 3.0.89 assembly audit is retained locally in
`obj/display-api-audit.txt` and `obj/display-driver-audit.txt`. VMware setters
change hardware; Virtio/GOP setters ignore dimensions and remain fixed-mode.
SVGA choices require actual register limits and double-buffer VRAM capacity.
640/641 are forwarded explicitly through owned BSP/private AP bridges; 627/628
remain pointer-motion/clock calls. The signature hash changed only after reviewing
those additional dispatch branches.

Local evidence:

- Release x64 ISO/ELF publication succeeds. Final ELF SHA256:
  `46675e13b4b5d3a17731b12d248aaf8845cb56af1895db4c21ed919e86b89930`.
- All 868 plain host tests pass, including 15 display transaction cases:
  `obj/display-host-tests-final.log`.
- Five shared UI suites pass, including display drafts, keyboard selection,
  confirmation modal, stale state and permissions: `obj/display-ui-tests.log`.
- All 80 controller/integration/documentation tests pass:
  `obj/display-integration-final.log`.
- Fresh four-vCPU TCG display gate passes: `obj/display-native-final.log` and
  `obj/display-native-final/`. Covers actual Meridian Apply/Keep/Revert,
  native process exit, independent host timeout, physical framebuffer dimensions,
  persistent reboot, fixed Virtio mode refusal, safe boot, UID denial and ext2 audit.
- Existing four-vCPU Settings regression passes in 289.996 seconds, including
  the unchanged exact wheel assertion, concurrent network/file activity, account
  isolation, launcher retirement, reboot persistence and ext2 audit:
  `obj/display-settings-final.log`. Artifacts remained fixed throughout both final runs.

This is local QEMU evidence, not publication or installed acceptance. Operator
VMware input at 200%, driver variants including 3D, sustained device behavior
and interrupted/recovery scenarios remain Q2/H1. Full clean-source release
acceptance is still required before a new kernel tag. Orientation, refresh,
brightness, fractional scaling, multiple outputs and night light remain WIP.
