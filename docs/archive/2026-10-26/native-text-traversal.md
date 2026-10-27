# Native text and traversal extraction

## Scope and implementation

After the operator confirmed 0.0.21 and its four CLI/manual packages built,
installed, boot-confirmed and passed the VM checklist, this slice extracts the
remaining existing text/traversal algorithms into signed protected executables.

| Package | Added ordinary command dispatch |
|---|---|
| zenith-base-tools | grep, sort, uniq, seq, printf, tr, cut, sed, diff |
| zenith-fs-tools | find, tree, du |
| sunbeam-tools | Explicit access to the same multicall algorithms |

Normal dispatch retains signed ownership/mode/digest enforcement. Missing packages
use recovery builtins; a present invalid executable refuses. `recovery COMMAND`
explicitly selects the trusted prior implementation. Kernel changes extend the dispatch list and include the narrow HTTP correction
described below; no new syscall or runtime adapter is introduced. New algorithms
use the existing 0.0.21 ABI. Shell evaluation and xargs stay trusted because they
launch commands; network ownership/services and graphical clients remain separate.

## Supported utility profile

The owning source is `userland/sunbeam/text.c`, `regex.c` and `traversal.c`.
These are freestanding C, without libc, host subprocesses or managed callbacks for
text algorithms. File access still crosses the caller-authorized filesystem ABI.
All stdin/file handles close on error; native faults/cancellation retain process
cleanup. Compiler-reported stack use is checked against the 64 KiB native stack;
larger text routines are not inlined into main's frame.

- Line filters accept at most 8 KiB per line, reject NUL, and terminate output
  lines with LF. Byte ordering/ASCII case folding are intentional; UTF-8 bytes are
  preserved but locale collation and Unicode case/character indexing are absent.
- `grep [-invE] PATTERN [FILE...]`: fixed substring by default, line numbers and
  multi-file labels, statuses 0 matched / 1 none / 2 error. `--` ends options.
- `sort [-r] [FILE...]`: stable merge sort, 64 KiB storage including terminators,
  2048 lines; no partial sorted output on input/size failure. `uniq [FILE...]`
  removes adjacent repeats across input boundaries.
- `seq [FIRST [STEP]] LAST`: checked signed 64-bit integers, nonzero step,
  at most 10000 values; overflow stops safely. `printf` repeats a format only if
  it consumes arguments: %s, %d, %i, %%, newline/tab/CR/backslash escapes.
- `tr [-d] SET1 [SET2]`: byte ranges/escapes, binary-safe streaming.
  `cut -d BYTE -f LIST` or `cut -c LIST`: positive singleton/range selections,
  including open ends, up to 64 ranges. `-c` selects byte positions. Lines lacking
  the field delimiter pass through. Unsupported options fail explicitly.
- `grep -E`/`sed` use a bounded Thompson NFA, leftmost-longest search, 256 states,
  patterns up to 256 bytes, grouping depth 24 and four million active-state
  steps per search. Supported syntax: literals, escaped punctuation, `.`, byte
  classes/ranges/negation, `^`, `$`, grouping, alternation, `*`, `+`, `?`.
  Captures/backreferences, counted repetition, named POSIX classes, lookarounds
  and .NET extensions are refused. This is a documented native profile, not a
  replacement implementation of every .NET regex feature in recovery builtins.
- `sed [-n] SCRIPT [FILE...]`: up to 8 semicolon-separated commands, numeric or
  /regex/ addresses, d/p and s/PATTERN/REPLACEMENT/[g]. Replacement supports full
  match `&` and escaped delimiters/ampersand/backslash; capture replacement is
  refused. Replacement output stays within the line bound; zero-width global
  matching always advances.
- `diff FILE1 FILE2`: bounded LCS, classic change/add/delete hunks, missing final
  newline reporting; 32 KiB including terminators / 256 lines per file. Statuses
  are 0 equal / 1 different / 2 error.
- `find [PATH...] [-name GLOB] [-type f|d|l]`, `tree [PATH]`, `du [-s] [-h] [PATH...]`:
  no final-symlink recursion, natural VFS entry order, at most 1024 visited nodes,
  depth 15 and 1024-byte paths. Glob supports * and ?. Tree uses ASCII branches;
  du reports rounded logical-file KiB, including the legacy accepted -h flag.
  Limit/permission failures return nonzero and close every held directory handle.

The kernel's existing per-process request, transfer, CPU and pointer bounds still
apply. GNU option compatibility, full terminal/locale services and arbitrary
process spawning are not implied.

## Validation

- 700 C# host tests passed; Release ISO/ELF built.
- 17 C algorithm tests pass, including 48 seeded regex/filter comparisons with an
  independent reference, error statuses, malformed syntax, zero-width replacement,
  integer overflow, multi-file inputs, binary tr, missing final newline, symlink
  cycles and traversal-depth refusal. GCC and Clang freestanding builds pass.
