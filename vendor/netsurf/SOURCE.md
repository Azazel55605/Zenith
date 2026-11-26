# NetSurf browser core for Zenith

The native browser compiles NetSurf 3.11's HTML layout, CSS cascade, DOM,
redraw, navigation, content cache and URL handling. The selected image handlers
support JPEG, PNG, GIF, BMP and ICO. NetSurf's progressive PNG handler uses the pinned
libpng decoder with the same zlib as the browser core. The browser embeds the
Duktape 2.7.0 interpreter and WebIDL bindings from the same NetSurf release.
HTTP and HTTPS are registered by the Zenith frontend after
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
| [IJG libjpeg 10 release](https://www.ijg.org/files/jpegsrc.v10.tar.gz) | `8b9eaa13242690ebd03e1728ab1edf97a81a78ed6e83624d493655f31ac95ab5` |
| Zenith source subset, `sources.tar.xz` | `987f93fe8eddee92139390af2dbc83f920151af3357b8ba8bba555b2c8a79603` |

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

The JPEG decoder uses the upstream IJG decompression sources, portable ANSI C
configuration and in-memory allocator. This software is based in part on the
work of the Independent JPEG Group. The package includes IJG's full README.
`jpeg-limits.patch` updates the NetSurf handler to reject images above the
frontend's 8,388,608-pixel bitmap limit before decompression and caps progressive
coefficient storage requests through IJG's 8 MiB memory setting. Incomplete
images release their partial bitmaps when the decoder reaches the end of input.

## Responsive CSS and layout

The cross-build applies `libcss-*.patch` to LibCSS, `libdom-*.patch` to LibDOM and the remaining patches to NetSurf. It stages the source overlays in `grid/`, `variables/` and `math/`, regenerates LibCSS’s computed-style accessors and compiles `layout_grid.c` into the HTML handler. The build fingerprint includes the relative paths and contents of the overlays, patches and archive. The signed browser’s corresponding source includes these inputs.

Media conditions are evaluated by LibCSS. When a condition changes, the HTML handler reconstructs presentation boxes from the live DOM, transfers control and image ownership and recalculates Find matches. Ordinary changes within a breakpoint use NetSurf’s reformat path. The Grid parser stores its variable-length track lists and named areas in the stylesheet’s intern table. A template accepts up to 128 authored tracks per axis. Compiled math expressions in a track list share the width evaluator and use the containing Grid’s actual dimensions. The Flexbox changes cover basis parsing, wrapping, gaps and alignment. [CSS Grid Level 1](https://www.w3.org/TR/css-grid-1/) · [CSS Flexible Box Layout](https://www.w3.org/TR/css-flexbox-1/)

## JavaScript implementation

The upstream release includes Duktape 2.7.0, its MIT notice and NetSurf's
DOM binding descriptions under `content/handlers/javascript/duktape`. The core
build compiles the release's nsgenbind 0.9 on the host, generates the DOM C
bindings from the included WebIDL and compiles those bindings with the target
SDK. Regenerating the source subset uses GNU Bison 3.8.2 and Flex 2.7.6. Their
generated parser sources are in the subset so normal package builds work offline
with the existing host C toolchain. Installed packages carry the Duktape notice,
generator sources, binders and the complete source needed to repeat the build.

Every script-enabled document receives a private interpreter heap on first use.
Callbacks have a 500 ms monotonic execution budget. Nested callbacks share the
outer deadline. Interpreter allocations are limited to 4 MiB per document and
10 MiB across the process. Duktape's embedding helpers and bytecode loading are
unavailable to page scripts. DOM text, attributes and class tokens convert
between JavaScript's UTF-16 code units and LibDOM’s UTF-8 strings. Closing a
document cancels its timers and detaches its native event listeners. Window
names are scoped to one document. The Zenith HTTP transport has no cookie jar,
and the DOM reports cookies disabled.

## Reproducing and building

To reproduce the vendored archive from unmodified downloads, choose a new
output pathname. The packer verifies all four inputs and normalises tar metadata.

```sh
python3 vendor/netsurf/pack-sources.py netsurf-all-3.11.tar.gz zlib-1.4.tar.gz libpng-1.6.59.tar.xz jpegsrc.v10.tar.gz /tmp/netsurf-sources.tar.xz
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

## CSS math

The LibCSS patch adapts the computed-value union, generated accessors and ownership logic from `src/select/assets.py`, `src/select/select_generator.py` and `src/select/properties/width.c` at revision `d991ff10e4b23b06a345540a4b243c50d7a40368`. This checkout extends the representation to minimum and maximum widths. The NetSurf patch adapts the used-width changes in `content/handlers/html/layout.c` and `layout_internal.h` from the same upstream series through revision `e1d7a346dddf7d09a9738c48ca57663f090637ef`. [LibCSS source](https://github.com/netsurf-browser/libcss/commit/d991ff10e4b23b06a345540a4b243c50d7a40368) · [NetSurf layout changes](https://github.com/netsurf-browser/netsurf/commit/e1d7a346dddf7d09a9738c48ca57663f090637ef)

The local `math/` parser validates length-percentage expressions and encodes them as interned postfix instructions. Widths and Grid tracks resolve those instructions at layout using the enclosing box, element and root fonts, and actual viewport. LibCSS scales the complete author length to device pixels before rounding. At 90 and 144 DPI, both `240px` and `calc(120px + 120px)` therefore paint 225 and 360 device pixels, respectively. The supported functions are `calc()`, `min()`, `max()` and `clamp()`, including nested calls and scalar multiplication and division. A single expression accepts up to 128 instructions and 32 nested functions or groups.
