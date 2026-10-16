# Active work

Updated **2026-10-16**. This is a short handoff, not an append-only changelog.
[Status](STATUS.md) summarizes the system; [Roadmap](ROADMAP.md) orders future work.
Completed evidence lives in the [archive](archive/README.md).

## Current slice: ext2 metadata corruption — locally complete

The server's prepared baseline has directory `/var/lib` at inode 49. The failed
upgrade clone has a **regular file** at that same inode, making boot metadata
unreachable. GPT/EFI/kernel integrity still passes. This confirms on-disk root
corruption after preparation; the guard correctly returned K_UPDATE=1.

- [x] Separate the genuine failure from the independently fixed status-prefix race.
- [x] Inspect both server disks read-only; confirm baseline directory versus failed
  clone regular inode 49. Handle Debian sbin tool lookup without sudo.
- [x] Reproduce neighbouring directory-to-file corruption deterministically in a
  host test: another inode read replaces the shared inode scratch block during RMW.
- [x] Give inode RMWs independent buffers and serialize complete ext2 operations,
  with explicit release on exception for Cosmos's unwind limitation.
- [x] Pass 624 Release host tests: interrupted and concurrent metadata updates,
  cross-worker exception cleanup, parallel creation/indirect file writes and
  remount/checker audits, plus existing permission/fault/allocation coverage.
- [x] Build the Release ISO; pass 69 native session checks and independent ext2
  audit. Reproduce and fix a separate observer stall when scheduler output consumes
  a completed prompt. Fix the same filtering defect for complete authentication
  results/rejections; 109 controller regressions pass, no mutation/credential replay.
- [x] Complete common native acceptance on the rebuilt binary: 109 controller
  tests, 624 host tests, 69 session checks, 92 online repository checks,
  native-program gate and all 169 kernel recovery checks pass. Independent ext2
  and EFI audits pass; stopped upgrade image retains directory inode 49.

Do not repair the retained failed disk, relax ancestor checks, move kernel/v0.0.13
or claim the exact server thread interleaving was observed. The matching corruption
mechanism is reproduced; a fixed product binary needs its own release candidate.
See [investigation](archive/2026-10-16/kernel-update-ancestor-investigation.md).

## Waiting for external acceptance

The fix is committed in `c622c13`, with authentication observer follow-up
`ce42cfd`. Pull the new controller on the distributor **before** tagging/building
the next kernel candidate (**0.0.14**, not yet tagged). Immutable 0.0.13 cannot
acquire the product fix through a retry. New candidate server publication and
installed VM acceptance remain outstanding. Sunbeam **0.2.0** publication/install
with the fixed ABI-capable kernel is also unconfirmed. Kernel **0.0.12** and the
login/resource packages are operator confirmed. See [Status](STATUS.md#release-and-installation-boundary).

After fixed-candidate release acceptance, resume [native userland](components/USERLAND.md):
first finish release acceptance, then select the next bounded input/write slice.
Do not mark M5 complete or start unrelated desktop/multicore implementation from
this handoff alone.
