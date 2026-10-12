# Home-hosted package repository

User direction, 2026-10-11: host the initial repository on the user's home
infrastructure using a **dedicated VM with its own IP**, with direct LAN access and
remote access through the user's existing VPN and DNS. Native services remove the
dependency on Docker restrictions and ports already mapped on another host. VM OS,
address/name and storage details are deployment choices still to record. No server has been provisioned and no ports/DNS/firewall rules changed.
See [PACKAGE-MANAGER](PACKAGE-MANAGER.md) for the guest client and trust model.

Update, 2026-10-12: the user will create the VM from a **Debian template** and
provide version/IP/hostname/SSH details afterward. The VM should both build and
distribute releases. The [Debian deployment kit](../deploy/distributor/README.md)
now contains a manual kernel build/test/sign/publish workflow, a signature-checking
immutable publisher for core kernels and core/extra data packages, and an nginx
static-service configuration. Reuse the signing key already trusted for the user's
successful installed kernel upgrade. Provisioning and actual VM/LAN/VPN/HTTPS
acceptance remain pending; the kit does not implement P4 signed online indexes.

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

Hypervisor/network attachment, VM OS choice, IP/DNS name, permitted LAN/VPN subnets,
repository storage/backup, package/release size budget, chosen web service and publisher
access. Docker/Podman availability and existing host port mappings are not dependencies.
With those details, create a concrete minimal deployment plus publish/verify checklist.
The current planning task does not assume SSH access or authorization to deploy.


Current home information (updated 2026-10-12): DNS is 10.0.10.3. The dedicated Debian package
server VM has not been provisioned and no repository address/name is assigned.
M3's host user/TAP network profiles and DNS configuration permit acceptance once
it exists; no server has been deployed as part of networking implementation.
See [NETWORKING](NETWORKING.md) for the current LAN/CA bootstrap test procedure.

## Offline delivery foundation now implemented

[P1 packages](PACKAGES.md) define signed .zpk files, core/extra section identities
and a reusable offline transaction engine. Ship artifacts under content-addressed
paths; host tooling signs with RSA-PSS/SHA256 outside the served tree. The current
client pins an exact public PEM fingerprint explicitly and accepts signed files
from local storage or manual fetch. TLS CA trust is configured separately.

A signed core/extra index protocol, generation/expiry checks, persisted rollback
floors, key rotation and automatic dependency download remain P4 implementation.
Setting the HTTPS base URLs in repos.conf does not implement that client. The VM
can initially serve immutable .zpk files and the public PEM while that work proceeds;
convey its key fingerprint through a trusted channel. Never serve the private key.
