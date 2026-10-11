# Pinned Mbed TLS port

- Upstream: https://github.com/Mbed-TLS/mbedtls, tag `mbedtls-3.6.7`.
- Source archive: `source-3.6.7.tar.gz`, fetched from
  https://codeload.github.com/Mbed-TLS/mbedtls/tar.gz/refs/tags/mbedtls-3.6.7.
- SHA256: `807f0a1cc7c63b571180e9efeb6236b677e550869eb0442ace33aa9c69055fcc`.
- License: upstream dual Apache-2.0/GPL-2.0-or-later; Zenith selects Apache-2.0.
  Complete upstream terms retained in [LICENSE](LICENSE); attribution/Apache text
  also ships in `man licenses` through MbedTlsLicense.

No upstream file is modified. `tools/build-tls.py` verifies the archive before
extracting into ignored build output. Clang compiles portable C with Zenith's
configuration and minimal private byte/string/format/zeroize platform functions.
Native compilation uses `-nostdinc`, compiler builtin headers and Zenith's shim,
so host socket headers cannot introduce libc dependencies. Both host and native
profiles explicitly select Mbed TLS's software IP parser (`MBEDTLS_TEST_SW_INET_PTON`)
for certificate IP SAN matching. Every native build checks object imports with
`nm`; only kernel-provided memcpy, memset and zenith_rdseed_word may remain external.
The source archive permits offline builds, contains the original license notices,
and keeps dependency changes independently reviewable from our bridge/configuration.

`src/Native/Tls/config.h` enables only client TLS 1.2 ECDHE-RSA/ECDHE-ECDSA AES-GCM,
P-256, SHA-256/384/512, RSA/PKCS#1, PEM/X.509 and CTR-DRBG. No OS, sockets, filesystem,
PSA, time libc, resumption or server code is enabled. Library date flags are added
by a verification callback comparing each certificate with the explicit system UTC
snapshot; all existing validation flags remain intact. Verification is required.

The bridge owns one 1 MiB static allocator arena and two 32 KiB queues. Managed
NetworkService serializes its use. C never invokes managed callbacks. The only
freestanding entropy import is `zenith_rdseed_word`; failure after 256 attempts per
word rejects seeding/reseeding. Secret state/queues/arena are zeroed on retirement.
Managed socket polling supplies ciphertext; buffers/pointers are fixed for each call.
There are no managed object-layout assumptions. Arena exhaustion propagates a library
error; allocator corruption traps rather than continuing with compromised state.
Formatting convenience APIs are unused and return failure/empty output in this port.

`--host` compiles the same port/profile as a shared object, replacing only RDSEED
with Linux getrandom and adding a test-only entropy failure switch. AES/GCM/SHA/
CTR-DRBG upstream self-tests are enabled for those compilation units and the bridge;
other modules' self-test convenience RNGs are not compiled. Native guest tests run
these known-answer tests and real handshakes through the freestanding ABI.
The host suite uses OpenSSL MemoryBIO for good/bad chain/name/date/record cases.
These checks establish integration, not independent cryptographic certification.

Keep dependency updates explicit: verify release/security notes, replace archive and
checksum together, review configuration/ABI, rerun host and native faults/cancellation
and missing-entropy rejection. Do not disable certificate verification to make an
incompatible mirror work. TLS 1.3/public roots/revocation/multiple concurrent TLS
sessions require new implementation and validation.
