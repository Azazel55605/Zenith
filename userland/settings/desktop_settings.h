#ifndef MERIDIAN_DESKTOP_SETTINGS_H
#define MERIDIAN_DESKTOP_SETTINGS_H
#include "../include/zenith_desktop_preferences.h"
#include "../ui/widgets.h"
#include "../ui/text.h"

struct meridian_desktop {
    long (*read)(unsigned int,struct zenith_desktop_preferences *);
    long (*save)(const struct zenith_desktop_preferences *);
    long (*apps)(struct zenith_desktop_apps *);
    long (*dirty)(int);
    struct zenith_desktop_preferences saved,draft;
    struct zenith_desktop_apps catalog;
    struct zui_combo choice;
    struct zui_input rename;
    const char *message;
    int ready,field,naming;
};

unsigned int desktop_page(const char *);
void desktop_enter(struct meridian_desktop *,unsigned int);
int desktop_refresh(struct meridian_desktop *,unsigned int);
int desktop_changed(const struct meridian_desktop *);
int desktop_default(const struct meridian_desktop *);
long desktop_apply(struct meridian_desktop *);
void desktop_reset(struct meridian_desktop *,int defaults);
void desktop_form(struct meridian_desktop *,struct zui *,struct zui_form *);
void desktop_popup(struct meridian_desktop *,struct zui *);
int desktop_event(struct meridian_desktop *,struct zui *,const struct zenith_gui_event *);
int desktop_action(struct meridian_desktop *,struct zui *,int);
void desktop_cancel_input(struct meridian_desktop *);
#endif
