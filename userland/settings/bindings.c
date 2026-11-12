#include "bindings.h"
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static int field(int kind){return kind==BIND_KEYBOARD?1:kind==BIND_TIMEZONE?2:kind==BIND_SESSION?3:kind==BIND_ANIMATIONS?4:kind==BIND_TASKBAR?5:kind==BIND_WALLPAPER?6:kind==BIND_LAUNCHER?7:kind==BIND_CURSOR?8:0;}
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
        if(s->animation_count>4||(s->animation_count&&s->animation>=s->animation_count))return 0;
        for(unsigned int i=0;i<s->animation_count;i++)if(!bounded_name(s->animations[i],24))return 0;
        if(s->taskbar_count>5||(s->taskbar_count&&s->taskbar>=s->taskbar_count))return 0;
        for(unsigned int i=0;i<s->taskbar_count;i++)if(!bounded_name(s->taskbars[i],24))return 0;
        if(s->wallpaper_count>12||(s->wallpaper_count&&s->wallpaper>=s->wallpaper_count))return 0;
        for(unsigned int i=0;i<s->wallpaper_count;i++)if(!bounded_name(s->wallpapers[i],24))return 0;
        if(s->launcher_count>2||(s->launcher_count&&s->launcher>=s->launcher_count))return 0;
        if(s->cursor_count>2||(s->cursor_count&&s->cursor>=s->cursor_count))return 0;
        for(unsigned int i=0;i<s->launcher_count;i++)if(!bounded_name(s->launchers[i],24))return 0;
    }
    return 1;
}
static unsigned int saved(const struct zenith_system_settings *s,int f){return f==1?s->keyboard:f==2?s->zone:f==3?s->session:f==4?s->animation:f==5?s->taskbar:f==6?s->wallpaper:f==7?s->launcher:s->cursor;}
void binding_init(struct meridian_bindings *b,struct settings_app *colors,long (*read)(struct zenith_system_settings *),
                  long (*save)(unsigned int,unsigned int,unsigned long),long (*dirty)(int)){
    *b=(struct meridian_bindings){0};b->colors=colors;b->read=read;b->save=save;b->dirty=dirty;binding_refresh(b);
}
int binding_of(const char *id){
    return same(id,"display.configuration")?BIND_DISPLAY:same(id,"style.colors")?BIND_COLORS:same(id,"input.keyboard")?BIND_KEYBOARD:same(id,"locale.time")?BIND_TIMEZONE:
        same(id,"system.session")?BIND_SESSION:same(id,"style.animations")?BIND_ANIMATIONS:same(id,"windows.taskbar")?BIND_TASKBAR:
        same(id,"style.wallpaper")?BIND_WALLPAPER:same(id,"workspace.search")?BIND_LAUNCHER:same(id,"style.cursors")?BIND_CURSOR:BIND_NONE;
}
static int preference_changed(const struct meridian_bindings *b,int f){return b->ready&&b->choice[f]!=saved(&b->preferences,f);}
static char *digits(char *s,unsigned int v){char d[10];int n=0;do{d[n++]=(char)('0'+v%10);v/=10;}while(v);while(n)*s++=d[--n];return s;}
/* The current scale in percent: kernels without fractional scales report only 1 or 2. */
static unsigned int display_percent(const struct zenith_display *s){return (s->flags&8)&&s->percent?s->percent:s->scale*100;}
static int display_saved(const struct meridian_bindings *b){
    for(unsigned int i=0;i<b->display_count;i++){unsigned int m=b->display_modes[i];if(b->display.modes[m].width==b->display.width&&b->display.modes[m].height==b->display.height&&b->display_scales[i]==display_percent(&b->display))return (int)i;}
    return -1;
}
static void display_refresh(struct meridian_bindings *b){
    if(!b->display_read)return;
    struct zenith_display s={0};
    if(b->display_read(&s)||s.version!=1||s.mode_count>24||s.phase>2||s.scale<1||s.scale>2){b->display_ready=0;return;}
    int old_saved=display_saved(b),draft=b->display.revision&&((int)b->display_choice!=old_saved||(b->display.phase&&(b->display.flags&4)));
    int ended=b->display.phase&&!s.phase;
    b->display=s;b->display_ready=1;b->display_count=0;
    static const unsigned int percents[5]={100,125,150,175,200};
    for(unsigned int m=0;m<s.mode_count;m++)for(unsigned int k=0;k<5;k++){
        unsigned int w=s.modes[m].width,h=s.modes[m].height,p=percents[k];
        if(p!=100&&p!=200&&!(s.flags&8))continue;
        if(w*100/p<800||h*100/p<600||(p==200&&(w%2||h%2))||w>3840||h>2400||b->display_count>=120)continue;
        unsigned int i=b->display_count++;b->display_modes[i]=m;b->display_scales[i]=p;
        char *t=b->display_labels[i];t=digits(t,w);*t++=' ';*t++='x';*t++=' ';t=digits(t,h);*t++=' ';*t++='(';t=digits(t,p);*t++='%';*t++=')';*t=0;
    }
    b->resolution_count=0;
    for(unsigned int i=0;i<b->display_count;i++){
        unsigned int m=b->display_modes[i],known=0;
        for(unsigned int r=0;r<b->resolution_count;r++)if(b->resolution_modes[r]==m)known=1;
        if(known||b->resolution_count>=24)continue;
        unsigned int r=b->resolution_count++;b->resolution_modes[r]=m;
        char *t=b->resolution_labels[r];t=digits(t,s.modes[m].width);*t++=' ';*t++='x';*t++=' ';t=digits(t,s.modes[m].height);*t=0;
    }
    if(!draft||ended){int saved=display_saved(b);b->display_choice=saved<0?0:(unsigned int)saved;b->display_revision=s.revision;}
    if(ended){b->display_message=s.error?"Display change failed; previous mode restored.":"Display is ready.";if(b->dirty)b->dirty(binding_any_changed(b));}
}
void binding_refresh(struct meridian_bindings *b){
    display_refresh(b);
    if(!b->read)return;
    struct zenith_system_settings s;
    if(b->read(&s)||!valid(&s)){b->ready=0;b->message="Settings service unavailable; edits kept.";return;}
    int drafts=0;for(int f=1;f<=8;f++)drafts|=preference_changed(b,f);
    if(drafts&&s.revision!=b->revision)b->message="Changed elsewhere. Reset to reload.";
    for(int f=1;f<=8;f++)if(!preference_changed(b,f))b->choice[f]=saved(&s,f);
    b->preferences=s;b->ready=1;
    if(!drafts){b->revision=s.revision;if(b->message&&same(b->message,"Settings service unavailable; edits kept."))b->message=0;}
}
int binding_changed(const struct meridian_bindings *b,int kind){
    if(kind==BIND_DISPLAY)return b->display.revision&&((b->display.phase&&(b->display.flags&4))||(int)b->display_choice!=display_saved(b));
    if(kind==BIND_COLORS)return b->colors&&b->colors->edited;
    int f=field(kind);return f&&preference_changed(b,f);
}
int binding_any_changed(const struct meridian_bindings *b){
    for(int k=BIND_COLORS;k<=BIND_CURSOR;k++)if(binding_changed(b,k))return 1;
    return 0;
}
int binding_has_defaults(int kind){return kind==BIND_COLORS;}
int binding_at_defaults(const struct meridian_bindings *b,int kind){return kind!=BIND_COLORS||!b->colors||settings_at_defaults(b->colors);}
/* The resolution of a (mode, scale) pair, and the pairs of one resolution in scale order. */
static unsigned int resolution_of(const struct meridian_bindings *b,unsigned int pair){
    for(unsigned int r=0;r<b->resolution_count;r++)if(pair<b->display_count&&b->resolution_modes[r]==b->display_modes[pair])return r;
    return 0;
}
static int scale_pairs(const struct meridian_bindings *b,unsigned int resolution,unsigned int *out){
    int n=0;if(resolution>=b->resolution_count)return 0;
    for(unsigned int i=0;i<b->display_count&&n<5;i++)if(b->display_modes[i]==b->resolution_modes[resolution])out[n++]=i;
    return n;
}
int binding_scale_count(const struct meridian_bindings *b){unsigned int p[5];return b->display_ready?scale_pairs(b,resolution_of(b,b->display_choice),p):0;}
const char *binding_scale_name(const struct meridian_bindings *b,unsigned int index){
    unsigned int p[5];int n=scale_pairs(b,resolution_of(b,b->display_choice),p);
    if((int)index>=n)return "";
    char *label=(char *)b->scale_labels[index],*t=digits(label,b->display_scales[p[index]]);*t++='%';*t=0;return label;
}
unsigned int binding_scale_choice(const struct meridian_bindings *b){
    unsigned int p[5];int n=scale_pairs(b,resolution_of(b,b->display_choice),p);
    for(int i=0;i<n;i++)if(p[i]==b->display_choice)return (unsigned int)i;
    return 0;
}
int binding_choose_scale(struct meridian_bindings *b,unsigned int index){
    unsigned int p[5];int n=scale_pairs(b,resolution_of(b,b->display_choice),p);
    if((int)index>=n)return -22;
    return binding_choose(b,-BIND_DISPLAY,p[index]);
}
int binding_choice_count(const struct meridian_bindings *b,int kind){
    if(kind==BIND_DISPLAY)return b->display_ready?(int)b->resolution_count:0;
    int f=field(kind);if(!f||!b->ready)return 0;
    if(f>=3&&b->preferences.version!=2)return 0;
    return (int)(f==1?b->preferences.keyboard_count:f==2?b->preferences.zone_count:f==3?b->preferences.session_count:f==4?b->preferences.animation_count:
        f==5?b->preferences.taskbar_count:f==6?b->preferences.wallpaper_count:f==7?b->preferences.launcher_count:b->preferences.cursor_count);
}
const char *binding_choice_name(const struct meridian_bindings *b,int kind,unsigned int i){
    if(kind==BIND_DISPLAY)return i<b->resolution_count?b->resolution_labels[i]:"";
    int f=field(kind);if(i>=(unsigned int)binding_choice_count(b,kind))return "";
    return f==1?b->preferences.keyboards[i]:f==2?b->preferences.zones[i]:f==3?b->preferences.sessions[i]:f==4?b->preferences.animations[i]:
        f==5?b->preferences.taskbars[i]:f==6?b->preferences.wallpapers[i]:f==7?b->preferences.launchers[i]:i==0?"Meridian Light":"Meridian Dark";
}
unsigned int binding_choice(const struct meridian_bindings *b,int kind){if(kind==BIND_DISPLAY)return resolution_of(b,b->display_choice);int f=field(kind);return f?b->choice[f]:0;}
int binding_writable(const struct meridian_bindings *b,int kind){
    if(kind==BIND_DISPLAY)return b->display_ready&&(b->display.flags&2)&&!b->display.phase&&b->display_count>0;
    if(kind==BIND_COLORS)return b->colors&&b->colors->have_state&&!!(b->colors->state.flags&Z_SETTINGS_WRITABLE);
    return b->ready&&!!(b->preferences.flags&2)&&binding_choice_count(b,kind)>0;
}
static long mark(struct meridian_bindings *b){return b->dirty?b->dirty(binding_any_changed(b)):0;}
int binding_choose(struct meridian_bindings *b,int kind,unsigned int choice){
    if(kind==BIND_DISPLAY){
        /* A resolution: keep the chosen scale when it offers it, otherwise its first (100%). */
        if(!binding_writable(b,kind))return -13;
        if(choice>=b->resolution_count)return -22;
        unsigned int p[5];int n=scale_pairs(b,choice,p),pick=n?(int)p[0]:-1;
        for(int i=0;i<n;i++)if(b->display_scales[p[i]]==b->display_scales[b->display_choice])pick=(int)p[i];
        if(pick<0)return -22;
        return binding_choose(b,-BIND_DISPLAY,(unsigned int)pick);
    }
    if(kind==-BIND_DISPLAY){   /* one (mode, scale) pair */
        kind=BIND_DISPLAY;
        if(!binding_writable(b,kind))return -13;
        if(choice>=b->display_count)return -22;
        unsigned int old=b->display_choice;b->display_choice=choice;
        if(mark(b)){b->display_choice=old;return -5;}b->display_message=0;return 0;
    }
    int f=field(kind);if(!f)return -22;
    if(!binding_writable(b,kind))return -13;
    if(choice>=(unsigned int)binding_choice_count(b,kind))return -22;
    unsigned int old=b->choice[f];b->choice[f]=choice;
    if(mark(b)){b->choice[f]=old;b->message="Cannot protect edits; selection kept.";return -5;}
    b->message=0;return 0;
}
long binding_apply(struct meridian_bindings *b,int kind){
    if(kind==BIND_DISPLAY){
        if(b->display.phase)return -16;
        if(!binding_changed(b,kind))return 0;
        unsigned int i=b->display_choice;unsigned int p=b->display_scales[i];
        /* 100% and 200% go as 1 and 2, which every kernel accepts. */
        long r=b->display_save?b->display_save(1,b->display_modes[i],p==100?1:p==200?2:p,b->display_revision,0):-38;
        b->display_message=r==-11?"Display changed elsewhere. Reset to reload.":r?"Cannot preview this display mode.":"Preparing preview. Confirm within 15 seconds.";
        display_refresh(b);return r?r:-16; /* navigation waits for Keep/Revert */
    }
    if(kind==BIND_COLORS)return b->colors?settings_apply(b->colors):0;
    int f=field(kind);if(!f||!binding_changed(b,kind))return 0;
    long r=b->save?b->save((unsigned int)f,b->choice[f],b->revision):-38;
    if(r){b->message=r==-11?"Changed elsewhere. Reset to reload.":r==-13?"Administrator permission required.":"Save failed; edits kept.";return r;}
    b->message=f==3?"Default session saved for next boot.":"System default saved and applied.";
    struct zenith_system_settings s;
    if(b->read&&!b->read(&s)&&valid(&s)){b->preferences.revision=s.revision;b->preferences.keyboard=s.keyboard;b->preferences.zone=s.zone;b->preferences.session=s.session;b->preferences.animation=s.animation;b->preferences.taskbar=s.taskbar;b->preferences.wallpaper=s.wallpaper;b->preferences.launcher=s.launcher;b->preferences.cursor=s.cursor;}
    const char *message=b->message;binding_refresh(b);b->message=message;mark(b);return 0;
}
void binding_reset(struct meridian_bindings *b,int kind){
    if(kind==BIND_DISPLAY){
        if(b->display.phase){if(b->display_save)b->display_save(3,0,0,b->display.revision,b->display.token);return;}
        int saved=display_saved(b);b->display_choice=saved<0?0:(unsigned int)saved;b->display_revision=b->display.revision;b->display_message=0;mark(b);return;
    }
    if(kind==BIND_COLORS){if(b->colors)settings_reset(b->colors);return;}
    int f=field(kind);if(!f)return;
    b->choice[f]=saved(&b->preferences,f);b->message=0;binding_refresh(b);mark(b);
}
void binding_defaults(struct meridian_bindings *b,int kind){if(kind==BIND_COLORS&&b->colors)settings_defaults(b->colors);}
