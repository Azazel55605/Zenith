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
explicitly selects the trusted prior implementation. Kernel changes only extend
the dispatch list; no new syscall or runtime adapter is introduced. New algorithms
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
- [ ] Commit the final candidate and pass clean-source common release acceptance.
- [ ] Distributor signing/delivery and operator-installed acceptance.

No failed workload is automatically repeated. Retained initial fixture:
`/tmp/zn-text-guest`; build/host logs `/tmp/zn-text-{build,host}.log`.
