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

Retry deletes/rebuilds only that automation-owned checkout. If publication already
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

Native acceptance uses an actual short /var/tmp/zn-* workspace so QEMU serial and
monitor sockets stay within Linux AF_UNIX path limits, including when a tagged
older harness resolves its keep path. Phase logs stay in obj/distributor/release-*;
See the [Linux Unix-socket pathname limits](https://man7.org/linux/man-pages/man7/unix.7.html).
native evidence moves into that release directory's native/ subtree on success or
failure. /var/tmp avoids consuming a tmpfs with large disk images. Stale sockets
are removed before moving artifacts across filesystems. A hard-killed service can
leave the short directory; its path is printed in the per-tag log. Inspect it only
after the service has stopped and preserve useful failure evidence before cleanup.

Monitor disk space: failed native runs retain sizeable images. After recording a
failure, remove obsolete automation-owned failed checkouts when no poll is active;
retain releases.json, per-tag logs, signing keys and published archives. The source
cache retains fetched tags even if they disappear upstream. Stop the timer and
service before maintenance. Actual VM scheduling/build/HTTPS acceptance remains
separate from local orchestration tests.
