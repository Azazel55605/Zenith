# Tag-driven distributor

Implemented host automation; deployment on the Debian VM remains operator-run.
The VM fetches Git every fifteen minutes after the previous poll completes. It
does not merge/reset the operator's checkout. A private bare cache and disposable
detached checkouts live under ~/.local/state/zenith/distributor.

## Release contract

The administrator-maintained recipes.json selects package identities and payloads:

| Tag | Package | Repository |
| --- | --- | --- |
| kernel/v0.0.7 | zenith-kernel 0.0.7 | core |
| zenith-docs/v0.1.0 | zenith-docs 0.1.0 | extra |

Unmapped tags and ordinary commits do not publish anything. Add a recipe when a
new independently installable package is ready. Compiled apps remain kernel-linked.
Every selected tag must resolve to a commit reachable from origin/main. Versions
are independent across packages; use a new version for each changed release.

The configured Git origin and its maintainers are trusted. These tags are not
cryptographically authenticated release approvals. Protect main and release-tag
creation in your Git hosting policy. Never permit untrusted PR code on this signing
account. Building and signing currently share zenith-release; worker/signer
isolation is future work. No private keys go into Git or public CI.

Kernel builds run host tests, Release compilation, actual QEMU kernel-update and
online repository acceptance before signing. QEMU supports TCG fallback when nested
KVM is unavailable; allow hours for tests on this VM. Data recipes run host tests
and the existing format/signature/publication checks. Data-package-specific native
behavior tests are future recipe work. All releases then undergo full delivery
verification through the configured HTTPS proxy.

Signed indexes are renewed after a day even when no new tags exist (seven-day
validity). Polling/network/signing failures can prevent renewal: inspect logs and
state rather than assuming expired clients will keep installing. Publication and
verification do not install or reboot any Zenith guest.

## Enable on the existing VM

First deliver this committed source to ~/Zenith, either through Git or the existing
source-bundle workflow. Source must be available on origin/main before tags can
trigger builds. Future releases require Git commits/tags, not copied archives.
Keep the already-configured distributor JSON, TLS PEM and signing keys unchanged.

As zenith-release:

```sh
cd ~/Zenith
bash deploy/distributor/release.sh doctor
python3 -B deploy/distributor/poll.py
```

The first poll processes all mapped namespaced tags it finds. Do not create tags
for existing releases with different source provenance. Run the first poll manually
before enabling unattended runs; it may build releases already tagged upstream.

As the administrator:

```sh
sudo install -m 0644 deploy/distributor/zenith-distributor.service /etc/systemd/system/
sudo install -m 0644 deploy/distributor/zenith-distributor.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now zenith-distributor.timer
systemctl list-timers zenith-distributor.timer
journalctl -u zenith-distributor.service -n 100 --no-pager
```

