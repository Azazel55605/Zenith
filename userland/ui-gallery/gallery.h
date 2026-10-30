#ifndef ZENITH_UI_GALLERY_H
#define ZENITH_UI_GALLERY_H
#include "../ui/ui.h"
#include "../ui/widgets.h"
/* Page 0: appearance controls. Page 1 (Ctrl+Right): stage-1 widget preview. */
#define GALLERY_TOGGLE 60
#define GALLERY_FOOTER 900
#define GALLERY_DIALOG 950
struct gallery {
    struct zui ui; int light,accent,page;
    int toggles[2],saved_toggles[2],saved_layout;
    struct zui_list list; struct zui_combo combo; struct zui_scroll scroll; struct zui_dialog dialog;
};
int gallery_init(struct gallery *g,unsigned int *pixels,int light);
void gallery_draw(struct gallery *g);
int gallery_event(struct gallery *g,const struct zenith_gui_event *event);
#endif
