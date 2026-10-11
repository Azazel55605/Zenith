# Home-hosted package repository

User direction, 2026-10-11: host the initial repository on the user's home
infrastructure using a **dedicated VM with its own IP**, with direct LAN access and
remote access through the user's existing VPN and DNS. Native services remove the
dependency on Docker restrictions and ports already mapped on another host. VM OS,
address/name and storage details are deployment choices still to record. No server has been provisioned and no ports/DNS/firewall rules changed.
See [PACKAGE-MANAGER](PACKAGE-MANAGER.md) for the guest client and trust model.

## Initial shape

Use a static repository with a native web service inside the dedicated home VM. A package server initially
needs to distribute immutable files and metadata; it does not need a package-specific
API, authentication database or a Zenith HTTP-server implementation.

Proposed flow:

`release builder → verifier/signing step → staging repository → read-only HTTP server → Zenith client`

The builder may initially be the developer machine. Signing keys do not belong in
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
