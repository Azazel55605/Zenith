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

## Controller candidate

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
