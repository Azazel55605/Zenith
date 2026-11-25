#ifndef ZENITH_MERIDIAN_STYLES_H
#define ZENITH_MERIDIAN_STYLES_H
#include "../include/zenith_style.h"
#include "../include/zenith_notify.h"
/* Meridian's pickers (call 669, kernel 0.0.72): every style setting a page shows as previews, with one draft per
 * field (6 wallpaper, 8 cursor pack, 9 cursor size, 10 decoration, 11 widgets, 12 buttons, 13 splash, 14 login,
 * 15 boot, 16 sounds). Saving goes through the system settings call (626) with the same field numbers. */
/* The largest picker picture: a default tile (176x110) plus the 3 px it now grows on each side to fill the tile. */
enum { STYLE_FIELDS=18,STYLE_THUMBS=40,STYLE_THUMB_PIXELS=182*116,STYLE_NOTIFY_SENDERS=96 };
struct meridian_sender { char name[56]; unsigned char off,draft; };
struct meridian_styles {
    long (*catalog)(unsigned int field,struct zenith_style_list *out);
    long (*preview)(unsigned int field,unsigned int index,int width,int height,unsigned int *pixels);
    long (*save)(unsigned int field,unsigned int choice,unsigned long revision);
    long (*dirty)(int value);
    struct zenith_style_list list[STYLE_FIELDS]; unsigned char ready[STYLE_FIELDS];
    unsigned int draft[STYLE_FIELDS];
    const char *message;
    /* System sounds (669 ops 3..5): level and per-event switches, drafted like the choices. */
    long (*sounds)(struct zenith_sound_settings *out);
    long (*sound_save)(unsigned int level,unsigned int mask,unsigned long revision);
    long (*sound_play)(unsigned int event);
    struct zenith_sound_settings sound; int sound_ready; unsigned int sound_level,sound_mask;
    /* Notifications (670): revisioned visibility, popup behavior, and a test post. */
    long (*notify_read)(struct zenith_notify_settings *out);
    long (*notify_save)(const struct zenith_notify_settings *settings);
    long (*notify_post)(const char *title,const char *body,const char *icon);
    struct zenith_notify_settings notify; int notify_ready; unsigned int notify_dnd,notify_popup,notify_flags,notify_corner;
    /* Per-app switches (670 ops 4 and 5): the senders seen, each switched off or on, with a draft. */
    long (*notify_senders)(char *out,unsigned int capacity);
    long (*notify_mute)(const char *name,unsigned int off,unsigned long revision);
    struct meridian_sender senders[STYLE_NOTIFY_SENDERS]; int sender_count;
    /* Preview cache: (field, index, size) per slot, dropped when the appearance key changes. */
    unsigned int thumb_field[STYLE_THUMBS],thumb_index[STYLE_THUMBS],thumb_size[STYLE_THUMBS],thumb_key,thumb_next;
    unsigned int thumbs[STYLE_THUMBS][STYLE_THUMB_PIXELS];
};
void styles_init(struct meridian_styles *s);
/* Reloads the lists; drafts that differ from the saved choice are kept. */
void styles_refresh(struct meridian_styles *s);
/* The style fields a page shows, in order; returns how many. */
int styles_page_fields(const char *page_id,unsigned int *out);
int styles_field_ready(const struct meridian_styles *s,unsigned int field);
int styles_changed(const struct meridian_styles *s,const char *page_id);
int styles_any_changed(const struct meridian_styles *s);
int styles_choose(struct meridian_styles *s,unsigned int field,unsigned int choice);
long styles_apply(struct meridian_styles *s,const char *page_id);
void styles_reset(struct meridian_styles *s,const char *page_id);
int styles_sound_set(struct meridian_styles *s,unsigned int level,unsigned int mask);
int styles_notify_set(struct meridian_styles *s,unsigned int dnd,unsigned int popup,unsigned int flags,unsigned int corner);
int styles_sender_toggle(struct meridian_styles *s,int index);
/* A cached preview (ARGB, width x height up to 176 x 110), or 0 when the kernel cannot draw it. */
const unsigned int *styles_thumb(struct meridian_styles *s,unsigned int field,unsigned int index,int width,int height,unsigned int key);
#endif
