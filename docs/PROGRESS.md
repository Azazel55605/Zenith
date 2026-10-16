# Active work

Updated **2026-10-16**. [Status](STATUS.md) summarizes delivery; [Roadmap](ROADMAP.md)
orders future work. Completed evidence belongs in the [archive](archive/README.md).

## Current slice: native input and writable file handles

The operator requested the next unbundling slice and repair of Debug/Release
live-ISO builds from the Cosmos VS Code extension.

- [x] Reproduce normal build failure in the system .NET SDK's stale workload-set
  selection. Operator switched to installed manifests with administrator rights
  and reports that the extension builds work again. Cosmos stays pinned at 3.0.89.
- [x] Define bounded piped/redirected UTF-8 stdin, regular-file write/create/
  truncate/append, explicit flush, per-invocation handles and I/O budgets.
- [x] Implement managed service calls, validate full user ranges before effects,
  enforce caller permissions and preserve cleanup on every exit/fault/cancel path.
- [x] Extend independent Sunbeam tools and explicit package capability dependency.
- [x] Verify host regressions (632 Release tests plus 11 package-tool tests) and
  expanded native input/write/persistence/denial/fault tests. The first native run
  passed in 453.166 s, including independent ext2 contents/audit and desktop login.
- [ ] Run full pre-tag acceptance before any kernel release. The first common run
  stopped in the existing graphical editor-discard/logout step. Replaced timed
  HMP response reads with the shared complete-response helper and added explicit
  confirmation/closed events and failure screenshots. That sequence passed.
  The faster helper then exposed truncated keyboard input in the framebuffer
  console; retained its previous 90 ms typing cadence without replaying commands
  or passwords. Rerunning common acceptance with both transport corrections.

Interactive TTY, streaming pipes, seek and general process scheduling remain later
slices. Existing buffered text output and read-only ABI-1 binaries stay supported.

Previous TCP release: 0.0.15 was tagged after full local acceptance; the operator
reports real-path improvement. [TCP record](archive/2026-10-16/tcp-connect-reliability.md).
