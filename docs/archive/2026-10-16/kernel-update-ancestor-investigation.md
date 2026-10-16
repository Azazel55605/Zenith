# Kernel update ancestor failure — investigation, 2026-10-16

Baseline controller `e4b1d46`, immutable product tag kernel/v0.0.13. Product
failure remains unresolved; the independent status-observation fix below is not
a claim that the package can now install on the operator's server fixture.

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
inspection is still pending; this tool-launch failure is not evidence of corruption.

## Next evidence and decision

Run the inspector on the **stopped failed server disk** before another build.
If its required files/directories are absent or malformed, investigate the disk
writer/baseline and compare the prepared image. If they are intact, identify the
live VFS/ancestor lookup failure under the server's runtime conditions. The
read-only inspector does not prove live metadata reads are correct. Do not remove
ancestor/symlink protections or invent a filesystem repair/retry without evidence.
