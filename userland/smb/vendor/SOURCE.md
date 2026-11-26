# SMB authentication primitives

The userland client uses Mbed TLS 3.6.7 for MD5, SHA-256 and AES. Its build verifies and extracts the [same offline source archive](../../../vendor/mbedtls/SOURCE.md) as the kernel TLS bridge. AES-CMAC signs SMB 3.0/3.0.2 messages; HMAC-SHA-256 signs SMB 2.1 and derives SMB 3 signing keys.

NTLMv2 also needs MD4 solely for deriving the account secret from the UTF-16 password. The following unmodified files come from Mbed TLS 2.28.10, revision `2fc8413bfcb51354c8e679141b17b3f1a5942561`, and are distributed under the [Apache 2.0 license](../../../vendor/mbedtls/LICENSE).

| File | SHA-256 |
|---|---|
| [md4.c](https://github.com/Mbed-TLS/mbedtls/blob/2fc8413bfcb51354c8e679141b17b3f1a5942561/library/md4.c) | `8b9b9b6500336bb018df5277d263643d3bbc69cb178760286f6e406c799418f4` |
| [mbedtls/md4.h](https://github.com/Mbed-TLS/mbedtls/blob/2fc8413bfcb51354c8e679141b17b3f1a5942561/include/mbedtls/md4.h) | `bd114fd3569ddef8a1315fc875d7f9ec0a892f64599e6c9809f6772e1544fc06` |

The protocol structures implement [MS-SMB2](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-smb2/), [MS-NLMP](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-nlmp/) and the NDR32 NetrShareEnum call in [MS-SRVS](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-srvs/).
