# Signed online packages (0.0.7, extended in 0.0.8)

This is a bounded private LAN/VPN repository client, using the existing pinned
RSA-PSS package key and verified HTTPS. It supplies discovery, planning, download
and existing installed-root transactions. It is not TUF; key rotation/revocation,
multiple trust roles, public-mirror hardening and matched root snapshots remain
separate P4/recovery work. The simple DE and existing compiled recovery tools remain
kernel-linked; Sunbeam begins independent native packaging ([native ABI](NATIVE-PROGRAMS.md)).

## Guest workflow

After installing/confirming 0.0.7 on ext2, configure your mirror once:

```text
pkg mirror https://zenith-stable.net.local
pkg refresh
pkg search
pkg plan zenith-docs
pkg install zenith-docs
```

Review the displayed package/dependency list and answer `y` to apply. `--yes` is
required for noninteractive application; `pkg plan` neither downloads archives
nor changes installed files. Existing `pkg install /path/package.zpk` and multi-file
offline transactions remain available. Names have no dots/slashes; use an explicit
path or `.zpk` filename for local archives.

```text
pkg refresh
pkg upgrade
pkg kernel update
reboot
boot confirm
```

`upgrade` covers installed data packages. Kernel update separately selects a newer
core kernel, checks pending/recovery state before download, verifies signed index
size/hash and the actual kernel container, then calls the existing P3 coordinator.
It does not reboot or confirm automatically. Existing TLS and signing trust persist;
they do not need importing on every update. A missing key requires `pkg trust`,
while private HTTPS trust can be provisioned with `tls bootstrap URL SHA256`.

The 0.0.7 bootstrap itself still uses the older guest's explicit fetch/check/install
commands. Later kernels can use the shortcut after their signed index is refreshed.
All mutation commands require root and an installed ext2 system. Ordinary users
can search and plan from readable authenticated caches.

## Update everything (0.0.8)

```text
pkg update
# Or, after reviewing the update policy:
pkg update --yes
reboot
boot confirm
```

`update` refreshes both signed catalogs itself, resolves upgrades to every installed
signed data package (including native program packages), and selects a newer core
kernel above the boot version floor. It does not install unrelated packages merely
because they exist in the catalog. The same dependencies/conflicts/platform/ownership
rules apply, with up to 32 selected packages. One confirmation shows the complete
plan. No available update returns success without changing installed packages or
boot generations; the authenticated metadata cache can still refresh.

All candidate archives are downloaded and verified first. Kernel boot identity,
retained artifacts, available ESP space and recovery/pending state are preflighted
before changing data. Then data commits in its existing transaction and kernel
staging uses its separate boot transaction. These are **not one atomic root/ESP
transaction**. A staging failure can leave successfully updated data installed;
inspect `boot status`, resolve recovery and rerun as appropriate. Catalog freshness
is checked before data commit and again before kernel staging. Cancellation and
known preflight failures preserve the old installed data. Interrupted data writes
use `pkg recover`; interrupted boot writes use `boot recover`.

A pending or recovery-required boot generation blocks another system update before
refresh/download. Confirm or roll back it first. No automatic reboot or boot
confirmation occurs. Data-only updates need no reboot; fonts apply on next desktop
initialization and native program replacement applies on next invocation.

For a guest still on 0.0.7, use `pkg refresh` and `pkg kernel update` once to get
0.0.8, then reboot/confirm. The new command is available in that loaded kernel.

Independent source recipes now provide `zenith-man-pages` and `zenith-fonts` in
core, `sunbeam-tools` and `zenith-docs` in extra. Install wanted packages by name;
then `pkg update` maintains them. Embedded fonts and a small recovery manuals remain
available when the independent data packages are absent.

## Operator workflow

Update the server checkout to this implementation. Publish the already-issued
0.0.7 archive with its recorded source commit, then run:

```sh
bash deploy/distributor/release.sh data zenith-docs 0.0.1 packaging/zenith-docs --repository extra
bash deploy/distributor/release.sh verify-data zenith-docs 0.0.1 --repository extra
bash deploy/distributor/release.sh index
```

New `kernel` and `data` releases sign/activate both indexes after immutable archive
publication. `publish` of an existing kernel does not access the private key: run
`index` explicitly afterward. A metadata failure leaves the immutable archive
published; rerun `index` instead of trying to replace/rebuild the version.

Indexes default to seven-day validity. Run `index` to renew them even without new
packages; the distributor timer renews them as well as discovering independent package tags. Keep the highest generations across
server restores: clients reject replayed or changed existing generations. The
lower-level `index-seal` tool accepts explicit generations for operator recovery.
Production 0.0.5/0.0.6 archives retain their original bytes and source identities.

## Protocol and limits

Each mirror base serves `core/index.zri`, `extra/index.zri` and immutable
`REPOSITORY/NAME/VERSION/NAME.zpk` archives. Signed headers are domain-separated from
data/kernel containers with `ZENITH-SIGNED-INDEX`. Metadata binds repository,
nonzero monotonically increasing generation, issue/expiry timestamps and exact
archive size/SHA256. Data entries include the authenticated manifest, allowing
dependency/conflict/ownership planning before payload downloads.

The index signer validates every source archive with the pinned signing identity
and checks its immutable path. Indexes are at most 256 KiB/64 entries each and
valid for no more than 30 days. The guest checks signature, repository, clock,
expiry and cached generation; the cached signed bytes hold the persisted floor.
The same generation is accepted only with identical signed bytes. Refresh replaces
each repository independently after validation; failure preserves its previous
cache. An expired older cache remains usable solely as the generation floor during
refresh, not for installation. First contact has no prior floor; clock integrity
and first-contact trust remain limited by the current system's unauthenticated RTC/NTP.

Caches live below `/var/lib/zenith/pkg/repositories`. Verified archives use SHA256
filenames and are checked again before use. Bad downloaded bytes are rejected and
removed for retry. Partial or missing transfers never reach the installed transaction.
Cache retention/cleanup UI is future work. Old valid caches survive server downtime
until expiry; installed software remains usable afterward.

The resolver prefers configured repository priority, then the highest compatible
numeric version, retaining satisfied installed dependencies. It uses bounded
backtracking (4096 choices), preserves reverse dependencies, refuses downgrades,
bundled identities and ownership/conflict violations, and accepts hook-free data
dependency cycles as an atomic set. At most 16 packages are applied per transaction;
32 installed packages and existing P1 byte/file/space limits still apply. Difficult
or oversized plans fail without applying a partial subset. Locally edited owned
files remain protected by the existing installer. Equal-version changed bytes do
not silently replace an installed package.

Metadata signatures do not replace archive signatures, and TLS trust does not
replace package trust. Confirmation follows a visible plan; metadata freshness is
checked again after confirmation and downloads before application. Offline recovery,
external ISO fallback and existing P1/P3 journals remain available.
