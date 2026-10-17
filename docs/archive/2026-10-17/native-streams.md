# Native streams and bounded foreground pipelines

Recorded **2026-10-17**. Historical implementation/acceptance evidence; see
[current userland state](../../components/USERLAND.md) and
[ABI/limits](../../NATIVE-PROGRAMS.md#live-streams-and-pipelines).

## Scope and result

At tested commit `cba1cbf`, protected native programs have live stdout/separate
stderr, canonical host stdin, EOF/cancellation and bounded byte pipes. Sunbeam
adds independently compiled wc/head/tail. Raw TTY/PTYs, jobs, seek, environment,
general process scheduling and managed multicore remain outside this slice.

Implementation commits are `fddd561`, `efa5ca6` and `cba1cbf`. Source, build/signing
keys and package server configuration were not deployed by this local acceptance.
After operator approval, main and immutable tags for kernel 0.0.18, sunbeam-tools
0.4.0 and zenith-man-pages 0.1.2 were pushed. All three remote tags resolve to
tested `cba1cbf1ced6e312a3074d3ef355fab490182a5f`. Distributor publication and
installed acceptance remain unverified.

## Implementation steps

- Added byte channels with bounded storage, backpressure, EOF and early reader
  closure. Producer shells isolate state; the final stage retains parent state.
  Nested stages share a worker budget. Native pipe bytes preserve binary/UTF-8;
  text terminals use incremental decoding. Buffered compatibility redirections
  preserve bytes and enforce a size cap.
- Standard descriptors yield/resume on managed stacks. Invocation contexts keep
  their own roots, memory, credentials, FP/register state and budgets. Raw user
  entry remains serialized on the BSP. Expected setup/VFS errors are converted
  under a separate service gate, never held across blocking standard streams.
- Reused desktop/local/serial canonical line input. Added native isatty and the
  zenith-native-stream-abi capability; new bundles require the compatible kernel.
- Added bounded Sunbeam utilities and stderr diagnostics; retained recovery builtins.
  Utility algorithm tests are part of the common controller gate and accept GCC
  or Clang. Updated manuals and the native API guide alongside implementation.

## Failures found and corrected

1. Child pipeline shells initially did not inherit `$?`; copied the prior status
   and added a regression. Producer state remains isolated.
2. An initial test expected an intact keyboard echo despite interleaved debug
   output. It now checks the tool's complete output line. The ASCII serial logger
   cannot prove UTF-8; native counts and independent file bytes prove it instead.
3. Simultaneous native cancellation reproduced Cosmos recursive-exception
   fail-fast. Channels/canonical reads now return cancellation errno and native
   contexts finish through normal cleanup. Cancelled stage startup/sleep returns
   130 directly; cancelled pipe/redirect text output is discarded. Native setup
   and filesystem error conversion is serialized without serializing pipe waits.
4. Cancellation exposed a console handoff race that cleared the next command's
   prefix. Input ownership now ends immediately on Ctrl+C. Public partial/queued
   lines and EOF survive worker handoff; secret type-ahead is still discarded.
   Host tests cover these races, and the guest gate repeats early/blocked cancel.

## Acceptance

Environment: local x64, user-local .NET SDK 10.0.401, pinned Cosmos 3.0.89 and QEMU
TCG. Evidence: `/var/tmp/zn-stream-accept3` (local/ephemeral); the commands and tested commit
are the durable reproduction boundary.

- Common controller tests: 117 passed, including five Sunbeam algorithm tests.
- Release host tests: 654 passed; Release ISO/ELF and package tool built.
- Login/session/styling: 71 passed; online repository: 92 passed.
- Expanded native programs: passed, including live pre-exit output, serial/desktop/
  local input/EOF/Ctrl+C, binary and split UTF-8 pipes, early reader exit, multiple
  protected stages, FP state, separate stderr, simultaneous errors, ordinary-user
  permissions, same-kernel executable upgrade and independent ext2/content audits.
- Kernel update/recovery: 169 passed, including interrupted stages/menu replacement,
  corrupt-root refusal, activation/confirm/rollback and broken-kernel recovery.
- Additional checks: 12 pinned-dependency adapters; 11 package-builder tests;
  signed tool four passed/one optional native-bridge test skipped; three meta tests.
  Utility algorithms passed through both GCC and Clang compiler branches.
- Focused fresh-install cancellation regression passed in 69.268 s: five early/
  blocked cancellations, managed cancellation, error conversion, eight stages,
  early consumer exit, usable shell and clean shutdown, without fail-fast.

| Common gate phase | Wall time |
|---|---|
| Host tests | 8.428 s |
| Kernel build | 21.694 s |
| Package tool | 1.043 s |
| Native login-session-smoke.py | 200.62 s |
| Native repository-smoke.py | 330.228 s |
| Native native-process-smoke.py | 382.505 s |
| Native kernel-update-smoke.py | 496.721 s |

Reproduce from a clean checkout of the tested revision:

```sh
ZENITH_VALIDATION_ACCELERATOR=tcg python3 -B deploy/distributor/validate.py --keep /var/tmp/NEW_EVIDENCE_DIRECTORY
```

The candidate was rebuilt without `--existing`, and every test gate passed. The
aggregate command then failed while retaining VM images across filesystems: the
chosen `/tmp` destination is a 16 GiB tmpfs and filled as sparse images expanded.
The complete original workspace was moved on the main filesystem to `/var/tmp`,
and only the failed partial copy was removed. Final artifact-hash and clean-source
checks were completed manually and passed; `retention-recovery.json` records both
the original failure and recovery. The aggregate CLI exit is not represented as
success. No product gate was skipped or overridden. Use a roomy regular filesystem
for evidence, rather than a small tmpfs.

## Handoff and remaining boundaries

Source and immutable package tags were pushed with operator approval. On the
installed VM, upgrade the kernel first, reboot and `boot confirm`, then update
Sunbeam/manuals. Verify representative commands, permissions and repeated Ctrl+C
through the actual VMware/local/desktop path; local QEMU success does not establish
operator installation. The stream capability prevents old kernels installing the
new tool bundle. Canonical input buffers are invocation-scoped, not a POSIX TTY.
The exact stale TCP/NAT cause and legacy PS/2 profile issue remain separately open.

Artifact hashes verified after evidence recovery:

- ISO SHA256: `55dafe57d93ac6f80d3d1587ae8d91aa5235f5e10c4eefd799b87f4f1e33ecbb`
- ELF SHA256: `2014484b8cd37843e4bfdb5000c9aac9d9434de017958ae7eefcde291cfd3070`
