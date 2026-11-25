# Public HTTPS trust anchors

The kernel build uses the November 5, 2026 Mozilla server-authentication CA
bundle distributed by the curl project. It contains 118 public certificates.
The build checks the exact PEM bytes against SHA-256
`427c064216761a1e72a8b30f0d954d08cb6e4435a82091fb865afd20c8497285`
and embeds immutable DER data in the freestanding TLS bridge. Builds work offline.
[Upstream PEM](https://curl.se/ca/cacert-2026-11-05.pem) · [Published digest](https://curl.se/ca/cacert-2026-11-05.pem.sha256)

This is curl's PEM extraction of Mozilla's certificate data under the Mozilla
Public License 2.0. NSS-specific policy constraints outside the certificates are
absent from the extraction. Zenith uses Mbed TLS chain, hostname, key and date
validation. [Upstream extraction notes](https://curl.se/docs/caextract.html) · [License](LICENSE)

To update, review the upstream additions and removals, choose a dated snapshot,
compare its published digest, then update `cacert.pem`, `WEB_CA_SHA256` in
`tools/build-tls.py`, the dated URLs in `man licenses` and
`tools/native-network-smoke.py`, and this record together. Run the bridge suite, including
unknown CA rejection, and verify a public TLS 1.2 download in the guest. The
kernel package distributes the new anchors. Local package mirror trust stays
under the administrator's control in `/etc/zenith/tls-ca.pem`.
