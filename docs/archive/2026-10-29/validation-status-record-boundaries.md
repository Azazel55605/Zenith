# Status-record boundaries — 2026-10-29

Owner: Q2 in [master roadmap](../../ROADMAP.md). Controller-only repair; preserve
immutable `kernel/v0.0.28` and its kernel artifacts.

## Distributor evidence

After the standalone Settings pass, the normal parallel retry failed during setup
at `S_FETCH_010zpk`. The HTTP fetch reached 100%; the controller waited 300.016
seconds and reported status unavailable. Its read-only readbacks did not repeat
the fetch. QEMU was alive. The setup guest had no graphical monitor, so the generic
Settings CPU snapshot's monitor-unavailable result supplies no guest-stall proof.

The retained log at
`release-irymmdw4/native/a/setup/serial.log` contains four complete logger records:
`S_FETCH_010zpk=0` at guest times 17.500, 47.158, 77.538 and 107.927 seconds. The
first was the real operation result; the others were saved-status readbacks.
`fetch complete bytes=243953` also appears. A direct offline invocation of the
server's `observation` parser nevertheless returned `observed_status=None`.

The operator then supplied the actual normalized text. Debug-line removal had
removed the only newline separating the progress/echo from the following logger
record. Examples became:

```text
...100% 0.2/0.2 MiB[zenith] [   17.500] user: S_FETCH_010zpk=0
... | logger[zenith] [   47.158] user: S_FETCH_010zpk=0
```

The parser correctly requires a record boundary; the normalizer had erased it.
The original parser reproduces this false failure locally with the captured
progress/debug/status arrangement. This establishes a controller parsing defect,
not a failed TCP transfer, slow package write or unavailable guest status.

## Repair and validation

Before removing debug output, strip ANSI sequences and compile the exact requested
numeric status record. When a removed debug record ends immediately before that
complete record, preserve its original terminating newline. Continue removing
other debug fragments so records split internally by known debug output still
reassemble. Keep anchored status matching, exact names, complete numeric values,
terminators, nonzero results and original deadlines. Never invent a separator
where the raw stream had none, accept a prompt as success, repeat the operation or
relax the tagged harness assertions.

Regressions cover progress and command-echo prefixes, the captured logger layout,
plain results, 0/1/130 values, split labels/digits, missing terminators, numeric
junk, embedded echoed records and end-to-end waits. A completed fetch now needs
neither readback nor another guest operation; a nonzero result remains failure.
All 329 common controller tests passed in 33.289 seconds, including 40 status
regressions. Documentation validation passed for 99 files and 43 work IDs.
Operator offline verification against the complete retained log remains pending.

The original earlier VM-registry selection error was not reproduced by the
operator's passing standalone gate and remains unexplained. The separate local
restricted-CPU launcher-retirement failure is also not repaired by this parser
change. The normal distributor matrix and installed VM still require acceptance.
