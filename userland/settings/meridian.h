#ifndef ZENITH_MERIDIAN_H
#define ZENITH_MERIDIAN_H
#include "app.h"
#include "../ui/text.h"
#define MERIDIAN_PAGE_COUNT 43
enum { MERIDIAN_COLORS=16, MERIDIAN_ABOUT=38 };
struct meridian_page { const char *id,*title,*category,*requirements,*foundation; int group; };
extern const struct meridian_page meridian_pages[MERIDIAN_PAGE_COUNT];
/* Complete catalogue; controls are virtualized to eight visible results. */
struct meridian {
    struct settings_app colors;
    struct zui navigation;
    struct zui_input search;
    char query[128];
    char information[4096];
    int page,category,group,offset,scroll,search_focus,closing,lines;
    int recent[6],recent_count,matches[MERIDIAN_PAGE_COUNT],match_count;
};
int meridian_init(struct meridian *app,unsigned int *pixels,struct settings_backend backend,const char *page);
void meridian_draw(struct meridian *app);
void meridian_event(struct meridian *app,const struct zenith_gui_event *event);
void meridian_refresh(struct meridian *app);
int meridian_open(struct meridian *app,const char *id);
struct zui *meridian_ui(struct meridian *app);
#endif
