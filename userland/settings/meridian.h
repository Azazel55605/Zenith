#ifndef ZENITH_MERIDIAN_H
#define ZENITH_MERIDIAN_H
#include "app.h"
#include "bindings.h"
#include "navigation.h"
#include "../include/zenith_system_settings.h"
#include "../ui/text.h"
/* Shell over the navigation model, page bindings and shared widgets. Control ids:
 * 1,2 mode; 10-15 accent; 50-54 effects; 60,61 scope; 100+ sidebar rows; 900+ footer;
 * 950+ dialog; 1002 search; 2004 combo, 2100+ its rows; 3000+ page links. */
struct meridian {
    struct zui ui;
    struct settings_app colors;
    struct meridian_bindings bindings;
    struct meridian_nav nav;
    struct zui_list sidebar;
    struct zui_scroll page_scroll;
    struct zui_combo combo;
    struct zui_dialog dialog;
    struct zui_input search;
    char query[64];
    int page,search_focus,closing;
    int pending,pending_row,pending_page;
    int recent[6],recent_count;
    long (*information_read)(unsigned int,struct zenith_settings_information *);
    char public_information[4096];unsigned int public_kind;int public_error;
    char information[4096];
};
int meridian_init(struct meridian *app,unsigned int *pixels,struct settings_backend backend,const char *page);
int meridian_resize(struct meridian *app,int width,int height,unsigned long capacity);
void meridian_draw(struct meridian *app);
void meridian_event(struct meridian *app,const struct zenith_gui_event *event);
void meridian_system_services(struct meridian *app,long (*read)(struct zenith_system_settings *),long (*save)(unsigned int,unsigned int,unsigned long));
void meridian_information_services(struct meridian *app,long (*read)(unsigned int,struct zenith_settings_information *));
void meridian_refresh(struct meridian *app);
int meridian_open(struct meridian *app,const char *id);
struct zui *meridian_ui(struct meridian *app);
#endif
