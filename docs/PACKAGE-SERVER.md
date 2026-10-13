# Home-hosted package repository

User direction, 2026-10-11: host the initial repository on the user's home
infrastructure using a **dedicated VM with its own IP**, with direct LAN access and
remote access through the user's existing VPN and DNS. Native services remove the
dependency on Docker restrictions and ports already mapped on another host. VM OS,
storage details are deployment choices still to record. The user has
created Debian 13.7 VM Zenith-Package-Server and is provisioning the builder as
zenith-release at 10.0.20.7. The user has since assigned the proxy name
`https://zenith-stable.net.local`. Manual release build/distribution and a local-relay
installed kernel update are user-confirmed; SSH administration details and direct
guest VPN/HTTPS acceptance remain pending; no remote
ports/DNS/firewall rules have been changed by the agent.
See [PACKAGE-MANAGER](PACKAGE-MANAGER.md) for the guest client and trust model.

Update, 2026-10-12: the user created the VM from a **Debian 13 template** and
has supplied IP 10.0.20.7; SSH details remain pending. The VM should both build and
distribute releases. The [Debian deployment kit](../deploy/distributor/README.md)
now contains a manual kernel build/test/sign/publish workflow, a signature-checking
immutable publisher for core kernels and core/extra data packages, and an nginx
static-service configuration. Reuse the signing key already trusted for the user's
successful installed kernel upgrade. Manual kernel release building/publication is
user-confirmed; direct guest VPN performance and verified proxy HTTPS remain pending.
The kit does not implement P4 signed online indexes.

## Current HTTPS proxy and next priority (2026-10-12)

The package VM remains 10.0.20.7; the named Zoraxy proxy resolves to 10.0.10.3 on
the development host. Host SNI inspection currently returns a self-signed certificate
with CN `Zoraxy Self-host`, no Subject Alternative Name, valid May 2023–May 2038.
`openssl verify -CAfile ... -verify_hostname zenith-stable.net.local ...` rejects it
with hostname mismatch even when that certificate is trusted. This observation is
not an independently authenticated certificate fingerprint or guest acceptance.

