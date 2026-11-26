# Read the selected NetSurf 3.11 translation units from its own build rules.
NETSURF_USE_BMP := YES
NETSURF_USE_GIF := YES
NETSURF_USE_PNG := YES
NETSURF_USE_JPEG := YES
NETSURF_USE_DUKTAPE := NO
include content/Makefile
include utils/Makefile
include utils/http/Makefile
include utils/nsurl/Makefile
include desktop/Makefile
.PHONY: sources
sources:
	@printf '%s\n' $(S_CONTENT) $(S_FETCHERS) $(S_UTILS) $(S_HTTP) $(S_NSURL) $(S_DESKTOP) $(S_BROWSER) $(S_IMAGE) content/handlers/html/layout_grid.c
