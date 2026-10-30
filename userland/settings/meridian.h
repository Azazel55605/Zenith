#ifndef ZENITH_MERIDIAN_H
#define ZENITH_MERIDIAN_H
#include "app.h"
#include "../include/zenith_system_settings.h"
#include "../ui/text.h"
#include "navigation.h"
/* Complete catalogue; controls are virtualized to eight visible results. */
struct meridian {
    struct settings_app colors;
    struct zenith_system_settings preferences;
    long (*preferences_read)(struct zenith_system_settings *);
    long (*preferences_save)(unsigned int,unsigned int,unsigned long);
    int preference_picker,preference_offset,preference_search_focus;
    char preference_query[64];struct zui_input preference_search;
    long (*information_read)(unsigned int,struct zenith_settings_information *);
    char public_information[4096];unsigned int public_kind;int public_error;
    unsigned int preference_field,preference_choice;
    unsigned long preference_revision;
    int preference_edited,preference_prompt,preferences_ready;
    const char *preference_message;
    struct zui navigation;
    struct zui_input search;
    char query[128];
    char information[4096];
    int page,category,group,offset,scroll,search_focus,closing,lines;
    int category_offset,pending_category;
    int recent[6],recent_count,matches[MERIDIAN_PAGE_COUNT],match_count;
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
