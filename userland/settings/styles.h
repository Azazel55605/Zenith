#ifndef ZENITH_MERIDIAN_STYLES_H
#define ZENITH_MERIDIAN_STYLES_H
#include "../include/zenith_style.h"
/* Meridian's pickers (call 669, kernel 0.0.71): every style setting a page shows as previews, with one draft per
 * field (6 wallpaper, 8 cursor pack, 9 cursor size, 10 decoration, 11 widgets, 12 buttons, 13 splash, 14 login,
 * 15 boot, 16 sounds). Saving goes through the system settings call (626) with the same field numbers. */
enum { STYLE_FIELDS=17,STYLE_THUMBS=40,STYLE_THUMB_PIXELS=176*110 };
struct meridian_styles {
    long (*catalog)(unsigned int field,struct zenith_style_list *out);
    long (*preview)(unsigned int field,unsigned int index,int width,int height,unsigned int *pixels);
    long (*save)(unsigned int field,unsigned int choice,unsigned long revision);
    long (*dirty)(int value);
    struct zenith_style_list list[STYLE_FIELDS]; unsigned char ready[STYLE_FIELDS];
    unsigned int draft[STYLE_FIELDS];
    const char *message;
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
/* A cached preview (ARGB, width x height up to 176 x 110), or 0 when the kernel cannot draw it. */
const unsigned int *styles_thumb(struct meridian_styles *s,unsigned int field,unsigned int index,int width,int height,unsigned int key);
#endif
