# Native-window device IRQ forwarding — 2026-10-29

Owners: H1/U1/Q2 in [master roadmap](../../ROADMAP.md). Kernel source candidate;
immutable `kernel/v0.0.28` is unchanged. Local evidence only: no clean-source full
qualification, publication or installed VMware acceptance yet.

## Operator report

On the two most recent kernels in VMware, all keyboard and mouse input sometimes
stops; no panic; a Terminal cursor can keep blinking. No single trigger (window
movement, scrolling) was identified.

## Reproduction

Exact qualified 0.0.28 ELF (`5fc6d12a…`) on a copy of the
`obj/028-settings-ready-final` desktop fixture, QEMU 11 with KVM, PS/2 keyboard and
mouse (VMware's default devices), Settings open, scripted wheel bursts and title
drags through the monitor. All three attempts froze within two rounds:
keys and pointer motion ignored, clock and scheduler advancing. Under TCG ten
rounds did not reproduce (timing dependent).

Frozen state: IOAPIC IRQ12 line asserted, local APIC ISR/IRR empty. The 8042 held
an unread mouse byte; its edge-triggered line could produce no further edge, and
the shared output buffer also blocked the keyboard.

## Root cause

Host KVM tracing (`trace-cmd`: `kvm_pio` 0x60/0x64, `kvm_set_irq`,
`kvm_ioapic_set_irq`, `kvm_apic_accept_irq`, `kvm_inj_virq`, `kvm_eoi`):

- 1136 normal IRQ12 deliveries: inject vector 0x2c → guest reads port 0x60
  (median 23 µs) → EOI (minimum 21 µs).
- The final delivery: inject 0x2c → EOI after 4 µs, no port read.

A second trace with `kvm_entry`/`kvm_exit` showed the interrupt arrived at user
RIP `0x4023e6` (the native Settings program) and the EOI write came from `z_eoi`
in `src/Native/x64/Process.s`, followed by `z_exit`. `z_bridge` acknowledged every
user-mode device IRQ and returned to the program without running its driver. Any
stateful device IRQ during a native window was lost: fatal for the 8042, and
plausibly the cause of the earlier unexplained missed virtio notification.

## Change

- `z_bridge`: timer vectors 32/239 keep acknowledgement and budget accounting.
  Other device vectors go to `z_device_yield`, which saves a resumable frame (as a
  service request does; the interrupted RAX is the resume result), records the
  vector and leaves through `z_exit`. The invalid-user-stack exit records device
  vectors the same way.
- `z_exit`, after restoring the managed CR3/IDT/CR4/MSRs/segments/FP, delivers a
  recorded vector through its original Cosmos stub with a synthesized kernel
  interrupt frame (16-byte aligned like a CPU frame, IF clear) returning to
  `z_restore_registers`. The Cosmos driver reads the device and sends EOI; it may
  reschedule as for any interrupt in managed code. The runner still holds the raw
  entry gate, so no other native window can replace the bridge globals first.
- `NativeProcess`: status 4097 resumes the unchanged frame without consuming the
  service-call budget.

## Local validation

Release ELF SHA256 `dc8333141e9de2902818d466107c6bf6f83d602ca295e510a5add15e0800ab3b`,
staged as its own generation on a disposable fixture copy (the kernel refuses a
mismatched `kernel=` identity, which invalidated a first attempt that overwrote the
old file; that panicked run is not counted).

- Reproducer: 2 × 12 rounds without a stuck line; afterwards keyboard input
  reached Settings/Terminal and a typed `logger` command completed.
- `native-settings-smoke`: 96 checks passed. `native-graphics-smoke`: 48 checks.
- `native-process-smoke`: first run failed `N_STREAM_ERR` only because the exact
  error line completed an unterminated `[SCHED]` trace record (that record split
  2/458 times, versus 0 in about 1950 in three qualified earlier runs: forwarded
  IRQs add legitimate preemption points). `tools/guest_pipeline.py` now also
  accepts the exact line after a known trace tag; two regressions cover the
  retained slice and reject unknown prefixes. The rerun passed all 212 checks.
- 349 controller tests and 782 host tests passed.

Evidence: `obj/irq-gates/`, traces `/tmp/claude-1000/zvm/kvm-run1.dat` and
`kvm.dat` (temporary). Remaining: clean-source full qualification of all ten
native gates, kernel release, distributor and installed VMware acceptance.

## Clean-source release qualification

Unchanged source `760c5fe` passed fresh unsigned acceptance
(`deploy/distributor/validate.py --keep obj/irq-qualified-760c5fe`, four native
jobs): 349 controller tests, host tests, kernel build, runtime adapter and package
tool, and all ten native gates — login 242.6s, Settings 305.0s, network 285.6s,
repository 454.4s, CLI 256.7s, graphics 189.6s, materials 109.2s, system app
styling 39.2s, native processes 751.3s, kernel update 798.6s (overlapping; not
summed). An earlier run at `b2fa649` passed every gate but was correctly refused
because documentation was committed while it ran.

- ELF SHA256: `5e4d25adcdb5553b6f2c6f7d03c673e5b09f7a90642c5932005f66f7f9b61d5a`
- ISO SHA256: `1b1a19a166fd922533bad90fc47df8eaa5db3b57237921be121c0e0009f1eb14`

Tag `kernel/v0.0.29` was pushed at `760c5fe`. Distributor build and installed
VMware acceptance remain pending.
