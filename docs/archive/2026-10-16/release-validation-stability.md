# Common kernel release acceptance — 2026-10-16

Source baseline: `758d7af` (controller-only changes on top); existing product
ISO/ELF from kernel 0.0.13. No product tag/version was changed.

## Problem and changes

Recurring release failures crossed different layers: native startup bugs,
UART/status damage, environment/acceleration differences and inconsistent test
budgets. These are not all compiler failures. The previous fixes remain; this
slice reduces the gaps that let regressions reach a long server build first.

The audit found CI omitted login/session acceptance, ran the repository gate
last and did not run the new authentication policy regression tests. Distributor
kernel builds ran C# tests but no Python controller regressions before compilation.

[validate.py](../../../deploy/distributor/validate.py) provides one unsigned
candidate command. It runs the common controller regressions, checks native tools,
firmware and accelerator configuration, runs Release host tests, builds kernel
and package tool, then calls release.native_checks directly. There is no second
native gate list: current order is login/session, repository, native programs,
kernel crash/recovery. `--existing` accepts explicitly prebuilt output-x64 files.
All gates still run and changed artifacts are refused; SHA256 evidence is retained.
Default pre-tag mode requires a clean source commit and refuses source changes
during acceptance.
Neither mode signs, publishes, creates a tag or reuses success to authorize signing.

The distributor runs current-controller regression tests and native prerequisites
before expensive kernel work, including for older immutable product tags. CI
uses the same controller set and native plan, waits for the host/controller job
before compiling, and also triggers on kernel tags. Existing optional native
CPU-worker integration coverage remains separate. Failed evidence is retained;
fixture private key directories are excluded from CI uploads.

## Validation

- Common controller set: **99 tests passed** (bounded waits, status/transport,
  accelerator/profile, unchanged-tag adaptation and new acceptance orchestration).
- Release orchestration/signing/publication fixtures: **18 tests passed**.
- Release C# tests: **620 passed** through the actual new entry point.
- Common TCG login/session gate: **69 checks passed, 244.119 s**.
- Common TCG repository gate: **92 checks passed, 521.918 s**, including combined
  update/download, real new-kernel reboot, confirmation/rollback and clean ext2.
- Common TCG native-program gate: **passed, 321.663 s harness wall time**.
- Common TCG kernel recovery gate: **169/169 checks passed, 1090.548 s** harness
  wall time, including interrupted staging/menu publication, ISO undo, real new
  ELF activation, confirmation/rollback and graphical retained-kernel fallback.
- Full unsigned command exited **0** after all four mandatory gates; ISO/ELF
  SHA256 values remained unchanged. Final ext2/FAT and secret-free transcript
  audits passed. The full run used the existing-artifact path; the default
  clean-build/source-movement guard is covered by controller regressions.
- Recovery authentication actually took 44.472 s and 48.670 s in two cases:
  existing larger budgets were preserved and real completion was observed.
  The fast controller suite was rerun after preflight/source-guard refinements;
  native harnesses and the successful existing-artifact path were unchanged.
- Python syntax, workflow YAML/dependency inspection and diff whitespace checks pass.

The first sandboxed release-test attempt could not write its disposable /var/tmp
workspace. Repeating with the required filesystem access passed all 18 tests;
no product workaround was made for the sandbox boundary.

## Reproduction and limits

```sh
python3 -B deploy/distributor/validate.py --controller-only
ZENITH_VALIDATION_ACCELERATOR=tcg python3 -B deploy/distributor/validate.py --keep /var/tmp/zenith-candidate-001
```

Use a new evidence directory and a clean committed candidate before tagging.
The full validation recorded here uses `--existing --keep /tmp/zn-stability-acceptance`:
controller changes do not require rebuilding the unchanged product. The source
provenance of prebuilt artifacts is the caller's responsibility, not a signing
attestation. Avoid concurrent builds in one checkout. Short runtime socket paths
are still allocated independently in /var/tmp and retained under the evidence
folder at completion/failure. The full command uses disposable disks and fixture
keys, not package-server settings or production signing keys.

Tested artifact SHA256 values:

```text
Zenith.iso  3c250aea93bfe253ee31a973615a4f0818e1b5d6401f678985c5947f911c909c
Zenith.elf  958663b667fc05da43d8ff7e1599d95e6f8e643e8ad8d60ed53b14d7de90d8c7
```

Ephemeral evidence: `/tmp/zn-stability-acceptance` (phase-times.json, artifacts.json,
phase logs and retained native disks/transcripts); the outer log is
`/tmp/zenith-stability-acceptance.log`. These paths may expire; this record retains
results, artifact identity, commands and source baseline.

Hosted CI, server deployment/publication and installed acceptance are not implied
by local success. The operator is retrying kernel 0.0.13 independently; no success
was reported while implementing this slice. This improves prevention and early
failure detection, not guest I/O speed, universal hardware support or guaranteed
absence of future kernel bugs. Full distributor checks before signing remain.
