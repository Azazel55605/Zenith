# Package server operations overview

Reviewed **2026-10-15** from repository runbooks and operator reports. This review
did not connect to or change the VM. For exact setup/update commands use the
[deployment runbook](../deploy/distributor/README.md),
[automation runbook](../deploy/distributor/AUTOMATION.md) and
[dashboard runbook](../deploy/distributor/DASHBOARD.md).

## Deployment record

| Item | Recorded deployment |
|---|---|
| Host | Dedicated Debian 13 VM on TrueNAS; native services |
| Address | `10.0.20.7`; LAN and existing VPN |
| Public-facing internal endpoint | `https://zenith-stable.net.local` through Zoraxy |
| DNS | Home resolver `10.0.10.3` |
| Builder account / checkout | `zenith-release`, `~/Zenith` |
| Publication storage | `/srv/zenith/packages`, served by nginx |
| Toolchain reported | .NET SDK 10.0.401, Cosmos tools/patcher 3.0.89; native GCC/Clang path |
| Backups | Automated TrueNAS backups reported; restore test not confirmed |

The operator has confirmed verified guest HTTPS and named package installation.
The server is no longer a future VM to provision. Credentials, keys, firewall
rules and certificate changes remain operator-managed; do not infer current live
health from these recorded values.

## How a release moves

1. Updated controller polls trusted package-specific tags reachable from main.
2. It pins the source and builds in a per-release checkout with separate logs.
3. Required host/build/native gates run. Failed validation prevents signing/publication.
4. It builds/signs immutable archives and receipts and publishes signed indexes.
5. Dashboard shows activity and versions; installed Zenith refreshes and verifies.
6. Client installation and reboot/confirmation, where needed, establish acceptance
   beyond a successful server build.

The current controller and tagged product source are distinct. Pulling controller
fixes does not move an existing immutable tag. Package version namespaces are
independent. New kernel validation includes login, kernel-update, repository and
native gates where present in the tag.

## Retry, logs and dashboard

A recorded failed/interrupted release is not repeatedly rebuilt every timer tick.
Use the documented explicit retry to test a correction. Prefer the systemd-backed
trigger so closing an SSH session does not own the worker lifetime. Inspect phase
logs and actual service/QEMU state before calling a long build stuck.

The lightweight management UI lives at `/manage/` behind nginx/proxy access policy.
It shows timer last/next run, manual trigger, live phase output, expandable package
versions and storage. Successful old activity can be hidden on new discovery;
failed/interrupted activity disappears only after a strictly newer version of the
same package completes. This is display filtering, not deletion of release pins,
archives or direct log access. Authentication/proxy setup belongs to the runbook.

## Storage policy

The controller runs bounded maintenance under its locks. It removes successful
checkout leftovers and expires old failed/interrupted checkouts while retaining
the newest failure and bounded attempt history. Registered same-owner inactive
scratch orphans can expire; unregistered `/var/tmp` directories need operator review.
Active/pending work, signing keys, published packages, indexes and backups are
protected. Read-only reporting and `storage --clean` use the same policy.

Dashboard allocated-byte reporting is bounded and can be partial; sparse/reflink
accounting is not a deduplicated physical-usage guarantee. Exact retention defaults,
paths and safety checks live in [maintenance code](../deploy/distributor/maintenance.py)
and the [automation runbook](../deploy/distributor/AUTOMATION.md).

## Validation performance

The home VM's storage comparison measured KVM at 95.765 s versus TCG at 23.468 s
for the same benchmark. An explicit TCG policy was selected there. KVM group
membership alone does not prove the active backend or good nested I/O performance.
Additional guest vCPUs do not distribute managed Zenith execution; see
[multicore](MULTICORE.md). Preserve mandatory release gates rather than skipping
durability/authentication checks to shorten builds.

## Remaining operator acceptance

- Kernel 0.0.13 / Sunbeam 0.2.0 build, publication and installed acceptance.
- An actual backup restore test, retaining the existing signing identity.
- Ongoing certificate renewal/expiry and signing-key backup/rotation procedures.

Historical setup commands and resolved connectivity/certificate incidents are in
[the server archive](archive/2026-10-15/PACKAGE-SERVER.md), not a second setup checklist.
