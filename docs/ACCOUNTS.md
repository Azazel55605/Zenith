# Accounts and filesystem access

M2 adds authenticated identities and discretionary filesystem access to the
installed ext2 system. This is an access policy for Zenith's trusted, kernel-linked
apps. It is not process isolation: kernel code can construct root credentials or
call raw drivers. Protected userland remains M5 work.

## First installation and login

New and existing ext2 installations with no shadow database start a physical
console root setup session. Both default accounts (`root`, UID/GID 0; `user`,
UID/GID 1000) are locked and have no default password. Run:

```sh
passwd root
passwd user
login user
```

Each `passwd` asks twice using hidden terminal input. Passwords are never command
arguments, echoed output or history. Ctrl+C/end-of-input cancels without changing
the password. Up/down history, completion and clipboard input are disabled during
password entry; buffered typeahead is discarded. Keep the root password: after
setting it, later boots start as `nobody` (65534) and require `login NAME`.

`login` replaces the desktop session, closes its windows, cancels terminal jobs
cooperatively and clears the clipboard. `su [NAME]` changes only the current
terminal; root can switch without a password, other users must authenticate.
`logout` ends the desktop session. `whoami` and `id` report credentials, even if
someone changes `USER` or `HOME`. Launcher power controls are available only to
root. Applications opened from a terminal inherit that operation's identity;
launcher applications inherit the desktop session. System-log mirroring uses a
narrow root scope for its fixed boot-time destination, preserving log persistence
without granting caller authority.

Root creates accounts with `useradd NAME`, which allocates an unused UID/GID from
1000..65533, a private `/home/NAME`, and a locked shadow entry. `passwd NAME` unlocks
it. Ordinary users can run `passwd` to change their own password after proving the
current password. Removing users, group administration and password aging are
not implemented. UID/GID 65534 and the name nobody are reserved for
unauthenticated sessions.

Live FAT roots and legacy FAT installations retain a root-only maintenance session.
They cannot safely store per-user permissions or shadow records; account commands
require ext2. Upgrade to an ext2 installation to use authentication. Corrupt account
databases fail closed to `nobody` and an error; they do not reopen root setup. Repair
requires a trusted live boot and offline disk access. Locked initial root setup is
for trusted physical access, not an unattended remotely exposed installation.

## Access rules

The mounted-driver adapter covers shell/System.IO, Files, Editor and inode/handle
operations. Credentials are immutable and explicitly scoped per worker/window;
new threads default to `nobody`, not root. Checks choose owner, primary or
supplementary group, then other bits. Root bypasses discretionary checks.

- Reading/listing requires read; directory traversal requires search/execute.
- Creating, removing and renaming require parent write and search. Sticky
  directories additionally require root, directory owner or entry owner.
- Creation uses caller UID/GID and fixed umask 022. Setgid directories inherit
  their GID and pass setgid to child directories.
- Only owners/root may chmod; only root may chown. Non-root cannot set setuid or
  setgid bits. These bits never grant execution privilege: there is no userland
  executable loader. ACLs, capabilities and configurable umask are deferred.
- Opening through System.IO checks the requested read/write access before
  allocation or truncation; denied operations release their handles. Permissions
  are rechecked on each file read/write/truncate, including held handles after chmod. This is stricter than Unix's open-time descriptor grants.
- Symlink resolution and mount traversal check all ancestors. Unlink checks the
  parent before marking an open file delete-pending; already authorized deletion
  completes at final close even after the closing identity changes.

`/tmp` is a fresh 16 MiB ext2 RAM volume, mode 1777, on every boot. Home directories
are 0700. Authentication initialization enforces root-owned 0755 ancestors
(`/`, `/etc`, `/home`, `/var`, `/var/log`) to prevent account/log replacement.
`/etc/passwd` and `/etc/group` are root-owned 0644; `/etc/shadow` is
root-owned 0600. `/dev/null` and `/dev/zero` remain 0666. Raw disk/partition nodes
are root-readable 0400 and never writable. Installing, formatting, checking raw
partitions, mounting/unmounting, saved system settings and power/panic commands
require root. FAT mounts expose no persisted Unix ownership and permit mutation
only to root; `ls -l` retains `?` for their unknown metadata.

## Passwords, entropy and storage

The versioned shadow record uses PBKDF2-HMAC-SHA256, 600,000 iterations, a fresh
16-byte salt and 32-byte hash. Verification validates bounded canonical records
and compares derived bytes without early exit. SHA-256/HMAC/PBKDF2 vectors are
compared with independent .NET crypto on the host and a fixed guest vector.
This implementation has not received an independent cryptographic audit.
The iteration choice follows [OWASP's PBKDF2 guidance](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html).

The x64 entropy source uses CPUID-gated RDSEED with bounded retries, trusting the
CPU/hypervisor implementation. No timer, XorShift or OS-crypto fallback is used.
Unavailable entropy refuses password creation and `/dev/random` reads. ARM64
currently has a fail-closed stub. `/dev/random` is read-only, returning at most
4096 bytes per read; failed reads erase the buffer. This does not replace Cosmos's
other random APIs or claim suitability for unimplemented TLS/package signatures.
Password strings are managed memory and cannot be reliably erased; intermediate
byte buffers are cleared. Do not log or dump passwords.

Account changes stage root-only files, flush, replace and sync. A user is published
in passwd after its group, locked shadow entry and private home exist. Validation
or entropy failure preserves existing passwords. Failed additions can leave orphan
locked records/homes for root to reconcile. Replacement relies on the existing VFS
rename behavior, not a journal or crash-atomic transaction. Crash-dirty ext2 still
requires host e2fsck; authentication provides no power-loss recovery guarantee.

## Validation

`securityprobe` checks entropy, the guest password vector and credential scope
restoration. Root's `accessprobe EXT2_DIRECTORY` checks held-handle I/O denial,
denied pending unlink, authorized final-close cleanup, creation ownership and
inaccessible parents across a nested proc mount. Probes use temporary objects.

Run `python3 tools/security-smoke.py [OUTPUT_DIRECTORY]` for disposable QEMU
installation, password/login/su failures, owner metadata, shadow/root/raw-disk and
sticky-delete denials, ordinary-user Files/Editor operations and reboot persistence.
The output directory contains disk/log evidence and must not be committed. The
harness rejects password text in serial logs. The existing filesystem, large-profile
and disk-only UEFI suites remain separate regressions.
