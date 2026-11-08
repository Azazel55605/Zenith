# Process GUI raw-fault command delivery (Q3/U1)

## Server evidence and boundary

The 0.0.48 native-process gate times out at `N_GUI_EARLY_RAW_FAULT=139`.
The operator's post-password extract from
`release-37k11h3q/native/u/desktop/serial.log` contains successful
`N_GUI_EARLY_RAW=0`, collection #2 (306606 objects freed), a pressure collection,
and then `N_GUI_EARLY_RAW_FAULT=2`. QEMU is terminated by the harness rather than
exiting spontaneously. This is an observed wrong status, not evidence of a VM
freeze or an uncompleted native fault.

Sunbeam's exact `--tty-fault` branch returns 1 if raw admission fails, otherwise
performs the deliberate invalid write and should terminate at 139. It cannot
return 2; Sunbeam's usage/argument path and shell syntax errors can. The command
and history existed only in the stopped Terminal; the original harness retained
no final Terminal image. A lost/misencoded character is consistent with the
status, but the precise damaged argument and origin cannot be established from
this extract. Do not infer a kernel fault-containment failure from status 2.

## Initial fault-command guard

`public-fault-caret-v1` wraps only the two exact public GUI fault-command strings
in the unchanged native-process harness. It waits for the focused public prompt,
sends one character, waits for a real caret blink at its advanced position, then
sends the next. It retains the complete command screen before sending Enter once.
No character, command or fault operation is repeated or corrected. A missing
acknowledgement aborts before submission and preserves phase/character-count
metadata plus the screen. The expected 139 assertion remains unchanged.

Credentials, arbitrary text, canonical/raw payloads, zero-delay EOF, Ctrl+C/D and
local-console checks keep their original transport. Matching requires the exact
public GUI command and live registered `gui-monitor.sock`; no shell, keyboard,
raw-input cleanup, allocator or guest binary changes are included. This paces
input by actual rendering rather than guessing a longer fixed sleep. The stored
screen also distinguishes a remaining wrong-command failure from an ABI defect.

## Validation

Unit cases cover ordered one-time character/Enter submission, failure before
Enter without replay, refusal of nonpublic prompts and noninterception of
credentials, local-console commands and stream input. All 562 controller tests
pass, including documentation checks. The unchanged `--terminal-only` gate passes
under TCG with four installed CPUs in 254.947 seconds against the existing matching
0.0.48 kernel. Both GUI labels return the required 139, both terminal-restoration
checks pass, and the unmodified local-console sequence also passes. The complete
pre-Enter command image was visually inspected. Evidence:
`/var/tmp/zenith-process-048-public-fault`; log:
`/tmp/process-048-public-fault.log`. ELF SHA256 remains
`ccc340280f38b733092d47baac57717ab8a7ffb45624e42c78789172033af26d`.

This focused gate includes graphical/local canonical, raw, fault, bounds and
hidden-input checks; it does not replace full native-process release acceptance.
The original server corruption was not reproduced or recovered as an exact typed
line. The change hardens delivery and preserves evidence for any remaining failure;
it does not establish the root cause of every keyboard-loss path. Original
fixtures and tags are preserved.

On 2026-11-08 the operator confirms the focused `--terminal-only` gate passes on
the distributor after controller repair `547f972`. This establishes server-side
focused GUI/local terminal acceptance; it does not establish a passing full
native-process gate, complete release publication or installed-VM acceptance.


## Complete public GUI command entry

The next full distributor run at `release-pd5mocyc/native/u/desktop/serial.log`
fails earlier waiting for `TTY_RAW_READY` in `N_GUI_EARLY`. The safe extract
contains `N_GUI_EARLY_CANCEL=130`, collection #2 (310105 objects freed), and
pressure collection at 211.228s, with no raw-reader output/status. The v1 guard
covered only fault commands and could not establish delivery of this launch.
The cause of missing readiness remains unproved without the command screen and
process state. The earlier focused pass was not full release acceptance.

`public-command-caret-v2` covers an exact allowlist of canonical pipeline launch,
cancellation pipeline launch, cancellation-status read, raw-reader launch, raw
fault and terminal-bounds commands for `N_GUI` and `N_GUI_EARLY`. It observes the
focused public prompt and each typed character before Enter, retains each command
screen, and sends no key twice. Payload transport, literal control bytes,
local-console commands, credentials and immediate EOF timing are preserved.
Existing readiness, 130/139/0 status and restoration assertions remain unchanged.

Caret color is calibrated from a real blink with foreground/background contrast
in either appearance. A unique moved solid two-pixel caret can acknowledge a
character without waiting for another blink; unchanged, short or ambiguous shapes
are refused. Normal and bottom-row wraps are supported. Local testing caught a
pointer covering one caret pixel: the guard stopped before Enter although that
key had arrived. The keyboard-only fixture now uses one planned vertical move to
park the pointer in the top band, observes that it is clear, then reobserves the
prompt. It sends no clicks or corrective motion. An irrelevant exact top-left
coordinate is not required for legacy PS/2 delivery. These observer issues are
not reported as kernel defects.

Public raw-input timeout capture uses the actual `gui-monitor.sock`, retaining
the final screen and bounded read-only CPU/scheduler state as `PROCESS_INPUT`.
Password/authentication markers are excluded. This evidence cannot pass a gate.
All 570 controller tests pass, including exact command allowlisting, static-caret
calibration/advancement, unacknowledged-input refusal, unchanged payload/credential
transport, one planned pointer move and private-marker diagnostic exclusion. The
unchanged focused GUI/local gate passes locally under TCG with four installed
CPUs in 279.692s. Evidence: `/var/tmp/zenith-process-048-public-topband`; log:
`/tmp/process-048-public-topband.log`. The full unchanged native-process gate also passes under TCG with four installed
CPUs in 794.425s, including all 64 stream checks, held-root/fault/permission cases,
package upgrade, independent persistent-content/ext2 audit, both GUI sequences and
local-console recovery. Evidence: `/var/tmp/zenith-process-048-public-full`; log:
`/tmp/process-048-public-full.log`. The existing kernel remains unchanged at
SHA256 `ccc340280f38b733092d47baac57717ab8a7ffb45624e42c78789172033af26d`.

This qualifies the complete local process path rather than just a focused rerun.
The server's original incomplete launch was not recovered as a command image;
root cause of every keyboard-loss path remains open. Distributor full retry and
installed acceptance remain separate. The v2 input policy and failure artifacts
make remaining delivery versus runtime failures observable without weakening the
guest readiness/status assertions or changing the immutable release tag.
