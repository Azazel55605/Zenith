#ifndef ZENITH_MERIDIAN_BINDINGS_H
#define ZENITH_MERIDIAN_BINDINGS_H
#include "app.h"
#include "../include/zenith_system_settings.h"
/* One draft contract for every editable Meridian page. A page is changed while its
 * draft differs from saved state; the window's unsaved flag covers all pages. */
enum { BIND_NONE=0,BIND_COLORS=1,BIND_KEYBOARD=2,BIND_TIMEZONE=3,BIND_SESSION=4 };
struct meridian_bindings {
    struct settings_app *colors;
    struct zenith_system_settings preferences; int ready;
    long (*read)(struct zenith_system_settings *);
    long (*save)(unsigned int field,unsigned int choice,unsigned long revision);
    long (*dirty)(int value);
    unsigned int choice[4];      /* system preference drafts, fields 1..3 */
    unsigned long revision;      /* revision the drafts are based on */
    const char *message;
};
void binding_init(struct meridian_bindings *b,struct settings_app *colors,long (*read)(struct zenith_system_settings *),
                  long (*save)(unsigned int,unsigned int,unsigned long),long (*dirty)(int));
int binding_of(const char *page_id);
void binding_refresh(struct meridian_bindings *b);
int binding_changed(const struct meridian_bindings *b,int kind);
int binding_any_changed(const struct meridian_bindings *b);
int binding_has_defaults(int kind);
int binding_at_defaults(const struct meridian_bindings *b,int kind);
int binding_choice_count(const struct meridian_bindings *b,int kind);
const char *binding_choice_name(const struct meridian_bindings *b,int kind,unsigned int index);
unsigned int binding_choice(const struct meridian_bindings *b,int kind);
int binding_writable(const struct meridian_bindings *b,int kind);
/* Errors: -13 read only, -22 invalid choice, -5 unsaved state not recorded. */
int binding_choose(struct meridian_bindings *b,int kind,unsigned int choice);
long binding_apply(struct meridian_bindings *b,int kind);
void binding_reset(struct meridian_bindings *b,int kind);
void binding_defaults(struct meridian_bindings *b,int kind);
#endif
