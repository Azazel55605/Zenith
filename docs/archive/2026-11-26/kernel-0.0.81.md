# Kernel 0.0.81 and SMB CLI release qualification

The release source is `2f19b177d2213835588d4872d7a138fcf97393dd`. It includes the kernel TCP receive repair, transfer ABI and download space checks, the first signed SMB CLI, and the Horizon Network tab design. [Source revision](https://github.com/Azazel55605/Zenith/commit/2f19b177d2213835588d4872d7a138fcf97393dd)

| Source tag | Main change |
| --- | --- |
| `kernel/v0.0.81` | Preserve new bytes in overlapping TCP segments, buffer bounded out-of-order data and expose the native transfer ABI. |
| `zenith-net-tools/v0.2.1` | Check free space before staging a known-length download. |
| `zenith-smb/v0.1.0` | List shares and directories and transfer individual files over signed SMB connections. |
| `zenith-man-pages/v0.1.11` | Ship the SMB transfer manual, including credential-file and replacement options. |

The clean-source release runner passed 1,207 managed tests, 598 controller tests, eight post-build adapter tests and all 28 fresh QEMU TCG gates. The runner rechecked the commit and artifact digests at exit. All 77 cases in the separate CI adapter test suite also passed locally. [Release runner](../../../deploy/distributor/validate.py) · [CI adapter suite](../../../.github/workflows/ci.yml)

| Clean Release build | SHA-256 |
| --- | --- |
| Zenith.elf | `1f8a7087e89b0f73fcc2cd695da3bd72f83aabb5835f7a5fa0175400e3b2a55b` |
| Zenith.iso | `cbf97a42ddd71027c8f2f2d4a8386d4aedb784d011c677a7ef3ebc37cccb3644` |

All three data archives passed signature, manifest, dependency and payload-digest checks with a temporary signing key. The two native binaries reported their release versions. On a fresh installation of the kernel above, the signed SMB CLI transferred 32 MiB in each direction against Samba requiring signing. An oversized download was refused, the extracted files matched and the guest ext2 filesystem passed its offline audit. [Samba and guest checks](../../../tools/smb-smoke.py)

The paired VMware test on the WireGuard route used a development build of the TCP repair. Median HTTP and verified HTTPS throughput reached 9.56 and 14.18 Mbit/s. The host capture and independent disk checks are described under N1. Home-LAN throughput remains open. [Measurement details](../../components/NETWORKING.md#vmware-tcp-window-probe-and-overlap-repair-2026-11-26)

Before tagging, the signature-verified stable v4 indexes were at generations 215 (core) and 211 (extra). Core offered kernel 0.0.80, net-tools 0.2.0 and man-pages 0.1.10. The new SMB CLI had no entry in extra. Q2 tracks distributor publication and the operator's installed versions. [Roadmap](../../ROADMAP.md#state-and-evidence)
