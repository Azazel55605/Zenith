# C4 release qualification and parallel CI

This is evidence recorded at publication. Current acceptance lives under
[C1–C4, Q2 and H1 in the master roadmap](../../ROADMAP.md).

## Qualified source and tags

The annotated tags `kernel/v0.0.44`, `sunbeam-tools/v0.7.0` and
`zenith-gui-probe/v0.2.0` were pushed together at
`85020296572df70adc5c436e06f7ab8eb0442ab7`.
Kernel/runtime sources are unchanged from the C4 implementation at `5924c3f`;
the subsequent changes repair and parallelize qualification.

[CI run 41824046258](https://github.com/Azazel55605/Zenith/actions/runs/41824046258)
passed its host/controller tests, Release build/adapters, all 14 native gates,
all 14 system-integration groups and its final coverage aggregate. It ran from
11:49:24 to 12:22:51 UTC on 2026-11-04: **33 minutes 27 seconds** including
setup, compilation, guest tests and evidence upload. This is one hosted run,
not a guarantee for other runner speeds or queue conditions.

The aggregate checked every gate's source and both shared artifact hashes:

| CI artifact | SHA256 |
|---|---|
| Zenith.elf | `e71f2dfa8beebb1bec46a87f03d10ce4a19879a64e7d876d167384bfbc479a7c` |
| Zenith.iso | `63c1708c655f7c63209ebe38f93df8715041ec6b24dadebd771ddef0fce3ea62` |

The distributor rebuilds and qualifies its own artifacts. These hashes do not
attest to a distributor-built binary or an operator-installed VM.

## Local evidence

Fresh unsigned Release acceptance in the pinned `c4f7b65` checkout passed all
14 mandatory native gates, 469 controller tests and 853 host tests without result
reuse. Its distinct local ELF/ISO hashes are recorded in
`obj/c4-pretag-044-final/artifacts.json`:

- ELF: `d41693f1f9d84b6a80af372b216f50f51c208b9d2f86202bab1f96f6823ca906`.
- ISO: `bc27ece3475c36df1fdff3de8aa967e8b482a5166d2d965134979ddb350546ef`.

The final controller suite passed 483 tests. Targeted real-VM requalification
passed 67 graphical checks, 81 login/session/styling checks, and the focused
four-CPU process/terminal path. That focused process run was not a substitute
for the full process/64-pipeline/managed-root gate, which passed in final CI.

## CI implementation and observed failures

The [workflow](../../../.github/workflows/ci.yml) builds one Release ISO/ELF,
binds it to the source commit, then schedules up to eight native and six system
groups on separate runners. The native matrix comes from the distributor's
mandatory gate plan. Only basic boot and its disk-only UEFI follow-up retain an
ordered shared fixture. The final required check rejects incomplete, duplicate,
failed or mismatched gate results. Default distributor/local acceptance still
runs the entire native plan.

NuGet packages and pinned Cosmos tool downloads are cached. Guest passes,
kernel build outputs and mutable workload state are not cached between runs.
Successful jobs retain diagnostics/screenshots; failed jobs additionally retain
disposable replay binaries/disks, excluding generated private signing keys.
Failed jobs can be rerun independently within the same source/build run.

Hosted qualification established and repaired these concrete environment and
observation issues before tagging:

- Ubuntu's rolling runner no longer supplied the pinned linker's `libxml2.so.2`.
  Jobs now use Ubuntu 24.04, install the dependency and check the real linker.
- The disk-only UEFI constructor lacked the shared helper's keyboard argument;
  the persisted security fixture still expected an unauthenticated desktop.
  Both now follow the real keyboard and graphical-greeter contracts.
- New isolated workers lacked `xorriso`, previously inherited from the combined
  build environment. It is now an explicit dependency, prerequisite and hashed
  input to distributor result identity.
- The correct German slash output was embedded in a scheduler line and erased
  by filtering. The test now observes a complete, fresh logger record after the
  same actual Shift+7 injection.
- A graphical probe's exit preceded its queued terminal command completion.
  The test now waits for the final pasted command and checks their original
  order, without resending input or inferring success from a prompt.
- Process installation reached 100% copy but exceeded a generic 120-second
  command limit. Live setup now uses one CPU and a scoped 300-second install
  budget; installed protected execution and stress retain four CPUs with
  `smp=native`. The corrected local install completed in 55 seconds. Dedicated
  one/two/four-CPU and default-disabled coverage remain mandatory.

## Operator boundary

Tag publication starts the distributor's independent build/sign/publish path.
Its delivery checks and actual installed four-vCPU VM observations remain
separate acceptance. Activate workers with `smp=native`, confirm the new boot
generation after updating, and follow the [multicore contract](../../MULTICORE.md).
