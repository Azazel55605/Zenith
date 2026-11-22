# PureDOOM in Zenith

The engine source is from [PureDOOM](https://github.com/Daivuk/PureDOOM) commit
`355cfbd16fac119718879239336ee2ea408886bd`. The upstream header, before the
Zenith change below, has SHA-256
`ec16ce721436bf3fa4c338320afedd56a93c0b563a94ebf283718503c71ca780`.

Zenith skips the first `W_AddFile` directory copy because no prior directory
exists on the first load. This avoids a null read on the protected native ABI.
Title drawing uses `DMENUPIC` when the base game supplies it in place of `TITLEPIC`.
The upstream GPLv2 license is in `upstream/LICENSE`. The engine package carries
its source, including this adapter and the files needed to rebuild it. WAD
files are supplied separately by the user.

To rebuild the preview on an x86-64 host, copy the installed
`/usr/share/zenith/doom/source` tree and run the following from its root with
Python 3 and GCC or Clang/LLD available. Use the package version for `VERSION`.

```sh
python3 -B tools/build-userland.py /tmp/zenith-doom-payload --program doom --version VERSION
```
