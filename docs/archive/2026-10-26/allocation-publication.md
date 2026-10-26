# Allocation publication during CLI release qualification

## Reproduced failure

Clean-source qualification of `c594419` passed 700 host tests, 194 controller
checks, the Release build and all 71 login/session checks. Repository acceptance
then returned `P4_KERNEL_STAGE=1` after a complete download, with
`expected bounded x64 ELF installation kernel`. This was a real error, not a
missing status or exhausted staging timeout.

The served and retained cached archive both had SHA256
`b904219f921d803afee993250aad98445ed5e38ec8eb989f848bb5c7605a7b6e`.
Host verification accepted its signature and payload. Twelve read-only checks on
an independent copy of that failed disk also passed, including a pressure GC.

A replay pausing at successful copy/validation sites passed all 92 repository
checks. A second replay observing **only the rejection branch** reproduced the
failure: the 16,255,162-byte payload array had its correct array method table and
length but zero bytes where the ELF header belongs. Only public ELF/header bytes
were inspected. The evidence establishes intermittent in-memory corruption;
network data, on-disk package corruption and a timeout do not explain this run.

Initial kernel ELF SHA256:
`c5faf93217b64cbd789436bc28a3973cf179a3a90ba081ba9e8eecc0887e6433`.
Evidence: `/tmp/zn-cli-qualified-c594419`, `/tmp/zn-kernel-read-c594419`,
`/tmp/zn-repo-copy-observe`, `/tmp/zn-repo-reject-observe`.

## Confirmed runtime gap and candidate correction

Inspection of the actual compiled/pinned runtime shows that `AllocObject`
restores interrupts before `RhpNewArrayFast` writes the method table and array
length. The same publication gap exists in all six object/array allocation entry
points. A pending timer can therefore let the scheduler/pressure collector see
reserved heap storage that is not yet a valid object. This is a concrete runtime
invariant violation; its connection to the observed corruption was tested with the unobserved repository gate after correction, as recorded below.

`AllocationPublicationFixup` adapts build copies of Cosmos 3.0.89. It retains an
outer IRQ scope through allocation **and** header/length initialization, preserving
the previous IRQ state. Explicit success/catch cleanup restores the state even if
allocation throws; it does not rely on Cosmos unwinding `finally`. Runtime export
names/signatures remain on the wrapper. The underlying runtime bodies are retained
as private helpers. NuGet packages and the upstream checkout are unchanged.

The adapter validates all six bodies/signatures and header writes before changing
a module, refuses layout drift, and is idempotent. A fixture executes the actual
pinned entry bodies with a simulated pending IRQ at allocation return. Original
bodies expose uninitialized objects; adapted bodies publish before restoring IRQs.
Eight tests pass, covering all six entries, disabled IRQ state, exceptions, negative
length, ARM64 metadata, drift rejection and repeated adaptation. This host fixture
proves the publication contract; it is separate from native VM evidence.

## Additional package compatibility

Separate native-only fixes preserve normal shell use for multi-file `wc` totals,
short/multi-file `head`/`tail`, listing regular-file operands and multi-file stat.
Stat uses no-follow metadata for its final component. Eight C algorithm tests and
75 installed CLI checks passed against the unchanged initial kernel. These fixes
require no additional kernel ABI.

## Native verification and remaining acceptance

- [x] Real repository update passes without debugging: all 92 checks, actual
  combined kernel activation/reboot and independent ext2 audit (356.286 s).
- [x] Clean-source full qualification passed at `58528671877cd59f1fffb68b890393041ea06b83`; see the exact evidence below.
- Retain kernel-first installation order: old kernels lack the new CLI capability
  names. Refresh/stage the kernel, reboot/confirm it, then update/install tools.
- Do not publish or tag the failed `c594419` candidate.

## Exact release qualification

`deploy/distributor/validate.py --keep /tmp/zn-cli-qualified-5852867` rebuilt
clean source `58528671877cd59f1fffb68b890393041ea06b83` and passed on local
QEMU 11.1.2, TCG, Cosmos 3.0.89 and the .NET 10 toolchain. Source cleanliness,
commit identity and artifact hashes were verified again after all gates.

| Gate | Result | Wall time |
|---|---|---|
| Common controllers | 197 passed | 9.0 s |
| Release host | 700 passed | 9.6 s |
| Release ISO/ELF | Built | 25.9 s |
| Login/session | 71 passed | 199.2 s |
| Repository | 92 passed, including real signed kernel staging/activation | 307.2 s |
| CLI packages | 75 passed, plus raw/fault/cancel restoration | 177.5 s |
| Native processes | Full matrix, all 64 byte pipelines and GUI/local raw input | 715.9 s |
| Kernel upgrade/recovery | 169 passed, independent ext2 and EFI audits | 479.2 s |

All 64 dependency-adapter regressions also passed before qualification, including
the eight allocation-publication tests. No debugger or workload retry was used
in the full acceptance run.

Artifact SHA256:

- ISO: `c15477631b6fbcf26f6201a3a536910c2fd9f324de20b6a61063ef5c9cc0d3b3`
- ELF: `940150c8718216b29b40f83f404259d35b9edc237cdb669cf512005a64f680ce`

Release tags target this exact qualified source: `kernel/v0.0.21`,
`sunbeam-tools/v0.5.0`, `zenith-base-tools/v0.1.0`, `zenith-fs-tools/v0.1.0`
and `zenith-man-pages/v0.1.3`. The atomic source/tag push succeeded and every
remote peeled tag was verified at that commit. Later documentation commits do not change the
qualified tag target. Local acceptance is unsigned; distributor signing/delivery
and installed VMware acceptance remain separate. Follow the
[VM test handoff](../../CLI-VM-TEST.md).
