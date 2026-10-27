# Kernel 0.0.13 repository-stage investigation

**Status:** local reproduction passes; the reported server failure remains under
investigation. Controller diagnostics and gate ordering are improved. No kernel
binary, immutable tag, timeout or signing/acceptance requirement changed.

## Evidence

The operator reports `RuntimeError: P4_KERNEL_STAGE=0` from repository-smoke.py.
This occurs after compilation, while awaiting a guest system-update result. The
first retained tail contains scheduler ticks and GC output only. It proves guest
activity, not the stage, exit status or selected accelerator. Filtered server
output and the failing wait metric were requested; no server cause is inferred.

Run against the existing 0.0.13 binary, using explicit TCG and disposable images:

```sh
ZENITH_VALIDATION_ACCELERATOR=tcg python3 -B deploy/distributor/validation-runner.py \
  tools/repository-smoke.py /tmp/zn-repo13-repro
```

All **92 repository checks pass** in **450.243 seconds**. `P4_KERNEL_STAGE` passes
in **216.511 seconds**, within the existing 600-second limit. This includes full
HTTPS signed kernel/data delivery, staging, actual reboot/hash identity,
confirmation/rollback, metadata failures, package transactions, authentication,
clean shutdown, independent ext2 audit and secret-free transcripts. Runtime log:
`/tmp/zenith-repo13-repro.log`; disposable images: `/tmp/zn-repo13-repro`.
This run started before the controller diagnostic edits; it establishes behavior
of the existing product binary, not server acceptance or a product fix.

## Controller changes and checks

- Report observed/expected exit status, last known update phase, process exit and
  serial evidence path. Persist structured fields in failed wait metrics.
- Reject a complete unexpected status promptly rather than spending the remaining
  deadline trying to recover an already known result. Never repeat the mutation
  or manufacture success from progress, prompts or saved observations.
- Run login, repository and protected-native gates before the long kernel recovery
  matrix. Preserve every required gate, legacy-tag handling and failed evidence.
- Expose only recognized structured progress fields; keep raw serial/secrets private.

**50** status/profile/accelerator/validation-controller tests pass; **38** release
and poller tests pass. The release tests need normal access to their disposable
`/var/tmp` workspace; sandbox read-only failure was rerun with that access.

A real disposable installed-guest probe with the new adapters rejects actual
status 1 when 0 is required, records the reason in wait metrics, accepts a
subsequent actual status 0, and powers off cleanly. Evidence:
`/tmp/zenith-status13-probe.log`, `/tmp/zn-status13-probe`. The failure is detected
after the existing initial 30-second observation window. Regression cases also
cover a nonzero status appearing later while an operation was initially busy.

## Remaining action

Obtain the filtered server update output and actual accelerator/timing metric
before deciding whether to repair guest behavior, server policy or observation.
Pulling the controller fixes preserves the existing 0.0.13 tag and signing keys.
Do not claim the server failure is fixed from the local pass. See
[active progress](../../ROADMAP.md#current-focus-and-execution-order) and [diagnostic runbook](../../../deploy/distributor/AUTOMATION.md#repository-kernel-staging-failures).
