# Home-hosted package repository

User direction, 2026-10-11: host the initial repository on the user's home
infrastructure, **LAN-only first**. Host OS/NAS, container support and storage details
are pending. No server has been provisioned and no ports/DNS/firewall rules changed.
See [PACKAGE-MANAGER](PACKAGE-MANAGER.md) for the guest client and trust model.

## Initial shape

Use a static repository on a conventional home server. A package server initially
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
A container, if supported, should mount published data read-only; direct installation
on the host is also viable. Choose deployment files only after host details are known.
Directory browsing is optional and not required by the client protocol.

## LAN deployment steps

1. Identify host, repository storage and backup location. Assign a stable LAN address
   or local DNS name that the guest can resolve.
2. Run the static server on a LAN-restricted listener/firewall policy. Do not publish
   router ports for this first stage.
3. Build and verify immutable artifacts into staging outside the served metadata.
4. Copy artifacts into the published tree, then publish a complete signed metadata
   generation referring to those files. Define index activation semantics on the
   actual host filesystem; avoid exposing half-written indexes.
5. Fetch from another LAN machine and then from Zenith/QEMU. Verify hashes/signatures,
   limits, interrupted downloads and unavailable-server diagnostics.
6. Keep previous release generations and back up repository data/key recovery material
   according to their separate policies. Retention follows supported clients/rollbacks.

QEMU user-mode networking may reach the host through its special host address or a
LAN endpoint depending on server placement. Test the actual route; do not hardcode the
same mirror address for physical machines. The client needs usable TCP/DNS before
online integration is claimed.

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

Before exposing beyond the LAN, decide domain/DNS, public reachability (including
possible CGNAT), TLS, bandwidth, firewall/reverse-proxy arrangement and external
availability. These are later decisions, not reasons to delay the LAN prototype.
Do not expose signing/build management endpoints with the file server.

## Information still needed

Host OS or NAS platform, Docker/Podman availability, existing web/reverse proxy,
repository storage/backup, intended local address/name and package/release size budget.
With those details, create a concrete minimal deployment plus publish/verify checklist.
The current planning task does not assume SSH access or authorization to deploy.
