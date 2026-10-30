#ifndef ZENITH_SETTINGS_APP_H
#define ZENITH_SETTINGS_APP_H
#include "../ui/ui.h"
#include "../include/zenith_settings.h"
struct settings_backend {
    long (*read)(struct zenith_appearance *state,unsigned int *saved,unsigned int *effective,unsigned int *available,unsigned int *reason,unsigned int *level);
    long (*save)(unsigned int mode,unsigned int accent,unsigned int materials,unsigned int level,unsigned long revision);
    long (*dirty)(int value);
    long (*scope)(unsigned int action,unsigned long revision);
};
struct settings_app {
    struct zui ui;
    struct zui_rect viewport;
    struct zenith_appearance state;
    struct settings_backend backend;
    unsigned int scope,inherited,has_scopes;
    unsigned int translucency,saved_translucency;
    unsigned int mode,accent,effects,saved_effects,effective_effects,available_effects,material_reason;
    unsigned long base_revision;
    int edited,prompt,closing,have_state;
    const char *message;
};
int settings_init(struct settings_app *app,unsigned int *pixels,struct settings_backend backend);
void settings_set_viewport(struct settings_app *app,struct zui_rect viewport);
void settings_request_navigation(struct settings_app *app);
void settings_draw(struct settings_app *app);
int settings_event(struct settings_app *app,const struct zenith_gui_event *event);
void settings_refresh(struct settings_app *app);
/* Drawing-free model used by Meridian. Edits are drafts until settings_apply.
 * Errors: -13 read only, -95 effect unavailable, -22 invalid value, -5 the window
 * could not record unsaved state, -16 scope change refused while edited. */
int settings_preview(struct settings_app *app,unsigned int mode,unsigned int accent,unsigned int effects,unsigned int level);
long settings_apply(struct settings_app *app);
int settings_reset(struct settings_app *app);
/* Personal scope: follow system defaults (saved immediately). System scope: draft of
 * dark, blue, effects off, translucency 40. */
int settings_defaults(struct settings_app *app);
int settings_at_defaults(const struct settings_app *app);
int settings_set_scope(struct settings_app *app,unsigned int personal);
void settings_theme(const struct settings_app *app,struct zui *ui);
#endif
