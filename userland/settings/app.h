#ifndef ZENITH_SETTINGS_APP_H
#define ZENITH_SETTINGS_APP_H
#include "../ui/ui.h"
#include "../include/zenith_settings.h"
struct settings_backend {
    long (*read)(struct zenith_appearance *state);
    long (*save)(unsigned int mode,unsigned int accent,unsigned long revision);
    long (*dirty)(int value);
};
struct settings_app {
    struct zui ui;
    struct zenith_appearance state;
    struct settings_backend backend;
    unsigned int mode,accent;
    unsigned long base_revision;
    int edited,prompt,closing,have_state;
    const char *message;
};
int settings_init(struct settings_app *app,unsigned int *pixels,struct settings_backend backend);
void settings_draw(struct settings_app *app);
void settings_event(struct settings_app *app,const struct zenith_gui_event *event);
void settings_refresh(struct settings_app *app);
#endif
