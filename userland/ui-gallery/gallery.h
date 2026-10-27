#ifndef ZENITH_UI_GALLERY_H
#define ZENITH_UI_GALLERY_H
#include "../ui/ui.h"
struct gallery { struct zui ui; int light,accent; };
int gallery_init(struct gallery *g,unsigned int *pixels,int light);
void gallery_draw(struct gallery *g);
int gallery_event(struct gallery *g,const struct zenith_gui_event *event);
#endif
