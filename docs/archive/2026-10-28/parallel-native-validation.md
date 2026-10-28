# Parallel native validation and retry receipts — 2026-10-28

Owners: Q1/Q2/Q3 in [master roadmap](../../ROADMAP.md). Controller-only work;
kernel/client source, assertions, signatures and existing tags remain unchanged.

## Execution

Independent gates use isolated disposable disks, monitors, firmware variables,
HTTP ports, signing fixtures and logs. Automatic sizing selects up to four workers,
reserving one CPU and 1 GiB for the host plus 1.5 GiB per gate. Override with
`ZENITH_NATIVE_JOBS=1..8`; resource limits still bound the selected count. Serial
mode retains the former order. Settings/login run first for early feedback; the
remaining gates use longest-first scheduling so process stress and kernel recovery
overlap. Nothing is omitted. Failure stops new launches; already running gates
finish and retain independent passes. Interrupting the controller terminates its
worker process groups before scratch evidence is moved.

The PackageTool is built before the pool. Each harness checks its exact DLL hash
and reuses that prebuilt tool; only the known Release build command is bypassed.
Changed hashes fail closed and altered build flags execute normally. Parallel
reporting uses phase-bound rows, serialized JSON/timing writes and active/failed
phase selection, rather than overwriting whichever row was appended last.

## Retry reuse

Only fully successful gates receive private atomic receipts under
`obj/distributor/native-receipts`. Keys cover pinned source, exact ELF/ISO hashes,
controller policies, relevant input policy, SDK/PackageTool and compiler/emulator/
filesystem tools, firmware, Python/Pillow, validation options, host boot/kernel and
KVM access. Source/artifacts/controller/tool output are rechecked before retaining
passes. Logs are copied and hash checked; malformed, modified, expired or incomplete
receipts are misses. Failures never receive receipts. GUI input-policy changes
invalidate GUI gates; unrelated gates remain reusable when all other inputs match.
Receipts are local to this host/boot and expire after seven days, with 128 retained.
They are not transferable certification or a bypass for failed assertions.

Native retries preserve valid completed passes; the first run after installing this
feature cannot infer receipts from historical unversioned logs. A new source or
changed artifact requires new results. `ZENITH_VALIDATION_FRESH=1` forces all gates;
`validate.py --keep ...` always forces a fresh full pre-tag acceptance run. The
existing build already preserves identical outputs on unchanged rebuilds: two
local Release publishes produced byte-identical ELF and ISO files, enabling exact
receipt reuse rather than trusting only a tag name.

## Local evidence

Four actual gates ran concurrently on the unchanged qualified 0.27 ELF:

| Gate | Seconds |
|---|---:|
| System-app style | 38.8 |
| Materials | 85.5 |
| Graphics | 158.9 |
| Native network | 279.6 |

The group finished in approximately 280 seconds versus 563 seconds of summed gate
time. This is a group benchmark, not a promised fourfold full-suite improvement or
a distributor measurement. All four passed; their independent dashboard rows and
logs remained distinct. Artifacts and original fixtures were preserved.

Regression coverage includes overlapping workers/resource limits, preflight ordering,
fail-fast scheduling with retained running passes, serial compatibility, exact-input
receipt invalidation/tampering/expiry, failed-gate retry, prebuilt-tool validation,
process-group cancellation, per-phase reporting and dashboard active/failure views.
All 297 shared controller regressions passed. The separate
[Settings input failure](input-acknowledgement.md) remains a diagnosis task;
parallel execution does not claim to cure lost GUI input.
