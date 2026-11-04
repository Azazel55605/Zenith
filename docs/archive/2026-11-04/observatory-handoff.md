# Observatory-to-Polaris fixture handoff (U7/Q2)

## Evidence

The distributor 0.0.45 graphics gate passed `G_OBSERVATORY=0` and timed out on
its next `G_POLARIS=0` command. The retained serial extract establishes that
Observatory did close:

- launch script completed at guest time 157.842;
- native surface retired at 160.845 (3.003 seconds later);
- closing motion finished at 161.737.

The immutable tagged helper sent Escape and slept 1.5 seconds before typing the
next public script. That delay did not acknowledge client retirement or Polaris
focus/input readiness. CPU samples of renderer/idle/monitor methods during the
later timeout do not establish a scheduler deadlock. The newer d05d092 helper
adds a client focus diagnostic and a retirement wait, but those source changes
alone do not alter the unchanged tagged harness or payload used by the server.

## Controller correction

`validation-graphics.py` observes only the known public Observatory fixture. The
host's surface-open message follows Windows.Open/Focus, supplying admission
acknowledgement even for the old client. The controller sends exactly one
Escape, waits for retirement, then requires an actual blinking caret and the
public ANSI user/cwd prompt before returning to the tagged helper. Unknown,
changed, unretired, frozen or private-prompt state fails without replay. Both
Performance and Processes invocations are handled independently. Other Escape
keys, input and fixture result assertions stay unchanged.

The shared prompt observer now requires foreground brightness for blue cwd text;
the recovery Terminal's dark blue background is not cwd evidence. No private
launcher output is exposed and no client binary or kernel is rebuilt.

The policy is installed by the current distributor controller when loading the
old graphics harness. Its rendered-mouse marker survives the tagged helper's
idempotent monitor-policy installation. Receipt identity includes this controller
module, so prior-input success is never silently reused.

## Exact replay

The copied clean setup disk from
`release-dz5t1sn4/native/g/setup/disk.img` contains the server ELF:
`a517720802cd362d12418b580bcc73b6f92625de35e182ff822524d60d823763`.
The fixture's kernel bytes were extracted and compared before replay. A separate
copy booted four-vCPU TCG with `smp=native`; signed Polaris/Observatory packages
and public scripts already on the disk were reused. Neither OS installation nor
kernel/client compilation ran. The original fixture remains unchanged.

The focused replay passes CPU warmup, both old Observatory views (the second with
whole-app materials), both post-close Polaris commands, clean shutdown and an
independent ext2 audit. It confirms the client has no FOCUSED marker. Evidence:
`obj/observatory-045-replay.log`, `/tmp/zn-observatory-045-e/` in the main workspace.
The complete unchanged tagged desktop sequence also passes: 40 graphical checks,
clean shutdown, exact kernel identity and independent ext2 audit. Full evidence:
`obj/observatory-045-full-replay.log`, `/tmp/zn-observatory-045-full/`.
All 517 controller tests pass, including both handoffs, missing admission,
unretired/frozen/private-prompt refusal and immutable-helper installation.
Distributor-host verification remains pending; local replay does not establish
installed VMware acceptance.

On the distributor, after updating the current controller, use:

```sh
PATH="$PATH:/usr/sbin:/sbin" ZENITH_VALIDATION_ACCELERATOR=tcg \
python3 -B tools/observatory-handoff-replay.py \
  --build "$HOME/.local/state/zenith/distributor/build-kernel-v0.0.45"
```

`--full` runs the unchanged tagged desktop assertions and audit too. Explicit
`--disk`, `--elf` and `--harness` select an active workspace instead. The tool only
chooses clean setup fixtures automatically and verifies their exact ELF before
booting an independent copy. No tag replacement or new kernel version is needed.
