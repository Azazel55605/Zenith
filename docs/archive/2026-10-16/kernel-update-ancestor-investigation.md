# Kernel update ancestor failure — investigation, 2026-10-16

Baseline controller `e4b1d46`, immutable product tag kernel/v0.0.13. Subsequent
read-only server inspection confirmed root metadata corruption. Matching ext2
buffer and RMW races are reproduced and fixed in committed source; rebuilt native
candidate acceptance passed. The independent status-observation fix below did not
fix the product. No claim of server publication or installed acceptance is made.

## Operator evidence

The entire TCG kernel gate reached normal upgrade after successful staged-cut,
menu-gap and external ISO recovery scenarios. Upgrade boot/authentication passed.
K_UPDATE returned in 37.296 seconds (22.71 QEMU CPU seconds):

```text
verified signed zenith-kernel 1.0.0 sha256=ff3e3a33aa32ab61b71ea16e007809ffb8f8e25e2d42b6620ad40edf40873a97
pkg: linked/missing package ancestor
[zenith] [   99.273] user: K_UPDATE=1
```

Retained server workspace: `release-_ltpswzx/native/k/upgrade` under
`~/.local/state/zenith/distributor/build-kernel-v0.0.13/obj/distributor`.
The old timing row says passed because the prefix arrived; the caller then
correctly rejected exit 1. No failure deadline was reached. NativePackageStore's
Regular check emits this message if an ancestor cannot be read as a directory;
NativeKernelUpdateStore also uses this reader for installed boot metadata/EFI
and retained kernel validation. The error omits which ancestor failed. No staging
progress was reported, narrowing the suspected call path to pre-staging validation;
this alone does not prove disk corruption or a particular VFS/driver defect.

## Independent observation defect and fix

The adapter also returned immediately on a bare NAME= prefix, before requiring
its digits/terminator. A fragmented-record regression failed before the fix
(returning after the first of three fragments). The adapter now drains a complete
actual numeric record within existing bounded windows; scheduler noise does not
extend the overall limit. Lost echoes and partial records do not cause early
readback. Readback still only reads a saved status, never repeats the operation.
Real nonzero prefix results are left for the tagged harness to reject and recorded
in safe guest_status metrics plus explicit exit-code output. No guessed zero,
password replay, changed package signature check, product code or tag move.

## Local validation and limits

- **104 controller regressions pass**, including fragmented prefix/digits/newline,
  lost echo, bounded missing terminator, genuine nonzero status and metrics.
- A disposable clone of the prior local live-install disk fetched the existing
  full signed kernel fixture, trusted its public key and executed the actual
  `pkg kernel install /root/one.zpk`: real K_UPDATE=0 in 69.913 seconds, phase
  kernel-staged, followed by successful boot status and synced poweroff.
  Probe used TCG and current status/result observation; final fragmented-drain
  refinement was then covered by deterministic controller tests. This is staging
  acceptance, not another activation/recovery matrix or reproduction of exit 1.
- New [inspector](../../../tools/inspect-kernel-update-disk.py) verified a healthy
  retained local upgrade disk and all root boot ancestors. Input SHA256 remained
  `a4f74c599cfa1cb29e2ec270b4354ae05d26c551844396227bacafd20bdce058`
  before/after inspection. An invalid empty image failed without repair.
- Prior full common acceptance remains separately documented in the
  [stability record](release-validation-stability.md); local passes do not override
  the operator's failure.

## Inspector environment follow-up

The operator pulled ef95c9d and boot-artifact verification passed, but root
inspection stopped at `Permission denied: debugfs`. The server previously reported
`/usr/sbin/debugfs`; its interactive shell may lack sbin in PATH. The inspector now
resolves executable debugfs via PATH, /usr/sbin or /sbin before inspecting data.
Fallback was verified against the retained local healthy disk with PATH discovery
forced unavailable. No sudo, repair or new kernel is required. Server root metadata
inspection was then completed as recorded below; this tool-launch failure itself
was not evidence of corruption.

