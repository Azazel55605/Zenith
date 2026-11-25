# Kernel 0.0.80 release qualification

The five source tags share commit `2f758f9306cf4c34ad5e723e5deec9fb5bc15d94`. They cover kernel 0.0.80, Settings 0.17.0, Orbit 0.3.0, net-tools 0.2.0 and man-pages 0.1.10. [Source revision](https://github.com/Azazel55605/Zenith/commit/2f758f9306cf4c34ad5e723e5deec9fb5bc15d94)

On November 25 the distributor validation runner built the Release kernel from clean source. All 1,190 host tests, 596 controller tests, eight post-build adapter tests and 28 fresh QEMU TCG gates passed. The runner rechecked both artifact hashes and the clean source revision at exit. The separate CI adapter suite passed all 73 tests, including the host scheduler and ARM64 paths. [Release runner](../../../deploy/distributor/validate.py) · [CI adapter suite](../../../.github/workflows/ci.yml)

| Local build artifact | SHA-256 |
|---|---|
| Zenith.elf | `21e654e04eb147a6c42a4be381b3da2ffd8ffbcdd819849daa7a14ca90406839` |
| Zenith.iso | `6d8590d29c35a5842101cf48c6780ee44a98c65beea103a490c1bfa7a4ab27ca` |

Each of the four independent package recipes produced its release-version archive, passed signature verification with a temporary test key, and included its matching About changelog. The manifest checks covered the new capabilities that keep incompatible clients off older kernels.

An installed QEMU guest running this kernel and signed net-tools 0.2.0 fetched the 200,116-byte CA bundle from curl.se using the built-in trust roots. The file extracted after shutdown matched SHA-256 `427c064216761a1e72a8b30f0d954d08cb6e4435a82091fb865afd20c8497285` and the ext2 audit passed. The standard VM suite also checked scheduled Orbit notifications, Settings persistence, HDA preferences and idle locking. [Network test procedure](../../testing/NETWORK-VM-TEST.md)

Before the tag push, the signature-verified v3 and v4 stable core indexes at generation 210 offered kernel 0.0.79, Settings 0.16.0, Orbit 0.2.1, net-tools 0.1.0 and man-pages 0.1.9. Distributor publication of the new versions and operator installation are tracked under Q2. [Master roadmap](../../ROADMAP.md#state-and-evidence)