Serve a private-CA certificate (or a purpose-made self-signed certificate) containing
`DNS:zenith-stable.net.local`. Install it in Zoraxy using its
[certificate import guidance](https://github.com/tobychui/zoraxy/wiki/Move-Certificates-to-Zoraxy).
Transfer the public trust PEM through the existing trusted bootstrap path; verify
its exact file SHA256 from the locally generated/exported copy, then root runs
`tls trust FILE SHA256` in Zenith. Package signatures still use the separate
`pkg trust` public key. Certificate hostname/time validation remains enabled.
Do not install the inspected default certificate as a workaround for the wrong name.

Automatic build/release pipeline work is deferred. Next package work is the signed
online index contract and manually published client slice: refresh/search, deterministic
core/extra dependency/conflict planning, download/install/upgrade by name using the
existing installed transaction engine. Validate useful scripts/data packages on the
persistent VM before automating releases. Kernel activation keeps its separate
confirmation/rollback boundary; compiled tool unbundling still requires M5.

## Initial shape

Use a static repository with a native web service inside the dedicated home VM. A package server initially
needs to distribute immutable files and metadata; it does not need a package-specific
API, authentication database or a Zenith HTTP-server implementation.

Proposed flow:

`release builder → verifier/signing step → staging repository → read-only HTTP server → Zenith client`

The agreed builder is now the same dedicated Debian VM as the distributor,
with native web workers unable to write release files or access the signing key.
Its trusted release account can build/sign/publish; this is not isolated execution
of untrusted build inputs. Signing keys do not belong in
the served directory; hosting and signing have different access needs. Trusted public
keys arrive with Zenith's installation/recovery medium. LAN operation does not make
unsigned artifacts authentic.

Suggested directory shape (proposal, not a frozen protocol):

```
repository/
  metadata/                 signed versioned indexes/trust metadata
  artifacts/<digest>/       immutable package files and kernel/release artifacts
  releases/<release-id>/    matching system-generation manifests
```

Use a maintained static web server such as Caddy or nginx according to the installed
host. Caddy documents [static file serving](https://caddyserver.com/docs/caddyfile/directives/file_server).
Install the chosen server directly in the VM and serve a read-only published tree.
No container is required. Select a maintained minimal server OS with normal service,
update and backup tooling; exact distribution/version and service configuration are
chosen when the VM is provisioned. Choose deployment files after recording VM details.
Directory browsing is optional and not required by the client protocol.

## VM, LAN and VPN deployment steps

1. Provision a VM with its own reachable IP (appropriate bridged/routed network),
   repository disk and backup location. Record OS/version and resource/retention budget.
2. Add its stable address/name to the user's DNS and allow the intended LAN/VPN routes
   and web port. Run the native static service; verify DNS/routing/firewall access from
   both LAN and a VPN-connected external client. No direct public router port is needed
   for this agreed access model.
3. Build and verify immutable artifacts into staging outside the served metadata.
4. Copy artifacts into the published tree, then publish a complete signed metadata
   generation referring to those files. Define index activation semantics on the
   actual host filesystem; avoid exposing half-written indexes.
5. Fetch from another LAN machine and then from Zenith/QEMU. Verify hashes/signatures,
   limits, interrupted downloads and unavailable-server diagnostics.
6. Keep previous release generations and back up repository data/key recovery material
   according to their separate policies. Retention follows supported clients/rollbacks.

QEMU and physical Zenith clients must resolve and reach the VM endpoint through their
actual network routes. An external VM client may use host/VPN routing; do not assume
QEMU automatically inherits VPN routes or that Zenith already has a VPN client. Native
Zenith VPN functionality is not implied by the server being VPN-accessible. Test the
intended client path and report an unavailable route clearly. TCP/DNS integration
remains required before online package operation is claimed.

Lab HTTP may be useful before guest TLS works, but authenticated package verification
is still a release gate. If the first transport experiment lacks verification it is
strictly a disposable download test, not an installation path. HTTPS and certificate/
clock validation must be tested on the guest before changing policy for public use.

## Operating behavior

Mirror downtime should report failure and leave installed software usable; preserve
verified cached files and never apply a partial download. Start with one repository,
manual refresh/apply and visible release/channel identity. Static hosting can support
additional replicas later because signatures and content hashes travel with artifacts.
No login/password needs to be embedded in a publicly distributable client for a
read-only public repository. Private repository access, if wanted later, needs a
separate credential/transport design.

Remote private access uses the existing VPN and DNS; it does not require a publicly
exposed repository. Record the VPN subnet/routes, DNS resolver/name and certificate
policy at deployment. Guest HTTPS/trust behavior must be verified; VPN transport does
not replace package signatures. Direct public access, if requested later, would need
its own reachability/TLS/bandwidth/availability plan.
Do not expose signing/build management endpoints with the file server.

## Information still needed

Known: Debian 13.7 at 10.0.20.7, release account zenith-release, nginx deployment
kit, manual release success and Zoraxy proxy https://zenith-stable.net.local.
Still needed: SSH administrator/key access, actual service/source/tool inventory,
VM resources/network attachment, permitted LAN/VPN subnets, repository storage,
backup destination/retention and restore evidence. Docker is not a dependency.


Current home information (updated 2026-10-12): DNS is 10.0.10.3. The dedicated Debian package
server VM exists as Zenith-Package-Server, with builder setup in progress;
its IP is 10.0.20.7 and its proxy name is zenith-stable.net.local. Earlier laptop
ping and TCP reachability succeeded, while guest ping failed. On 2026-10-13 a fresh
laptop HTTP check could not connect to 10.0.20.7:80; HTTPS reached the proxy but
failed certificate hostname validation (Zoraxy Self-host). Current guest transport
acceptance needs repeating after service and certificate setup are checked.
M3's host user/TAP network profiles and DNS configuration permit acceptance once
it exists; no server has been deployed as part of networking implementation.
See [NETWORKING](NETWORKING.md) for the current LAN/CA bootstrap test procedure.

## Offline delivery foundation now implemented

[P1 packages](PACKAGES.md) define signed .zpk files, core/extra section identities
and a reusable offline transaction engine. The manual kit publishes immutable
name/version directories with signed payloads and archive digest receipts;
content-addressed index paths remain a P4 design. Host tooling signs with
RSA-PSS/SHA256 outside the served tree. The current
client pins an exact public PEM fingerprint explicitly and accepts signed files
from local storage or manual fetch. TLS CA trust is configured separately.

A signed core/extra index protocol, generation/expiry checks, persisted rollback
floors, key rotation and automatic dependency download remain P4 implementation.
Setting the HTTPS base URLs in repos.conf does not implement that client. The VM
can initially serve immutable .zpk files and the public PEM while that work proceeds;
convey its key fingerprint through a trusted channel. Never serve the private key.

## Completion checklist (2026-10-13)

1. Inventory the existing VM with `bash deploy/distributor/check-setup.sh` as
   zenith-release; compare source revision and public-key fingerprint with the
   development/release records. Check nginx configuration as administrator.
2. Restore/confirm direct HTTP delivery of an existing immutable release on
   10.0.20.7:80 from the laptop. Inspect service/listeners and actual network rules
   before changing them; a failed connection alone does not identify the cause.
3. Point Zoraxy at the working VM HTTP service and select a certificate containing
   DNS:zenith-stable.net.local. Bootstrap the public CA/certificate into Zenith
   with its known PEM SHA256; verify HTTPS without disabling certificate checks.
4. Test complete package bytes/signatures from LAN and VPN, then from installed
   Zenith. Confirm usable progress and an installed kernel reboot/confirmation.
5. Record resource/storage budget, release retention and separate signing-key
   backups; perform a restore test without replacing the installed trust key.
6. Implement P4 signed core/extra indexes and named package install/upgrade support
   in Zenith and the manual publisher. This is project implementation work, not
   missing Debian software. Automatic build scheduling follows that acceptance.