- First expanded installed TCG fixture passed 100 command checks, ordinary-user
  denials, signed dispatch, same-kernel package upgrade, raw/fault/cancel cleanup,
  executable tamper refusal and independent ext2 audit (224.049 s). Later small
  argument-bound/unsupported-regex/trailing-root normalization changes are covered
  by host regressions and must pass the final full qualification.
- [x] Final clean-source common release acceptance passes at `6615ecb`; exact evidence below.
- [ ] Distributor signing/delivery and operator-installed acceptance.

No failed workload is automatically repeated. Retained initial fixture:
`/tmp/zn-text-guest`; build/host logs `/tmp/zn-text-{build,host}.log`.

## First full qualification: optional-index error masking

The first clean candidate d998bf2 passed 206 controllers, 700 host tests, its
Release build and all 71 login/session checks. Repository refresh later received
HTTP status 404 for the deliberately absent optional index-v3.zri. The generic
GET retry retried this definitive missing-resource response; that second request
failed with "TLS transport closed unexpectedly", replacing the 404 signal required
for signed legacy-index fallback. The interactive update returned to the shell;
the harness subsequently failed waiting for its confirmation prompt. Text packages
were not installed in this fixture. Evidence is retained at
/tmp/zn-text-qualified-d998bf2/native/r/installed.

The correction preserves a definitive HTTP 404 without replaying it. Other bounded
GET transport retry, certificate verification and the refusal to fall back from
an already established extended catalog remain unchanged. A decoder/retry-policy
regression covers 404 preservation, transport retry exhaustion and cancellation;
the real HTTPS fixture now requires exactly one missing-index request per repo
before the first legacy fallback. This corrects the evidenced error masking; it
does not establish the cause of every TCP/TLS close. No failed update is replayed
to manufacture a successful qualification.

The corrected real HTTPS fixture passed all 94 repository checks, including
combined kernel activation, protocol-downgrade refusal and independent ext2 audit
(318.236 s). All 701 host tests and the corrected Release build pass. Full clean
qualification of the committed correction remains required.

## Final clean-source qualification

Source: `6615ecb20a56ed9075eeb422e2b31830eb1602e2`. The common unsigned acceptance
command rebuilt clean source and verified its commit, clean state and artifact
hashes again after every gate. Local environment: QEMU 11.1.2, TCG, Cosmos 3.0.89,
.NET 10. Evidence directory: `obj/text-qualified-6615ecb`.

| Gate | Result | Wall time |
|---|---|---|
| Common controllers | 206 passed, including 17 algorithm tests | recorded in controller-tests.log |
| Release host | 701 passed | 9.9 s |
| Release ISO/ELF | Built | 24.1 s |
| Login/session | 71 passed | 203.5 s |
| Repository | 94 passed, including one-request fallback and downgrade refusal | 328.9 s |
| CLI packages | 100 checks, mode restoration, tamper refusal and ext2 audit | 225.8 s |
| Native processes | Full matrix, all 64 pipelines and GUI/local input | 752.0 s |
| Kernel recovery | 169 passed, ext2/EFI audits and manual fallback | 488.0 s |

Artifact SHA256:

- ISO: `4ac885900adfdaba7fd7fb0fd56c57d6408d60422832a61ea37157f5ccb195fe`
- ELF: `6b728b1736d35d8e4db4a7704ea2b8e450d3a8809fed34ff33cbc6f6f0702798`

Pushed delivery tags at this exact qualified source: kernel/v0.0.22,
sunbeam-tools/v0.6.0, zenith-base-tools/v0.2.0, zenith-fs-tools/v0.2.0 and
zenith-man-pages/v0.1.4. The atomic source/tag push succeeded; every remote
peeled tag was independently verified at `6615ecb`. Subsequent documentation commits do not change the
qualified source. Distributor publication and installed VMware acceptance remain
pending. Follow the [VM test handoff](../../testing/CLI-TEXT-VM-TEST.md). Network extraction
remains the next CLI slice; the 404 correction does not establish broad TCP/TLS
reliability on every path.

## Installed operator results

On 2026-10-26 the operator supplies the graphical-terminal checklist screenshot.
It shows the expected sort/uniq output, numbered grep matches, descending seq,
field selection, full-match sed replacement, identical-file diff/status 0,
file traversal, logical KiB and tree totals. No-match grep exits 1 and malformed
regex exits 2. The operator confirms the first sed no-op was a typo; the corrected
command prints the expected [aaa]b. These functional checks pass on the installed
VM. The screenshot does not separately display exact package versions, kernel
boot confirmation or distributor publication receipts. Owned networking remains
the next implementation slice.
