# musl for the NetSurf package

The pristine [musl 1.2.6 release](https://musl.libc.org/releases/musl-1.2.6.tar.gz)
has SHA-256 `d585fd3b613c66151fc3249e8ed44f77020cb5e6c1e635a616d3f9f82460512a`.
The license notices are in COPYRIGHT. The browser build compiles the C library
for a single Zenith application thread. Its startup, allocator backing store,
clock and file calls use the same protected ABI as other native programs.
