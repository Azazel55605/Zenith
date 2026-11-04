# C4 CPU gate: preserve raw UART text offsets

The 0.0.44 distributor failed `4-native distinct actual CPUs run 1` after an
immediate `CPU_TEST=0` wait (0 additional serial bytes). The retained extract
showed four matching APIC/actual identities and `overlap=True` for the preceding
batch, followed only by the next command echo.

`Machine.log()` used `Path.read_text()`, translating CRLF to LF. Its callers
passed `len(log())` to `wait()`, whose incremental byte reader decoded raw CRLF
without translation. Every CRLF shortened the supplied offset by one character,
allowing a repeated marker to match an earlier batch. Identity assertions then
correctly refused the missing current worker records.

Direct helpers and current distributor policy preserve raw CRLF in log snapshots.
The controller installs this policy for immutable tagged helpers; guest commands,
CPU identity/result/overlap checks and time limits remain unchanged. No kernel
or tag change is required.

Validation on 2026-11-04:

- 14 CI/controller regression tests, including stale repeated-marker rejection and
  current-controller repair of an old helper.
- 40 status and 17 profile regression tests passed.
- Fresh TCG CPU acceptance: **106 checks**, 1/2/4 native, four disabled, two/four
  fault cases; both repeated four-core batches passed. Wall time 103.883 seconds.
  Local evidence: `obj/cpu-offset-native.log`, `/tmp/zn-cpu-offset-fixed`.
- Local run used the working display-candidate kernel; CPU implementation is
  unchanged from C4. Exact distributor 0.0.44 verification remains pending.

Follow-up: the first wrapper replaced in-memory transports as well as file-backed
UARTs, breaking four controller fixtures that intentionally have no serial file.
The wrapper now preserves their original `log()` and installs idempotently. This
is a controller integration correction; real UART normalization is unchanged.
An isolated copy containing only this correction passed the **complete 486-test
controller suite** in 112.022 seconds, including documentation and publication
failure tests. Evidence: `/tmp/zenith-cpu-controller-fix-final.log`.
