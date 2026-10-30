#include "bindings.h"
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static int field(int kind){return kind==BIND_KEYBOARD?1:kind==BIND_TIMEZONE?2:kind==BIND_SESSION?3:0;}
static int bounded_name(const char *s,int capacity){
    for(int i=0;i<capacity;i++){if(!s[i])return i>0;if((unsigned char)s[i]<32||(unsigned char)s[i]>126)return 0;}
    return 0;
}
static int valid(const struct zenith_system_settings *s){
    if((s->version!=1&&s->version!=2)||s->flags&~3U||!s->revision||!s->keyboard_count||s->keyboard_count>8||!s->zone_count||s->zone_count>48||
       s->keyboard>=s->keyboard_count||(s->zone>=s->zone_count&&s->zone!=~0U)||!bounded_name(s->current_zone,64))return 0;
    for(unsigned int i=0;i<s->keyboard_count;i++)if(!bounded_name(s->keyboards[i],32))return 0;
    for(unsigned int i=0;i<s->zone_count;i++)if(!bounded_name(s->zones[i],64))return 0;
    if(s->version==2){
        if(s->session_count>3||(s->session>=s->session_count&&s->session!=~0U)||!bounded_name(s->current_session,16))return 0;
        for(unsigned int i=0;i<s->session_count;i++)if(!bounded_name(s->sessions[i],16))return 0;
    }
    return 1;
}
static unsigned int saved(const struct zenith_system_settings *s,int f){return f==1?s->keyboard:f==2?s->zone:s->session;}
void binding_init(struct meridian_bindings *b,struct settings_app *colors,long (*read)(struct zenith_system_settings *),
                  long (*save)(unsigned int,unsigned int,unsigned long),long (*dirty)(int)){
    *b=(struct meridian_bindings){0};b->colors=colors;b->read=read;b->save=save;b->dirty=dirty;binding_refresh(b);
}
int binding_of(const char *id){
    return same(id,"style.colors")?BIND_COLORS:same(id,"input.keyboard")?BIND_KEYBOARD:same(id,"locale.time")?BIND_TIMEZONE:
        same(id,"system.session")?BIND_SESSION:BIND_NONE;
}
static int preference_changed(const struct meridian_bindings *b,int f){return b->ready&&b->choice[f]!=saved(&b->preferences,f);}
void binding_refresh(struct meridian_bindings *b){
    if(!b->read)return;
    struct zenith_system_settings s;
    if(b->read(&s)||!valid(&s)){b->ready=0;b->message="Settings service unavailable; edits kept.";return;}
    int drafts=0;for(int f=1;f<=3;f++)drafts|=preference_changed(b,f);
    if(drafts&&s.revision!=b->revision)b->message="Changed elsewhere. Reset to reload.";
    for(int f=1;f<=3;f++)if(!preference_changed(b,f))b->choice[f]=saved(&s,f);
    b->preferences=s;b->ready=1;
    if(!drafts){b->revision=s.revision;if(b->message&&same(b->message,"Settings service unavailable; edits kept."))b->message=0;}
}
int binding_changed(const struct meridian_bindings *b,int kind){
    if(kind==BIND_COLORS)return b->colors&&b->colors->edited;
    int f=field(kind);return f&&preference_changed(b,f);
}
int binding_any_changed(const struct meridian_bindings *b){
    for(int k=BIND_COLORS;k<=BIND_SESSION;k++)if(binding_changed(b,k))return 1;
    return 0;
}
int binding_has_defaults(int kind){return kind==BIND_COLORS;}
int binding_at_defaults(const struct meridian_bindings *b,int kind){return kind!=BIND_COLORS||!b->colors||settings_at_defaults(b->colors);}
int binding_choice_count(const struct meridian_bindings *b,int kind){
    int f=field(kind);if(!f||!b->ready)return 0;
    if(f==3&&b->preferences.version!=2)return 0;
    return (int)(f==1?b->preferences.keyboard_count:f==2?b->preferences.zone_count:b->preferences.session_count);
}
const char *binding_choice_name(const struct meridian_bindings *b,int kind,unsigned int i){
    int f=field(kind);if(i>=(unsigned int)binding_choice_count(b,kind))return "";
    return f==1?b->preferences.keyboards[i]:f==2?b->preferences.zones[i]:b->preferences.sessions[i];
}
unsigned int binding_choice(const struct meridian_bindings *b,int kind){int f=field(kind);return f?b->choice[f]:0;}
int binding_writable(const struct meridian_bindings *b,int kind){
    if(kind==BIND_COLORS)return b->colors&&b->colors->have_state&&!!(b->colors->state.flags&Z_SETTINGS_WRITABLE);
    return b->ready&&!!(b->preferences.flags&2)&&binding_choice_count(b,kind)>0;
}
static long mark(struct meridian_bindings *b){return b->dirty?b->dirty(binding_any_changed(b)):0;}
int binding_choose(struct meridian_bindings *b,int kind,unsigned int choice){
    int f=field(kind);if(!f)return -22;
    if(!binding_writable(b,kind))return -13;
    if(choice>=(unsigned int)binding_choice_count(b,kind))return -22;
    unsigned int old=b->choice[f];b->choice[f]=choice;
    if(mark(b)){b->choice[f]=old;b->message="Cannot protect edits; selection kept.";return -5;}
    b->message=0;return 0;
}
long binding_apply(struct meridian_bindings *b,int kind){
    if(kind==BIND_COLORS)return b->colors?settings_apply(b->colors):0;
    int f=field(kind);if(!f||!binding_changed(b,kind))return 0;
    long r=b->save?b->save((unsigned int)f,b->choice[f],b->revision):-38;
    if(r){b->message=r==-11?"Changed elsewhere. Reset to reload.":r==-13?"Administrator permission required.":"Save failed; edits kept.";return r;}
    b->message=f==3?"Default session saved for next boot.":"System default saved and applied.";
    struct zenith_system_settings s;
    if(b->read&&!b->read(&s)&&valid(&s)){b->preferences.revision=s.revision;b->preferences.keyboard=s.keyboard;b->preferences.zone=s.zone;b->preferences.session=s.session;}
    const char *message=b->message;binding_refresh(b);b->message=message;mark(b);return 0;
}
void binding_reset(struct meridian_bindings *b,int kind){
    if(kind==BIND_COLORS){if(b->colors)settings_reset(b->colors);return;}
    int f=field(kind);if(!f)return;
    b->choice[f]=saved(&b->preferences,f);b->message=0;binding_refresh(b);mark(b);
}
void binding_defaults(struct meridian_bindings *b,int kind){if(kind==BIND_COLORS&&b->colors)settings_defaults(b->colors);}
