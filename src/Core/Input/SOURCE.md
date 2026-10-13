# German scan map source

GermanKeyboardLayout.cs is adapted from CosmosOS/Cosmos revision
81259dd941a39b60226dc6f9db14befd6558e592,
src/Cosmos.Kernel.System/Keyboard/ScanMaps/DEStandardLayout.cs.
The BSD 3-Clause notice is retained in LICENSE in this directory.

Changes: local namespace/name; ISO # and < > | scan positions; logical Y/Z
identities; explicit released right-Alt scan code. KeyboardScanPolicy is Zenith
code used by signature-checked adaptations of build copies of Cosmos 3.0.89.
Num Lock is applied only to actual keypad scans; dedicated PS/2 and virtio
navigation scans retain identity before layout conversion. Installed NuGet
packages and the sibling upstream checkout are not modified.
