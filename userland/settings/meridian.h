#ifndef ZENITH_MERIDIAN_H
#define ZENITH_MERIDIAN_H
#include "app.h"
#include "../include/zenith_audio.h"
#include "../include/zenith_network.h"
#include "bindings.h"
#include "styles.h"
#include "navigation.h"
#include "../include/zenith_system_settings.h"
#include "../ui/text.h"
/* Shell over the navigation model, page bindings and shared widgets. Control ids:
 * 1,2 mode; 10-15 accent; 50-54 effects; 60,61 scope; 100+ sidebar rows; 900+ footer;
 * 950+ dialog; 1002 search; 2004 combo, 2100+ its rows; 2006 scale combo, 2400+ its rows;
 * 3000+ page links; 4001/4002 display keep/revert; 4003 unlock. */
enum { FADE_PAGE=1,FADE_SIDEBAR=2 };
struct meridian {
    struct zui ui;
    struct zenith_audio audio;
    long (*audio_read)(struct zenith_audio *);
    long (*audio_control)(const struct zenith_audio_control *);
    int audio_error;
    const char *audio_message;
    unsigned long audio_drawn[16];
    /* Network (call 667, kernel 0.0.69): unset in host tests and older kernels, where the page shows the text summary. */
    long (*network)(unsigned int op,unsigned int value,void *output,unsigned int capacity,const void *input,unsigned int bytes);
    struct zenith_network_status net; int net_ok,net_mode,net_field,net_edited,net_locked,net_busy;
    char net_text[4][40]; struct zui_input net_inputs[4]; struct zui_rect net_rects[4]; char net_message[200];

    struct settings_app colors;
    struct meridian_bindings bindings;
    struct meridian_nav nav;
    struct zui_list sidebar;
    struct zui_scroll page_scroll;
    struct zui_combo combo,scale_combo;
    struct zui_dialog dialog,display_dialog;
    char display_countdown[64];
    struct zui_input search;
    char query[64];
    int page,search_focus,closing;
    int focus_row; /* sidebar row to focus next frame: -1 none, -2 the highlighted row */
    int pending,pending_row,pending_page;
    int recent[6],recent_count;
    long (*information_read)(unsigned int,struct zenith_settings_information *);
    char public_information[4096];unsigned int public_kind;int public_error;
    char information[4096];
    /* Unlock through the Desktop Portal (org.zenith.settings.system); unset in host tests and on
     * old kernels. authorize returns a handle; authorization_poll returns the state (final ones
     * close it) and the portal's message. */
    long (*authorize)(void);
    long (*authorization_poll)(long handle,char *message,unsigned long size);
    long unlock_handle;char unlock_note[100];
    /* The pickers (call 669, kernel 0.0.72): wallpapers, cursors, decorations, widgets, startup styles, sounds. */
    struct meridian_styles styles;
    /* What the next present should crossfade from the last one; fade_ready when set. */
    int fade_request,fade_ready,fade_rise;struct zui_rect fade_rect;
};
int meridian_init(struct meridian *app,unsigned int *pixels,struct settings_backend backend,const char *page);
void meridian_style_services(struct meridian *a,long (*catalog)(unsigned int,struct zenith_style_list *),
                             long (*preview)(unsigned int,unsigned int,int,int,unsigned int *));
void meridian_notify_services(struct meridian *a,long (*read)(struct zenith_notify_settings *),long (*save)(unsigned int,unsigned int,unsigned long),long (*post)(const char *,const char *,const char *));
void meridian_notify_senders(struct meridian *a,long (*senders)(char *,unsigned int),long (*mute)(const char *,unsigned int,unsigned long));
void meridian_sound_services(struct meridian *a,long (*read)(struct zenith_sound_settings *),long (*save)(unsigned int,unsigned int,unsigned long),long (*play)(unsigned int));
int meridian_resize(struct meridian *app,int width,int height,unsigned long capacity);
void meridian_draw(struct meridian *app);
void meridian_event(struct meridian *app,const struct zenith_gui_event *event);
void meridian_system_services(struct meridian *app,long (*read)(struct zenith_system_settings *),long (*save)(unsigned int,unsigned int,unsigned long));
void meridian_information_services(struct meridian *app,long (*read)(unsigned int,struct zenith_settings_information *));
void meridian_network_services(struct meridian *app,long (*call)(unsigned int,unsigned int,void *,unsigned int,const void *,unsigned int));
void meridian_audio_services(struct meridian *app,long (*read)(struct zenith_audio *),long (*control)(const struct zenith_audio_control *));
void meridian_refresh(struct meridian *app);
int meridian_open(struct meridian *app,const char *id);
struct zui *meridian_ui(struct meridian *app);
#endif