The supplied units assume /home/zenith-release/Zenith and the existing release
account. Adjust those paths before installation if yours differ. To change the
interval, edit OnUnitInactiveSec in a timer override and restart the timer. The
six-hour service limit kills its entire process group; interrupted releases resume
from recorded/published state on the next poll. The timer never overlaps itself;
the poller also uses a nonblocking state lock.
See the [Debian timer documentation](https://manpages.debian.org/trixie/systemd/systemd.timer.5.en.html)
for interval and activation semantics.

## Create a release

From the development checkout, after committing and reviewing the source:

```sh
git push origin main
git tag -a kernel/v0.0.7 FULL_REVIEWED_COMMIT -m 'Zenith kernel 0.0.7'
git push origin kernel/v0.0.7
```

Replace the example commit/version with the intended release. A docs package uses
zenith-docs/v0.1.0 instead. Do not move/reuse tags or published versions. The local
0.0.7 archive is a candidate: if choosing a server-built 0.0.7 instead, do not
distribute/install the local candidate as the same release. If either variant has
already been distributed/installed, preserve that exact artifact and choose a new
version for a new build.

## Results and recovery

For long native runs, prefer a service-owned retry over keeping a foreground SSH
shell open. As zenith-release, with no active poll:

```sh
python3 -B deploy/distributor/poll.py --queue-retry kernel/v0.0.7
sudo systemctl start --no-block zenith-distributor.service
```

This queues one recorded tag for explicit retry; the service consumes the request
and runs independently of the SSH session. Its existing six-hour limit still
applies. Keyboard cancellation and SIGTERM/SIGHUP now record Interrupted and a
reason instead of leaving only a Building record. Explicitly interrupted tags
wait for another requested retry; hard-killed older Building records remain
detectable as abandoned. A passed test count printed in a phase log does not
replace the worker's successful exit status or imply signing/publication completed.

An optional [web dashboard](DASHBOARD.md) provides timer values, Run now, build
status/logs and repository inventory. CLI inspection and explicit retries remain
available below.

releases.json records tag, pinned commit, timestamps and pending/building/complete/
failed status. Full per-tag logs sit beside it. Failed checkouts retain phase logs
and validation disks; successful checkouts are removed after verification. Complete
tags are skipped, moved tags refused. A normal failure waits for an explicit retry:

```sh
python3 -B deploy/distributor/poll.py --retry kernel/v0.0.7
```

Retry reuses that automation-owned checkout and its ignored compiler outputs only
when HEAD still matches the pinned commit, source is clean and no local Cosmos
override exists. Changed source/pins are refused without deleting the evidence.
Successful checkouts are still removed; this cache does not span different tags.
If publication already
succeeded, it verifies the existing immutable release and renews indexes rather
than rebuilding it. A mismatched receipt source commit is refused. No existing
archive is overwritten. Never delete state to bypass a tag/version mismatch.

For a controller/harness fix, update ~/Zenith without moving the original package
tag. Stop the timer and wait for/stop the service before retrying as zenith-release:

```sh
sudo systemctl stop zenith-distributor.timer
sudo systemctl stop zenith-distributor.service
git pull --ff-only
python3 -B deploy/distributor/poll.py --retry kernel/v0.0.7
sudo systemctl start zenith-distributor.timer
```

Restore the timer after the attempt even if it fails. A source-code fix to the
package itself requires a new commit/tag/version instead of moving the old tag.
Successful retry clears the previous error/finish fields. Run now performs an
ordinary poll and does not silently retry failed tags.

Direct poll.py invocations now add ~/.dotnet/tools and the standard sbin paths to
PATH before running child builds. On an older controller, export
PATH="$HOME/.dotnet/tools:$PATH:/usr/local/sbin:/usr/sbin:/sbin" before a manual
retry. A missing-tools error at preflight does not mean the tools need reinstalling
if doctor/service builds already found them. Per-tag logs append across attempts;
read the latest failure rather than an older traceback earlier in the same file.

Native acceptance uses an actual short /var/tmp/zn-* workspace so QEMU serial and
monitor sockets stay within Linux AF_UNIX path limits, including when a tagged
older harness resolves its keep path. Phase logs stay in obj/distributor/release-*;
See the [Linux Unix-socket pathname limits](https://man7.org/linux/man-pages/man7/unix.7.html).
native evidence moves into that release directory's native/ subtree on success or
failure. /var/tmp avoids consuming a tmpfs with large disk images. Stale sockets
are removed before moving artifacts across filesystems. A hard-killed service can
leave the short directory; its path is printed in the per-tag log. Inspect it only
after the service has stopped and preserve useful failure evidence before cleanup.

## Diagnosing a stalled native byte pipeline

The current controller captures failed `N_STREAM_BYTES_*` / `N_STREAM_FP_BYTES`
waits before the tagged harness stops QEMU. `pipeline-stall.json` is retained next
to that VM's serial log; its bounded numeric report is also printed into the phase
log. It contains three instruction/stack address and flags samples, exact tagged
ELF hash/symbols, and a validated Cosmos numeric scheduler snapshot when available.
For `_native_cpu_halt+1`, it also reads one ABI return-address word and keeps it
only when the exact ELF verifies a direct `CALL _native_cpu_halt` at that site.
The guest can move between reads, so this is an observation rather than a frozen
backtrace; other stack words and general registers are not read.
No password/general-register contents or arbitrary guest memory are copied. The
capture is read-only and cannot turn a failed gate into a pass. An unavailable
monitor/debug buffer does not hide the original failure.

To reproduce only the native gate against the retained 0.0.19 build, from the
updated controller checkout (no signing/publication or dashboard-state changes):

```sh
zenith_native_keep=$(mktemp -d /var/tmp/zn-XXXXXX)
PATH="$HOME/.dotnet/tools:$PATH:/usr/sbin:/sbin" ZENITH_VALIDATION_ACCELERATOR=tcg \
  python3 -B deploy/distributor/validation-runner.py \
  "$HOME/.local/state/zenith/distributor/build-kernel-v0.0.19/tools/native-process-smoke.py" \
  "$zenith_native_keep" > "$zenith_native_keep/phase.log" 2>&1
tail -n 80 "$zenith_native_keep/phase.log"
```

For an untagged runtime candidate, rebuild the updated checkout and run the
harness from that same checkout. The retained tag path above deliberately keeps
using the old release binary and will not test a new scheduler correction:

```sh
export PATH="$HOME/.dotnet/tools:$PATH:/usr/sbin:/sbin"
git pull --ff-only &&
dotnet publish -c Release -r linux-x64 -o output-x64 &&
ZENITH_VALIDATION_ACCELERATOR=tcg python3 -B deploy/distributor/validation-runner.py \
  tools/native-process-smoke.py "$(mktemp -d /var/tmp/zn-XXXXXX)"
```

This is a focused candidate test, not full release qualification. Keep a new kernel
tag gated on full clean-source acceptance and actual distributor-path evidence.

When a native pipeline fixture has already failed, replay its retained installed
disk without another build or OS installation. From the updated controller checkout:

```sh
PATH="$PATH:/usr/sbin:/sbin" ZENITH_VALIDATION_ACCELERATOR=tcg \
  python3 -B tools/native-pipeline-replay.py \
  --disk /var/tmp/zn-jERG7N/installed/disk.img \
  --elf output-x64/Zenith.elf --warmup
```

Use the actual retained disposable smoke disk and its matching ELF. This creates
fresh evidence under a printed short `/var/tmp/zn-*/replay` directory, verifies
kernel/ELF pairing, boots a copy and leaves the original disk untouched. Omit
`--warmup` for eight direct pipelines; `--warmup` reuses the current harness's
preceding fault/timeout/EOF/cancel statements and replays public package downloads
and no-op package installs on the clone. `--repeat N` controls repetitions (1..256),
and `--keep NEW_DIRECTORY` selects a new short evidence directory. Existing output
is refused. It needs QEMU/firmware, binutils and debugfs for warmed replay; no
compiler or package signing key is used. It does not qualify a release, publish
anything, or mark the original distributor failure successful.

A pipeline timeout now captures actual typed queue membership, current thread,
ticket/pass/runtime metadata and bounded saved code-frame locations in
`pipeline-stall.json`. Layouts and method tables must match the supplied ELF;
unknown layouts/absent tooling produce a diagnostic error without weakening the
failed check. Two live observations help compare progress. Endpoint changes are
marked, and saved contexts are not a frozen live backtrace. Ready/Sleeping state
alone does not imply queue membership or a stalled scheduler. The same observer
is used by ordinary native builds after updating the controller; a kernel rebuild
is not required just to collect these diagnostics.

Keep the original build and its binaries intact for this investigation. The short
workspace contains fresh disposable disks. A manual gate run does not qualify an
entire release or change its failed distributor record.

### Testing a compiled runtime correction

A retained replay runs the kernel embedded in that disk. To verify a new runtime
fix, build the updated main checkout and run the focused native gate with a fresh
disposable fixture (no publication or release record changes):

```sh
git pull --ff-only &&
dotnet publish -c Release -r linux-x64 -o output-x64 &&
dotnet build tools/Zenith.PackageTool -c Release &&
PATH="$PATH:/usr/sbin:/sbin" ZENITH_VALIDATION_ACCELERATOR=tcg \
  python3 -B deploy/distributor/validation-runner.py \
  tools/native-process-smoke.py "/var/tmp/zn-tickets-$(date +%s)"
```

For the runnable-ticket fix, the build must print `Adapted stride tickets`.
Keep the failed tagged artifacts for comparison. Passing this focused gate does
not replace clean-source full release acceptance or move the existing tag.

## Build efficiency and acceleration

Native acceptance runs the unchanged tagged harness through the current controller's
validation-runner.py. Regular .img copies use independent reflinks when supported,
otherwise sparse copies, with normal copying as a fallback. Copies are never hard
links: guest writes cannot modify the source disk. Firmware files and symlink
semantics keep their normal copy behavior. The local 256 MiB sparse fixture used
8 KiB after copying versus 256 MiB with shutil.copyfile; measured times were
0.002s versus 0.100s. Actual disks and server filesystems will differ.

All native acceptance gates remain. Fresh publication already signs core/extra
indexes, so the poller avoids signing them a second time or immediately renewing
them again. Recovery of an existing publication still verifies delivery and renews
indexes. Daily renewal when idle remains. Each release-work directory now contains
phase-times.json (phase, passed/failed/interrupted status and elapsed seconds);
successful phase messages also include seconds. Compare these timings on the VM
before changing resource allocations.

The device existing does not establish usable KVM. Run this small paused, diskless
probe as the actual release account; it does not change the running build:

```sh
printf 'info kvm\nquit\n' | sudo -u zenith-release qemu-system-x86_64 \
  -machine q35 -accel kvm -cpu max -m 64 -nodefaults \
  -display none -S -monitor stdio
```

Expected: `kvm support: enabled`. If access is denied, the administrator can add
the account to the device's kvm group:

```sh
sudo usermod -aG kvm zenith-release
```

The next systemd service invocation gets the new groups; a manual retry needs a
fresh login. Do not interrupt the current build to apply this. If KVM still fails,
check nested virtualization/CPU exposure in the TrueNAS VM configuration before
adding cores. The existing harness tries KVM then falls back to TCG, using one vCPU
per guest. Extra distributor vCPUs mainly help compilation; native gates run
sequentially to avoid contention and retain their existing timing assumptions.
Doctor now shows release-account groups and device accessibility, but the probe
is needed to distinguish permissions from unusable nested acceleration.

After the current build completes, pull the updated controller with `git pull
--ff-only`. Existing package tags and source pins stay unchanged. Subsequent polls
use the optimizations; no retagging, signing-key change or dashboard reinstall is
needed for this slice.

Monitor disk space: failed native runs retain sizeable images. After recording a
failure, remove obsolete automation-owned failed checkouts when no poll is active;
retain releases.json, per-tag logs, signing keys and published archives. The source
cache retains fetched tags even if they disappear upstream. Stop the timer and
service before maintenance. Actual VM scheduling/build/HTTPS acceptance remains
separate from local orchestration tests.


New source recipes also support zenith-man-pages/vVERSION (generated full manuals),
zenith-fonts/vVERSION (font subsets plus licenses), and sunbeam-tools/vVERSION
(freestanding native executable). Install GCC for the latter. Pull the updated
controller before consuming these tags. The poller automatically reports/cleans
known inactive build evidence under both worker/publication locks; the storage
policy and manual preview/apply command are in docs/PACKAGE-SERVER.md. Immutable
published packages and durable tag pins remain. Current kernel source adds the
protected executable acceptance gate; older immutable tags retain their existing
kernel/repository gates because they predate native ABI 1.

If concurrent Cosmos debug output damages the exact DHCP boot message, the current
controller's validation adapter checks the same guest's actual DHCP online state
and expected IP through a read-only shell probe. It accepts only a unique logger
response after the command, preserves failure for offline/wrong-address/dead guests,
and never retries/reconfigures DHCP. It also reaps VMs after failed boot checks.
This controller fix works with existing immutable release tags; pull controller
updates and queue the failed tag for retry. Tagged source is not rewritten.

## Repository kernel-staging failures

`P4_KERNEL_STAGE=0` is an expected guest exit status, not a compiler diagnostic.
It covers signed catalog refresh, full HTTPS download/verification, installed data
updates and kernel staging. A missing success marker alone does not identify
which operation failed. Inspect the retained `r/installed/serial.log` and
`wait-times.jsonl` before retrying or changing timeouts.

The controller reports a structured failure reason, observed/expected status,
last recognized guest phase and QEMU exit state. These observations also appear
in failed wait metrics and dashboard phase output. Only bounded known progress
fields are exposed; raw guest serial, command input and credentials remain private.
A complete unexpected exit status fails promptly without repeating the update.
Missing output, process exit and operation deadline remain failures; progress or
a shell prompt is never substituted for successful completion.

Current gate order is login/session, repository, protected native programs, then
the longer kernel crash/recovery matrix (optional gates only where the tagged
source contains them). All gates remain mandatory for signing. Earlier immutable
tags retain their product sources; updated controller adapters and ordering apply
when explicitly retrying them.

Check the actual accelerator in the failed wait metric. The service's effective
policy can be inspected with:

```sh
systemctl show zenith-distributor.service -p Environment
```

`auto` attempts KVM then TCG; group membership or an empty QEMU stderr log does not
prove the selected backend. The recorded home-server comparison favored forced
TCG, but confirm the effective policy and retained metrics before attributing a
new failure to nested acceleration. A local pass does not establish server success.


### Bounded progress budget for combined kernel staging

The exact `pkg update --yes` / `P4_KERNEL_STAGE` status operation spans download,
signature checks, data commit, kernel copy, readback and boot publication. The
0.0.13 server run reached 100% copied but hit its old 600-second aggregate deadline
under actual TCG. The controller now gives this one operation a 600-second
**inactivity** limit and an independent **1800-second total** limit.

Only advancing recognized download percentages, copy percentages or later update
milestones refresh the inactivity deadline. Duplicate/regressing progress,
scheduler/GC output and growing log files do not refresh it. An expired deadline
cannot be revived. All other commands retain their fixed deadlines. Actual wrong
exit status and QEMU exit still fail; progress never replaces the expected status,
reboot/hash, confirmation or filesystem checks.

Minute heartbeats show elapsed time, last known phase and remaining idle/total
budgets. At 100% copied they explicitly say the verified staging result is still
pending: copy percentage does not include all flush/readback/publication work.
Wait metrics retain the nominal timeout plus the selected idle/hard budget policy.
This changes test scheduling, not product storage throughput or durability barriers.

After pulling the updated controller, retry the same immutable tag through the
background service when no poll is active:

```sh
python3 -B deploy/distributor/poll.py --queue-retry kernel/v0.0.13
sudo systemctl start --no-block zenith-distributor.service
```

No tag move, new kernel version, signing-key replacement or service reinstall is
required. Server completion/publication remains an operator acceptance step.


## Authentication acceptance policy

Direct harnesses and the current controller share [guest_auth.py](../../tools/guest_auth.py).
The controller also applies this policy to unchanged tagged harnesses. Pull the
controller before queuing a retry; no new kernel tag is needed for this fix.

Secret-entry prompts have a minimum 60-second budget. Password hashing/account
save, successful authentication and authenticated session startup have a minimum
180-second budget; larger explicit caller budgets remain intact. These are fixed
limits: scheduler output, collection or log growth cannot extend them. The reported
0.0.13 native failure used 30 seconds for password-update completion; both input
prompts had already passed. PBKDF2 strength and product code are unchanged.

The policy sends no input and never repeats credentials. Success requires the
actual expected guest result; account results require a complete line and exact
username boundary. Explicit rejection, confirmation mismatch, missing entropy,
guest fatal errors, QEMU exit and deadline expiry remain failures with distinct
safe reasons. Tests expecting rejection still require actual rejection output.

Wait metrics distinguish password entry/confirmation, password-update completion
and authentication, recording both requested and effective seconds. Heartbeats and
failure diagnostics contain phase/timing information, never passwords or raw input.
Retry through the service using the commands above; local acceptance does not
establish server publication or an installed upgrade.


## Graphical input policy for existing tags

The observed-motion-v2 correction was withdrawn after the distributor journal
showed oscillating/stale cursor observations. Current policy never feeds relative
corrections back from screenshots. Exact destinations/deadlines remain unchanged;
on failure it records `input-failure.json`, the current framebuffer and read-only
CPU/scheduler state in `pipeline-stall.json` before QEMU is stopped. The Settings
post-close public script additionally waits for native-window retirement and a
rendered Terminal caret blink; the earlier worker status is not input readiness.
This scoped handoff observation never resends commands or credentials.

To diagnose the failed 0.0.27 Settings gate without recompiling the kernel or
reinstalling the OS:

```sh
PATH="$PATH:/usr/sbin:/sbin" ZENITH_VALIDATION_ACCELERATOR=tcg \
python3 -B tools/settings-replay.py \
  --build ~/.local/state/zenith/distributor/build-kernel-v0.0.27
```

It selects the newest retained setup fixture and runs the tagged assertions on an
independent disk copy with current controller diagnostics. Client fixture packages
may be prepared by the tagged harness; the kernel is unchanged. Keep 0.0.27 immutable.
See [evidence](../../docs/archive/2026-10-28/input-acknowledgement.md).

## Parallel gates and faster retries

Native gates run in separate disposable VMs. Automatic sizing uses up to four
workers within measured CPU/memory limits. To choose a maximum of six:

```ini
# systemctl edit zenith-distributor.service
[Service]
Environment=ZENITH_NATIVE_JOBS=6
```

Run `sudo systemctl daemon-reload` after editing the unit. The next service run
reports its selected worker count. Supported values are `auto` and `1..8`; the
scheduler reserves host capacity. One worker retains serial execution. Shared
PackageTool builds are prebuilt/hash-verified, and dashboard phases remain separate.

Completed native passes are reused on retries only with matching source, ELF/ISO,
policies, toolchain/firmware and host identity. Failed gates are never reused. Cache
receipts/logs are bounded under the pinned checkout's `obj/distributor/native-receipts`.
Old unversioned runs cannot seed this cache. New/changed kernels run their gates;
`ZENITH_VALIDATION_FRESH=1` disables reuse, and pre-tag `validate.py` always runs fresh.
See [design and measured local evidence](../../docs/archive/2026-10-28/parallel-native-validation.md).

## Before the next kernel tag

Use one unsigned acceptance path before creating a new `kernel/v...` tag:

```sh
ZENITH_VALIDATION_ACCELERATOR=tcg python3 -B deploy/distributor/validate.py --keep /var/tmp/zenith-candidate-001
```

Choose a new evidence directory each run. The command runs controller regressions,
checks native prerequisites, runs Release host tests, builds the Release ISO and
package tool, then calls the distributor's actual native gate plan: login/session,
repository, native programs and kernel crash/recovery. It neither signs nor
publishes nor creates a tag. The default mode requires a clean committed candidate and refuses source changes
during acceptance. Confirm CI on that candidate before tagging it.

For CI or an explicitly prebuilt candidate, add `--existing` to test `output-x64`
without rebuilding the kernel. This still runs controller/host tests, builds the
package tool and runs every native gate. `artifacts.json` records the ISO/ELF
SHA256 values, and any change to those files during acceptance fails the run.
With `--existing`, the caller is responsible for the artifacts' source provenance;
this is not a reusable signing attestation. Avoid simultaneous kernel builds in
the same checkout. The server always rebuilds and revalidates its immutable tag.

Fast checks alone are available with:

```sh
python3 -B deploy/distributor/validate.py --controller-only
```

CI and distributor kernel builds use this same controller test set, including
shared authentication waits, transport/status recovery, accelerator policy and
acceptance orchestration. On the distributor these run from the current controller
checkout, before host tests or compilation even for an older pinned tag. Missing
QEMU/filesystem/TLS tools, firmware, an invalid accelerator policy or unavailable
forced KVM fail before compilation. Auto policy may still fall back to TCG; its
actual selected backend remains recorded by native metrics.

CI now runs the same four release gates through this entry point, retains the
existing extra native CPU-worker coverage, and waits for host/controller jobs
before building the ISO. Kernel tags also trigger CI. Evidence and failures stay
under `--keep`, including retained guest disks/transcripts and phase metrics;
fixture private key directories are excluded from CI artifacts. Do not replace
full acceptance with blind retries, ever-growing timeouts or optional signing
gates. Controller/local success and actual server/installed acceptance remain
separate claims.


### Complete status records and genuine update errors

A wait for `user: K_UPDATE=` is only a record-arrival check; the tagged harness
separately requires the expected numeric exit. The controller now drains the
entire numeric record and terminator before returning, even if UART delivery is
fragmented or the command echo was lost. It prints `Guest exit status: NAME=CODE`
and records `guest_status` in timing metrics. `expected_status: null` means the
calling harness, rather than the adapter, checks the expected value. A nonzero
result is preserved; it is never converted into success or retried as a mutation.

The reported 0.0.13 `K_UPDATE` failure is confirmed **exit 1**, with
`pkg: linked/missing package ancestor` after signature verification. This is a
real guest failure, independent of the prefix-reading race. Increasing its timeout
or retrying the same install does not explain that rejection. Preserve the failed
workspace and inspect its stopped disk first:

```sh
python3 -B tools/inspect-kernel-update-disk.py /path/to/native/k/upgrade/disk.img
```

The helper verifies GPT, EFI and retained kernel hashes, then reads boot-related
root ancestor types with read-only debugfs on a temporary extracted copy. It does
not boot, mount, repair or modify the input, and does not read password/signing-key
files. Missing generations.tsv may be normal for a baseline installation;
installed.tsv and its directory ancestors must exist. Intact disk paths do not
rule out a live VFS lookup failure. Paste the output for diagnosis before starting
another long release run. Exact rejected runtime ancestor remains under investigation.
