# NetSurf browser core for Zenith

The native browser compiles NetSurf 3.11's HTML layout, CSS cascade, DOM,
redraw, navigation, content cache and URL handling. The selected image handlers
support PNG, GIF, BMP and ICO. NetSurf's progressive PNG handler uses the pinned
libpng decoder with the same zlib as the browser core. This build uses the upstream JavaScript-disabled
configuration. HTTP and HTTPS are registered by the Zenith frontend after
initialisation. Its private `nebula:start` handler uses the initiating
navigation context exposed by `explicit-navigation.patch` to deny embedded
or externally referred requests for the user's bookmarks and recent pages.
Ordinary file URLs use NetSurf's file fetcher. The target
configuration selects stdio reads for those files.

## Upstream sources

| Release input | SHA-256 |
| --- | --- |
| [NetSurf 3.11 complete source release](https://download.netsurf-browser.org/netsurf/releases/source-full/netsurf-all-3.11.tar.gz) | `4dea880ff3c2f698bfd62c982b259340f9abcd7f67e6c8eb2b32c61f71644b7b` |
| [zlib 1.4 release](https://zlib.net/zlib-1.4.tar.gz) | `02dd159783677a749c04adbdd02b55b8a2bcc7e054f51b76b549199a916ad584` |
| [libpng 1.6.59 release](https://downloads.sourceforge.net/project/libpng/libpng16/1.6.59/libpng-1.6.59.tar.xz) | `d80dd2a38a37f803cb9b6ac7b14bd6e74ddc3b654780a8380bdf93523fdb4389` |
| Zenith source subset, `sources.tar.xz` | `5f7fc41018d986772234d9aee246abac30dcdcc2d9dc516ec15d610a9f7a90a5` |

The NetSurf archive digest also matches the original tarball recorded in
[Debian's 3.11 source descriptor](https://deb.debian.org/debian/pool/main/n/netsurf/netsurf_3.11-2.dsc).
The zlib project publishes its release digest on the
[zlib release page](https://zlib.net/).
The libpng archive digest matches the checksum published by the
[PNG reference library project](https://www.libpng.org/pub/png/libpng.html).

The subset includes the source and headers for libcss 0.9.2, libdom 0.4.2,
libhubbub 0.3.8, libparserutils 0.2.5, libwapcaplet 0.4.3, libnsutils 0.1.1,
libnsbmp 0.1.7 and libnsgif 1.0.0 as shipped with NetSurf. NetSurf's
`COPYING` contains GPLv2, the OpenSSL exception and the MIT terms for artwork.
Each NetSurf library retains its own MIT notice in `COPYING`. The separate
zlib release retains its `LICENSE`. The libpng subset includes its `LICENSE`,
`TRADEMARK` and upstream prebuilt configuration header. Source file notices, upstream credits and
English resources remain in the subset. The Zenith browser package installs
the applicable notices and its corresponding source archive.

## Reproducing and building

To reproduce the vendored archive from unmodified downloads, choose a new
output pathname. The packer verifies all three inputs and normalises tar metadata.

```sh
python3 vendor/netsurf/pack-sources.py netsurf-all-3.11.tar.gz zlib-1.4.tar.gz libpng-1.6.59.tar.xz /tmp/netsurf-sources.tar.xz
cmp /tmp/netsurf-sources.tar.xz vendor/netsurf/sources.tar.xz
```

The core build runs offline with Python 3.12+, GCC targeting x86-64, GNU make,
binutils, patch, Perl and a host C compiler with zlib development files.
Run from the repository root.

```sh
python3 tools/netsurf/build-runtime.py
python3 tools/build-netsurf-core.py
```

`obj/netsurf-core/libnetsurf.a` contains the target code. The frontend includes
the staged dependency headers under `obj/netsurf-core/include` and the upstream
headers under `obj/netsurf-core/netsurf-all-3.11/netsurf`. The package builder
copies Messages, the stylesheets and other assets from `obj/netsurf-core/resources`.
The target C library is described in [the musl source note](../netsurf-runtime/SOURCE.md).

`no-curl-header.patch` guards a libcurl-only header using NetSurf's existing
build option. `zenith-platform.patch` declares the supported Zenith VFS and
process features and supplies the system name for the browser user agent.
`core-sources.mk` reads the selected core translation units from upstream's
build rules. `testament.h` records the release identity without embedding host
usernames or build times.

The build runs upstream's entity, character-set, CSS and message generators.
The HTML element lookup is pre-generated with GNU gperf 3.1. To regenerate
`hubbub-element-type.inc`, run the following inside the original `libhubbub`
directory and compare the result with the checked-in copy.

```sh
gperf src/treebuilder/element-type.gperf | sed 's/^\(const struct element_type_map\)/static \1/' > /tmp/hubbub-element-type.inc
```