## Server disk comparison

The operator inspected both stopped images in `release-_ltpswzx/native/k`:

| Path | prepared/disk.img | upgrade/disk.img |
|---|---|---|
| `/boot` | directory inode 13 | directory inode 13 |
| `/boot/efi` | directory inode 14 | directory inode 14 |
| `/var` | directory inode 27 | directory inode 27 |
| `/var/lib` | **directory inode 49** | **regular inode 49** |
| `/var/lib/zenith` | directory inode 50 | unavailable |
| `/var/lib/zenith/boot` | directory inode 51 | unavailable |
| `installed.tsv` beneath boot | regular inode 52 | unavailable |

GPT, EFI, kernel SHA256, root UUID and rescue menu passed on both disks.
`generations.tsv` was absent in both; its absence in the initial baseline is
allowed. Root corruption therefore appeared after preparation, before normal
upgrade validation. The ancestor guard correctly refused it. Preserve these
images; neither inspection repaired or mounted them.

## Reproduced mechanisms and fix

`Ext2Superblock` reused one `_inodeBuf` for inode reads and metadata writes.
A read of another table block between an RMW read/write replaced the entire
buffer. The writer then changed its own slot and wrote unrelated neighbouring
slots over the original block. A deterministic interruption reproduced a
directory becoming a regular file. Per-call buffers prevent that overwrite.
They alone do not protect two RMWs of the same block: a forced second writer
lost its directory UID update (expected 456, actual 0) when the first resumed.

A reentrant per-volume monitor now spans complete directory, file, allocator,
metadata and sync operations. It protects retained bitmap/indirection/file scratch
buffers and inode cache too. Explicit success/catch release avoids Cosmos's
unwind-finally limitation. Statistics take the same gate. Inode-table buffers
remain independent to tolerate nested reads within an operation.

The valid formatted regression fixture places a directory at inode 49 and regular
files in neighbouring inode-table blocks. Both metadata regressions pass with the
fix, as do cross-thread exception release and parallel file creation/indirect
writes followed by remount/content/checker audits. All **624 Release host tests**
pass, including existing permission/fault and allocation-budget coverage.

These tests prove an actual matching driver defect and its repair. They do not
observe the exact server thread schedule or establish that an already damaged
image can be repaired. Native validation must use a rebuilt product binary;
retrying immutable 0.0.13 cannot acquire the C# fix. Do not remove ancestor checks,
replay mutations automatically or move the old tag.

## Rebuilt native candidate validation

Product/observer fix commit: `c622c13`. Release publish completed with the new
driver. The first build attempt lacked
`cosmos.patcher` on PATH; adding the installed `/home/azazel/.dotnet/tools`
directory resolved the host environment issue without changing dependencies.
The common unsigned acceptance runner is testing these rebuilt artifacts with
forced TCG (`--existing` because source is still under development):

```sh
ZENITH_VALIDATION_ACCELERATOR=tcg python3 -B deploy/distributor/validate.py \
  --existing --keep /var/tmp/zenith-ext2-acceptance-20261016
```

ISO SHA256: `458a9e467f949b65cd064d0c60ee7dccde6fabc7633b5a12c634904bf77ab676`.
ELF SHA256: `9181304d02dac49751e0c394f62db434467075e067ca542f77659b8125475723`.
The first runner passed 104 controller tests, 624 Release host tests, package-tool
build and all 69 login/session checks (250.572 s), including independent ext2 audit.
Repository search then completed with a real exit 0, but scheduler text consumed
its status line and the following console prompt in the observer's noise filter.
The observer incorrectly treated the completed operation as busy. This disposable
run was deliberately interrupted and its evidence retained; it is not a passed
repository gate or a product filesystem deadlock.

