# CLI process and filesystem foundation

Work started **2026-10-26** after the operator confirmed kernel 0.0.20 installed
and running. The selected order is remaining CLI packages, then GUI Settings and
a shared UI design language. This candidate is not a released kernel.

## Changes and decisions

- Preserve the serialized protected user window on the BSP. Add a bounded process
  registry, exported environment and cooperative cancellation, without introducing
  managed multicore or changing the scheduler fix qualified in 0.0.20.
- A terminal has one native foreground input lease. Raw input belongs to that
  lease; returning, faulting or cancelling releases it and restores canonical
  input. The same channel/VT key encoder serves COM1, local text and GUI terminals.
- Add four background shell jobs per session, with EOF stdin and no terminal
  ownership. Jobs inherit copied cwd/environment and captured credentials. `jobs`,
  `wait %JOB`, `kill %JOB`, `$!` and owned native PID cancellation expose lifecycle.
  Ctrl+C cancels the foreground; closing a session requests cancellation of all
  its jobs. Account/session changes wait for background jobs to finish.
- Add caller-authorized namespace/metadata operations, no-follow stat, private
  descriptor seek and safe copy destination opening. Validate complete user
  request/pointer ranges before mutation. Same-inode copies refuse before truncate;
  recursive copies refuse descendants and recursive removal refuses mount roots.
- Build `zenith-base-tools` and `zenith-fs-tools` independently, with separate ELF
  paths, version tags and distributor recipes. A shared multicall source is used;
  the executables are installed/updated and verified independently of kernel bytes.
  New ABI capability dependencies prevent installation on an incompatible kernel.
- Installed base/file executables handle their ordinary shell command names.
  Missing executable paths retain the recovery implementation. A present but
  modified/unowned executable fails verification; it never falls back silently.
  `recovery COMMAND ...` explicitly invokes the trusted builtin.
- Existing Sunbeam tags remain retryable: only recipes with an explicit program
  selection pass the new generator argument to their pinned source.

## Native command coverage in this batch

| Package | Normal command dispatch |
|---|---|
| zenith-base-tools | cat, tee, wc, head, tail, echo, id, basename, dirname |
| zenith-fs-tools | mkdir, touch, rm, rmdir, cp, mv, chmod, chown, ln, ls, stat, pwd |
| sunbeam-tools | Existing explicit `sunbeam COMMAND` namespace plus new probes/tools |

This does **not** finish CLI unbundling. Remaining text tools include grep/sort/
uniq/seq/printf/tr/cut/sed/diff and file traversal tools. Shell evaluation, xargs,
authentication, package/configuration and device administration remain trusted;
network clients still need owned network service interfaces. No GUI API is added.

The native transfer profile remains bounded: 1 MiB cumulative stream/file writes,
16 file handles, recursive depth 16, existing request/CPU limits. Native cp refuses
symlinks and oversized files; numeric chmod/chown and `ln -s` are supported.
This is a Zenith utility profile, not GNU coreutils compatibility. Extended regex,
PTYs, fork/exec, Unix signal delivery and full VT screen emulation are not implied.

## Validation record

- Host tests: 700 passed after adding environment, process ownership/cancellation,
  terminal mode/key encoding, namespace request and background-job regressions.
- Existing Sunbeam stream host algorithms: 5 passed. Their host shim deliberately
  refuses namespace mutations; filesystem safety is exercised in the guest.
- Distributor polling regressions: 23 passed, including independent recipe/version
  pinning and backward-compatible generator arguments. All 194 common controller tests pass, including the 18
  distributor release tests and mandatory CLI gate ordering.
- Release ISO/ELF builds passed. Initial installed TCG CLI run passed 36 command
  checks, raw byte/fault/cancel restoration and root/mutation protections.
- Expanded installed TCG run passed 68 command checks, including package
  dispatch, same-kernel upgrade, malformed namespace requests, private seeks,
  native PID cancellation, ordinary-user denials and refusal of unowned/tampered
  executables. Raw UART input preserves high bytes and NUL. Independent ext2 audit passed.
  The initial native candidate completed all 64 byte pipelines plus native
  filesystem/FP/fault/cancel/permission checks and an independent ext2 audit.
  Focused GUI/local acceptance passed raw arrow/literal control input, fault-mode
  restoration and canonical EOF/cancellation (206.030 s). This used
  `--terminal-only`; it does not replace the default full gate before a tag.
- The first expanded fixture stopped early because its observer accepted a
  historical `ps` entry before listing completed. It now waits for the command's
  completion marker before selecting an active PID; no workload retries added.

No new tag or distributor/installed-VM success is claimed by this record. Full
clean-source release acceptance is still mandatory before a new kernel tag.


## Reproduce the focused checks

```sh
python3 -B deploy/distributor/validate.py --controller-only
dotnet test tests/Zenith.Tests -c Release
dotnet publish -c Release -r linux-x64 -o output-x64
ZENITH_VALIDATION_ACCELERATOR=tcg python3 -B deploy/distributor/validation-runner.py \
  tools/native-cli-smoke.py /tmp/zn-cli-NEW
ZENITH_VALIDATION_ACCELERATOR=tcg python3 -B deploy/distributor/validation-runner.py \
  tools/native-process-smoke.py /tmp/zn-terminal-NEW --terminal-only
```

Use new evidence directories. The mandatory default native process gate still
runs all existing checks plus GUI/local raw input; common release acceptance now
also runs the focused CLI package gate. Controller tests include recipe/release
regressions in CI and distributor pre-build checks. No test repeats a guest
mutation to recover a missing status.