The observer now preserves console lines and checks raw prompt evidence after
the exact unique command suffix, including a scheduler fragment on the prompt
line. A delayed previous prompt cannot mark a new operation complete. Prompt
evidence permits only read-only saved-status recovery; complete numeric status
is still mandatory, and the operation is never replayed. Both new regressions
pass within **106 controller checks**. Against the retained real serial log,
search is correctly classified as no longer busy while its damaged status remains
unknown until readback.

The second run passed 69 login/session checks (244.92 s) and the actual combined
kernel update: P4_KERNEL_STAGE=0 in **204.054 s**, followed by activation and reboot
persistence. It reached root password setup at the end of the repository gate.
That completed too, but `guest_auth` discarded the complete message because it
shared a scheduler line (`[SCHED] Tpassword updated for root`). Its unchanged
180-second deadline then failed; the real result and completed prompt were in
the retained log. This was not evidence of slow hashing or failed account storage.

Authentication observer revision 2 (`ce42cfd`) accepts an exact newline-terminated result
from either the raw or noise-filtered stream and checks explicit rejection in
both views. Username prefixes, partial records and prompt-only evidence still
fail. Three new regressions cover the real prefix, rejection and incomplete/wrong
identity; **109 controller checks** pass. Credentials are never resent and hashing
or deadlines were not weakened. Immutable 0.0.13 predates `guest_auth`, so its
controller receives the current helper directly rather than nesting an old one.

Full common acceptance restarted with the same binary at
`/var/tmp/zenith-ext2-acceptance-20261016-v3` and passed. No source edit to the driver
occurred after building these artifacts.

The normal upgrade in this run returned **K_UPDATE=0 in 37.758 s**, then booted
the exact new ELF, preserved user data and confirmed with the previous generation
retained. Read-only inspection of the stopped prepared and upgrade images gave:

| Path | Fixed prepared baseline | Fixed stopped upgrade |
|---|---|---|
| `/var/lib` | directory inode 49 | directory inode 49 |
| `/var/lib/zenith` | directory inode 50 | directory inode 50 |
| `/var/lib/zenith/boot` | directory inode 51 | directory inode 51 |
| `installed.tsv` | regular inode 52 | regular inode 64 |
| `generations.tsv` | absent | regular inode 63 |

Both images passed boot-artifact verification. Metadata file replacement during
activation explains their changed inode numbers; the required directory ancestors
remain intact. Inspection did not mount, repair or modify either input. This is
local candidate evidence, not a rerun on the retained failed server fixture.

## Final acceptance and release boundary

Forced TCG, x64, rebuilt Release ISO/ELF, common `validate.py --existing` path:

| Gate | Result | Wall time |
|---|---|---|
| Controller regressions | 109 passed | see controller-tests.log |
| Release host tests | 624 passed | 15.609 s |
| Package tool | passed | 2.002 s |
| Login/session/styling | 69 checks passed | 244.522 s |
| Online repository | 92 checks passed | 437.215 s |
| Native programs | passed, including permissions, faults, timeout and upgrades | 252.525 s |
| Kernel recovery | 169/169 checks passed | 692.138 s |

The final gate includes both durable power-cut checkpoints, external ISO recovery,
refusal of a corrupt root, actual healthy ELF activation, confirmation, retained
rollback, a signed broken ELF entry and real boot-menu/manual previous-generation
fallback. Final independent e2fsck and EFI FAT audits passed; credentials were
absent from transcripts. The acceptance runner checked the ISO/ELF hashes were
unchanged through the gates and exited successfully. No package was signed or
published by this unsigned acceptance run.

The next product candidate needs a new version (0.0.14); no such tag has been
created here. Pull the current controller on the distributor before creating the
tag, preserving immutable 0.0.13 and the original failed evidence. Server gates,
publication and operator-installed acceptance remain separate. This fix prevents
the reproduced races; it does not repair an already corrupted filesystem or
provide general power-loss atomicity.
