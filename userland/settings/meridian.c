#include "meridian.h"
enum { PENDING_NONE=0,PENDING_CLOSE=1,PENDING_ROW=2,PENDING_PAGE=3 };
enum { ID_ROWS=100,ID_FOOTER=900,ID_DIALOG=950,ID_SEARCH=1002,ID_COMBO=2004,ID_SCALE=2006,ID_COMBO_ROWS=2100,ID_SCALE_ROWS=2400,ID_LINKS=3000,ID_UNLOCK=4003 };
static unsigned long length(const char *s){unsigned long n=0;while(n<4096&&s[n])n++;return n;}
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static void text(struct zui *u,int x,int y,const char *s,const struct zui_font *f,unsigned int c){zui_text(u,x,y,s,length(s),f,c);}
static int sidebar_width(const struct meridian *a){return a->ui.width>=720?240:200;}
static int page_kind(const struct meridian *a){return binding_of(meridian_pages[a->page].id);}
static int page_has_pickers(const struct meridian *a){
    if(same(meridian_pages[a->page].id,"windows.notifications")&&a->styles.notify_ready)return 1;
    unsigned int f[4];int n=styles_page_fields(meridian_pages[a->page].id,f);
    for(int i=0;i<n;i++)if(styles_field_ready(&a->styles,f[i]))return 1;
    return 0;
}
static int page_changed(const struct meridian *a){return binding_changed(&a->bindings,page_kind(a))||styles_changed(&a->styles,meridian_pages[a->page].id);}
static int any_changed(const struct meridian *a){return binding_any_changed(&a->bindings)||styles_any_changed(&a->styles);}
static long page_apply(struct meridian *a){long r=binding_apply(&a->bindings,page_kind(a));if(r)return r;return styles_apply(&a->styles,meridian_pages[a->page].id);}
static void page_reset(struct meridian *a){binding_reset(&a->bindings,page_kind(a));styles_reset(&a->styles,meridian_pages[a->page].id);}
static unsigned int information_kind(const struct meridian *a){
    const char *id=meridian_pages[a->page].id;
    return same(id,"network.connections")?1:same(id,"system.users")?2:0;
}
static int audio_page(const struct meridian *a){
    const char *id=meridian_pages[a->page].id;
    return same(id,"audio.playback")?1:same(id,"audio.recording")?2:same(id,"audio.streams")?3:0;
}
static void refresh_audio(struct meridian *a){
    if(!a->audio_read||!audio_page(a)||a->ui.pressed>=0)return;
    struct zenith_audio state;long r=a->audio_read(&state);
    if(r||state.version!=1||state.device_count>16||state.stream_count>16){if(!a->audio_error)a->ui.dirty=1;a->audio_error=1;return;}
    const unsigned char *old=(const unsigned char*)&a->audio,*next=(const unsigned char*)&state;
    for(unsigned long i=0;i<sizeof(state);i++)if(old[i]!=next[i]){a->ui.dirty=1;break;}
    a->audio=state;a->audio_error=0;
}
void meridian_audio_services(struct meridian *a,long (*read)(struct zenith_audio *),long (*control)(const struct zenith_audio_control *)){
    a->audio_read=read;a->audio_control=control;refresh_audio(a);
}
/* ---------- Network (Connections page): status and editing through call 667. */
static int network_page(const struct meridian *a){return a->network&&information_kind(a)==1;}
static char *net_put(char *o,const char *s){while(*s)*o++=*s++;*o=0;return o;}
static char *net_num(char *o,unsigned long v){char t[24];int n=0;do{t[n++]=(char)('0'+v%10);v/=10;}while(v);while(n)*o++=t[--n];*o=0;return o;}
static char *net_ip(char *o,const unsigned char *a){for(int i=0;i<4;i++){o=net_num(o,a[i]);if(i<3)*o++='.';}*o=0;return o;}
static int net_zero(const unsigned char *a){return !a[0]&&!a[1]&&!a[2]&&!a[3];}
static int net_prefix(const unsigned char *m){int n=0;for(int i=0;i<4;i++)for(int b=7;b>=0;b--){if(m[i]>>b&1)n++;else return n;}return n;}
static void net_fields_from_status(struct meridian *a){
    const struct zenith_network_status *s=&a->net;int manual=s->mode==1;
    const unsigned char *ad=manual?s->saved_address:s->address,*gw=manual?s->saved_gateway:s->gateway,*dn=manual?s->saved_dns:s->dns[0],*mk=manual?s->saved_mask:s->mask;
    for(int i=0;i<4;i++)a->net_text[i][0]=0;
    if(!net_zero(ad))net_ip(a->net_text[0],ad);
    int p=net_prefix(mk);net_num(a->net_text[1],(unsigned long)(p?p:24));
    if(!net_zero(gw))net_ip(a->net_text[2],gw);
    if(!net_zero(dn))net_ip(a->net_text[3],dn);
    for(int i=0;i<4;i++){zui_input_init(&a->net_inputs[i],a->net_text[i],sizeof(a->net_text[i]));unsigned int n=0;while(a->net_text[i][n])n++;a->net_inputs[i].length=a->net_inputs[i].cursor=n;}
    a->net_mode=(int)s->mode;a->net_edited=0;
}
static void refresh_network(struct meridian *a){
    if(!network_page(a)||a->net_busy)return;
    struct zenith_network_status s;long r=a->network(Z_NET_STATUS,0,&s,sizeof(s),0,0);
    int ok=r==(long)sizeof(s)&&s.version==1;
    if(!ok){if(a->net_ok)a->ui.dirty=1;a->net_ok=0;return;}
    int first=!a->net_ok;
    const unsigned char *x=(const unsigned char *)&a->net,*y=(const unsigned char *)&s;
    for(unsigned long i=0;i<sizeof(s);i++)if(x[i]!=y[i]){a->ui.dirty=1;break;}
    a->net=s;a->net_ok=1;
    if(first||!a->net_edited)net_fields_from_status(a);
}
void meridian_network_services(struct meridian *a,long (*call)(unsigned int,unsigned int,void *,unsigned int,const void *,unsigned int)){
    a->network=call;a->net_field=-1;refresh_network(a);
}
static void refresh_information(struct meridian *a){
    unsigned int kind=information_kind(a);if(!kind||!a->information_read)return;
    struct zenith_settings_information state;long r=a->information_read(kind,&state);
    if(r||state.version!=1||state.kind!=kind||state.length>=sizeof(state.text)||state.truncated>1){a->public_error=1;return;}
    int changed=a->public_kind!=kind||a->public_error;
    for(unsigned int i=0;i<state.length;i++){if(a->public_information[i]!=state.text[i])changed=1;a->public_information[i]=state.text[i];}
    if(a->public_information[state.length])changed=1;
    a->public_information[state.length]=0;a->public_kind=kind;a->public_error=0;if(changed)a->ui.dirty=1;
}
/* The draft palette follows the colors model; repaint only when it really changed. */
static void sync_theme(struct meridian *a){
    struct zui *u=&a->ui;struct zui_theme theme=u->theme;unsigned int swatches[6];int background=u->full_background,preview=u->theme_preview,dirty=u->dirty;
    for(int i=0;i<6;i++)swatches[i]=u->swatches[i];
    settings_theme(&a->colors,u);
    int same_theme=background==u->full_background&&preview==u->theme_preview;
    const unsigned int *x=(const unsigned int *)&theme,*y=(const unsigned int *)&u->theme;
    for(unsigned int i=0;i<sizeof(theme)/sizeof(unsigned int);i++)if(x[i]!=y[i])same_theme=0;
    for(int i=0;i<6;i++)if(swatches[i]!=u->swatches[i])same_theme=0;
    u->dirty=dirty||!same_theme||a->colors.ui.dirty;a->colors.ui.dirty=0;
}
void meridian_information_services(struct meridian *a,long (*read)(unsigned int,struct zenith_settings_information *)){a->information_read=read;refresh_information(a);}
void meridian_system_services(struct meridian *a,long (*read)(struct zenith_system_settings *),long (*save)(unsigned int,unsigned int,unsigned long)){
    a->bindings.read=read;a->bindings.save=save;binding_refresh(&a->bindings);
}
/* The style catalogue and previews (call 669): the wallpaper page becomes a picker when the kernel has it. */
void meridian_style_services(struct meridian *a,long (*catalog)(unsigned int,struct zenith_style_list *),
                             long (*preview)(unsigned int,unsigned int,int,int,unsigned int *)){
    a->styles.catalog=catalog;a->styles.preview=preview;a->styles.save=a->bindings.save;a->styles.dirty=a->bindings.dirty;
    if(!catalog){for(int f=0;f<STYLE_FIELDS;f++)a->styles.ready[f]=0;a->styles.sounds=0;a->styles.sound_ready=0;return;}
    styles_refresh(&a->styles);
}
void meridian_notify_services(struct meridian *a,long (*read)(struct zenith_notify_settings *),long (*save)(unsigned int,unsigned int,unsigned long),long (*post)(const char *,const char *,const char *)){
    a->styles.notify_read=read;a->styles.notify_save=save;a->styles.notify_post=post;if(!read)a->styles.notify_ready=0;styles_refresh(&a->styles);
}
void meridian_sound_services(struct meridian *a,long (*read)(struct zenith_sound_settings *),long (*save)(unsigned int,unsigned int,unsigned long),long (*play)(unsigned int)){
    a->styles.sounds=read;a->styles.sound_save=save;a->styles.sound_play=play;styles_refresh(&a->styles);
}
struct zui *meridian_ui(struct meridian *a){return &a->ui;}
static void remember(struct meridian *a,int page){
    int i=0;while(i<a->recent_count&&a->recent[i]!=page)i++;
    if(i==a->recent_count&&i<6)a->recent_count++;
    if(i>5)i=5;
    for(;i>0;i--)a->recent[i]=a->recent[i-1];
    a->recent[0]=page;
}
/* Adopt a navigation state; a new page starts at its top with fresh transient state. */
static void adopt(struct meridian *a,const struct meridian_nav *next){
    int changed=next->page!=a->page;
    if(next->mode!=a->nav.mode||next->category!=a->nav.category)a->fade_request|=FADE_SIDEBAR;
    if(changed)a->fade_request|=FADE_PAGE;
    a->nav=*next;a->page=next->page;
    if(a->nav.mode!=NAV_SEARCH&&a->query[0]){a->query[0]=0;zui_input_init(&a->search,a->query,sizeof(a->query));}
    if(changed){
        zui_scroll_jump(&a->page_scroll,0);a->combo.open=a->scale_combo.open=0;a->ui.focus=a->ui.pressed=-1;
        remember(a,a->page);refresh_information(a);refresh_audio(a);a->net_ok=0;a->net_message[0]=0;a->net_field=-1;refresh_network(a);
    }
    a->ui.dirty=1;
}
static void clear_search(struct meridian *a){
    a->query[0]=0;zui_input_init(&a->search,a->query,sizeof(a->query));nav_search(&a->nav,"");
    a->search_focus=0;zui_scroll_jump(&a->sidebar.scroll,0);a->ui.dirty=1;
}
int meridian_open(struct meridian *a,const char *id){
    if(any_changed(a))return 0;
    struct meridian_nav next=a->nav;if(!nav_open(&next,id))return 0;
    adopt(a,&next);zui_scroll_jump(&a->sidebar.scroll,0);return 1;
}
int meridian_resize(struct meridian *a,int w,int h,unsigned long capacity){
    if(w<600||h<480||!zui_resize(&a->ui,w,h,capacity))return 0;
    a->combo.open=a->scale_combo.open=0;a->fade_request=a->fade_ready=0;return 1;
}
int meridian_init(struct meridian *a,unsigned int *pixels,struct settings_backend backend,const char *page){
    *a=(struct meridian){0};
    if(!settings_init(&a->colors,pixels,backend)||!zui_init(&a->ui,pixels,600,480)||!zui_input_init(&a->search,a->query,sizeof(a->query)))return 0;
    binding_init(&a->bindings,&a->colors,0,0,backend.dirty);
    a->focus_row=-1;nav_init(&a->nav,0);a->sidebar.base_id=ID_ROWS;a->combo.id=ID_COMBO;a->combo.popup.base_id=ID_COMBO_ROWS;a->scale_combo.id=ID_SCALE;a->scale_combo.popup.base_id=ID_SCALE_ROWS;
    remember(a,0);
    if(page&&!meridian_open(a,page))return 0;
    sync_theme(a);return 1;
}
static void copy_note(char *o,const char *s,unsigned long n){unsigned long i=0;for(;i+1<n&&s[i];i++)o[i]=s[i];o[i]=0;}
/* Asks the desktop for permission to change system settings; the answer arrives in meridian_refresh. */
static void unlock(struct meridian *a){
    if(!a->authorize||a->unlock_handle>0)return;
    long h=a->authorize();
    if(h>0){a->unlock_handle=h;a->unlock_note[0]=0;}
    else copy_note(a->unlock_note,h==-38?"This Zenith kernel cannot ask for permission. Run Meridian as root.":"The request was refused.",sizeof(a->unlock_note));
    a->ui.dirty=1;
}
static void poll_unlock(struct meridian *a){
    if(a->unlock_handle<=0||!a->authorization_poll)return;
    char message[96];message[0]=0;
    long state=a->authorization_poll(a->unlock_handle,message,sizeof(message));
    if(state>=0&&state<3)return;   /* pending or shown */
    a->unlock_handle=0;a->ui.dirty=1;
    if(state==3)a->unlock_note[0]=0;
    else if(state==7)copy_note(a->unlock_note,"No desktop can ask for permission here. Use sudo in a terminal.",sizeof(a->unlock_note));
    else if(state==4){char t[100]="Not allowed: ";copy_note(t+13,message,sizeof(t)-13);copy_note(a->unlock_note,t,sizeof(a->unlock_note));}
    else copy_note(a->unlock_note,"The request ended without an answer.",sizeof(a->unlock_note));
}
/* Under a read-only system setting: why, and the Unlock button (when the portal is there). */
static void unlock_row(struct meridian *a,struct zui *u,struct zui_form *f){
    if(!a->authorize)return;
    const char *note=a->unlock_note[0]?a->unlock_note:a->unlock_handle>0?"Waiting for permission...":"Changing this needs an administrator.";
    zui_add(u,ID_UNLOCK,ZUI_BUTTON,(struct zui_rect){f->x,f->y,120,34},a->unlock_handle>0?"Waiting...":"Unlock",0,a->unlock_handle<=0);
    zui_paragraph(u,(struct zui_rect){f->x+132,f->y+8,f->w-132,24},note,&zui_caption,u->theme.secondary,0);
    f->y+=46;
}
void meridian_refresh(struct meridian *a){
    poll_unlock(a);refresh_audio(a);refresh_network(a);
    unsigned long rev=a->colors.state.revision,res=a->colors.state.resources,pref=a->bindings.preferences.revision;unsigned int flags=a->colors.state.flags;
    int ready=a->bindings.ready;
    unsigned long display_revision=a->bindings.display.revision;unsigned int seconds=a->bindings.display.seconds;
    refresh_information(a);binding_refresh(&a->bindings);styles_refresh(&a->styles);settings_refresh(&a->colors);sync_theme(a);
    if(display_revision!=a->bindings.display.revision||seconds!=a->bindings.display.seconds)a->ui.dirty=1;
    if(rev!=a->colors.state.revision||res!=a->colors.state.resources||flags!=a->colors.state.flags||pref!=a->bindings.preferences.revision||ready!=a->bindings.ready)a->ui.dirty=1;
}
static int sidebar_rows(void *c,int i,struct zui_list_item *o){return nav_rows(&((struct meridian *)c)->nav,i,o);}
static int choice_rows(void *c,int i,struct zui_list_item *o){
    struct meridian *a=c;int kind=page_kind(a);
    if(i<0||i>=binding_choice_count(&a->bindings,kind))return 0;
    *o=(struct zui_list_item){0,binding_choice_name(&a->bindings,kind,(unsigned int)i),0,0,1};return 1;
}
static int scale_rows(void *c,int i,struct zui_list_item *o){
    struct meridian *a=c;
    if(i<0||i>=binding_scale_count(&a->bindings))return 0;
    *o=(struct zui_list_item){0,binding_scale_name(&a->bindings,(unsigned int)i),0,0,1};return 1;
}
static struct zui_rect search_rect(struct zui_rect s){return (struct zui_rect){s.x+12,s.y+8,s.w-24,34};}
static void draw_sidebar(struct meridian *a,struct zui *u,struct zui_rect s){
    zui_add(u,ID_SEARCH,ZUI_BUTTON,search_rect(s),"",0,1);
    a->sidebar.selected=a->nav.category;
    if(a->nav.mode!=NAV_CATEGORIES)for(int i=1;i<nav_row_count(&a->nav);i++)if(nav_row_page(&a->nav,i)==a->page)a->sidebar.selected=i;
    zui_list_draw(u,&a->sidebar,(struct zui_rect){s.x,s.y+52,s.w,s.h-52},nav_row_count(&a->nav),sidebar_rows,a);
}
static void picker(struct meridian *a,struct zui *u,struct zui_form *f,unsigned int field,const char *title,const char *help,int writable);
static int system_writable(const struct meridian *a);
static int style_page(const struct meridian *a){return same(meridian_pages[a->page].id,"style.application");}
static const char *wip_note(const struct meridian *a,int kind){
    if(audio_page(a))return "Live audio controls. Stereo PCM, 48 kHz / 16 bit. Input starts only for an authorized recording app. Work in progress: remembered choices, additional formats, independent routing and hot-plug devices.";
    if(kind==BIND_DISPLAY)return "Work in progress: orientation, refresh, brightness and multiple displays.";
    if(kind==BIND_COLORS&&style_page(a))return "Widget style and buttons apply to every app with the next frame.";
    if(kind==BIND_COLORS)return "Work in progress: automatic switching and custom colors.";
    if(kind==BIND_KEYBOARD)return "Work in progress: model, repeat, Num Lock and shortcuts.";
    if(kind==BIND_TIMEZONE)return "Work in progress: clock editing and sync policy.";
    if(kind==BIND_SESSION)return "Work in progress: session restore and confirmation policy.";
    if(kind==BIND_TASKBAR)return "F11 makes the focused window fullscreen; the bar ducks and returns at the bottom screen edge. Work in progress: position and pinned applications.";
    if(kind==BIND_ANIMATIONS)return "Work in progress: per-effect controls, peek and desktop transitions.";
    if(kind==BIND_WALLPAPER)return "Dynamic wallpapers follow light/dark mode and the accent color at once. Work in progress: your own images and scheduling.";
    if(kind==BIND_CURSOR)return "Every pack has the same shapes for text, links, window edges and busy apps. Contrast is larger and pure black and white; Comet follows your accent.";
    if(kind==BIND_LAUNCHER)return "Prism: Meta or Alt+Space always opens the search palette. Work in progress: file indexing, more providers, pinned applications and a custom shortcut.";
    {
        const char *id=meridian_pages[a->page].id;
        if(page_has_pickers(a)&&same(id,"style.decorations"))return "The desktop draws the title bar of every window; apps cannot draw their own.";
        if(page_has_pickers(a)&&same(id,"style.splash"))return "Plays once when you sign in. Animations Off or a slow machine skips it; a failed start shows its error instead.";
        if(page_has_pickers(a)&&same(id,"style.login"))return "Accounts, Clock and Split preselect the last account that signed in. Who may skip the login is in Security.";
        if(page_has_pickers(a)&&same(id,"style.boot-splash"))return "From the second boot phase on. Diagnostics and the rescue entry stay reachable in every style.";
        if(page_has_pickers(a)&&same(id,"audio.sounds"))return "Zenith Chimes plays short, quiet sounds for signing in and out, questions, errors, finished changes, notifications and the volume. Play previews a sound even when its event is off.";
        if(page_has_pickers(a)&&same(id,"windows.notifications"))return "Popups appear above the taskbar at the right; a click dismisses one. The sender is always the app's real name. Work in progress: a history panel and per-app choices.";
    }
    if(a->page==0)return "Work in progress: scheduled mode and persistent history.";
    if(a->page==MERIDIAN_ABOUT)return "Work in progress: hardware inventory is planned.";
    if(network_page(a))return "The wired connection, live. Beacon shows its traffic history and diagnostics. Work in progress: several profiles, priorities and link negotiation; Wi-Fi and VPN need drivers and services Zenith does not have yet.";
    if(information_kind(a)==1)return "This kernel reports the network as text only. Zenith 0.0.69 adds editing here and in Beacon.";
    if(information_kind(a)==2)return "Work in progress: account creation, deletion and passwords.";
    return "Work in progress: this page needs additional system services. Nothing here can be changed yet.";
}
static void note(struct zui *u,struct zui_form *f,const char *s){
    int inner=f->w-24,lines=inner>0?zui_measure(s,length(s),&zui_caption)/inner+1:1,h=lines*19+16;
    struct zui_rect r={f->x,f->y,f->w,h};
    zui_surface(u,r,8,ZUI_SURFACE_CONTROL);
    zui_paragraph(u,(struct zui_rect){r.x+12,r.y+8,inner,h-12},s,&zui_caption,u->theme.secondary,0);
    f->y+=h+12;
}
static void status(struct zui *u,struct zui_form *f,const char *s){
    if(!s)return;
    zui_paragraph(u,(struct zui_rect){f->x,f->y,f->w,36},s,&zui_caption,u->theme.text,0);f->y+=40;
}
/* Colors (mode and accent) and Application Style (the materials) share one appearance record and one footer. */
static void colors_form(struct meridian *a,struct zui *u,struct zui_form *f){
    struct settings_app *s=&a->colors;int writable=binding_writable(&a->bindings,BIND_COLORS),cw=f->narrow?f->w:f->w-f->label_w-16;
    struct zui_rect r;
    if(style_page(a))goto materials;
    zui_form_section(u,f,"Appearance");
    r=zui_form_row(u,f,"Color mode",0,272,32);
    zui_segment_track(u,(struct zui_rect){r.x-4,r.y-4,r.w+8,r.h+8});
    zui_add(u,1,ZUI_SEGMENT,(struct zui_rect){r.x,r.y,r.w/2,32},"Dark",s->mode==0,writable);
    zui_add(u,2,ZUI_SEGMENT,(struct zui_rect){r.x+r.w/2,r.y,r.w/2,32},"Light",s->mode==1,writable);
    r=zui_form_row(u,f,"Accent color",0,264,54);
    static const char *const names[6]={"Blue","Violet","Rose","Amber","Green","Teal"};
    for(int i=0;i<6;i++)zui_add_swatch(u,10+i,(struct zui_rect){r.x+i*44,r.y,32,32},names[i],i,s->accent==(unsigned int)i,writable);
    zui_swatch_caption(u,r.x,r.y+38);
    goto scope;
materials:
    zui_form_section(u,f,"Materials");
    static const char *const effect_names[3]={"Transparency","Backdrop blur","Whole app"};
    const char *help[3]={"Let panels reveal the background.",
        !s->available_effects?"Unavailable here; saved choices are kept.":!(s->effects&1)&&(s->effects&2)?"Saved; enable transparency to activate it.":s->material_reason>=3?"Some surfaces fall back for contrast or speed.":"Diffuse the backdrop; controls remain clear.",
        !(s->effects&1)&&(s->effects&4)?"Saved; enable transparency to activate it.":"Extend effects to the app canvas."};
    for(int i=0;i<3;i++){
        r=zui_form_row(u,f,effect_names[i],help[i],cw<300?cw:300,28);
        zui_add(u,50+i,ZUI_TOGGLE,r,"",!!(s->effects&(1U<<i)),writable&&!!(s->available_effects&(1U<<i)));
    }
    r=zui_form_row(u,f,"Strength",0,152,32);
    int level_enabled=writable&&!!s->available_effects;
    zui_add(u,53,ZUI_BUTTON,(struct zui_rect){r.x,r.y,32,32},"-",0,level_enabled);
    zui_add(u,54,ZUI_BUTTON,(struct zui_rect){r.x+r.w-32,r.y,32,32},"+",0,level_enabled);
    char percent[5];unsigned int n=s->translucency>100?100:s->translucency;int k=0;
    if(n>=100)percent[k++]='1';
    if(n>=10)percent[k++]=(char)('0'+n/10%10);
    percent[k++]=(char)('0'+n%10);percent[k++]='%';percent[k]=0;
    text(u,r.x+60,r.y+8,percent,&zui_label,u->theme.text);
scope:
    if(style_page(a)){unsigned int fl[4];int n=styles_page_fields("style.application",fl);for(int k=0;k<n;k++)if(styles_field_ready(&a->styles,fl[k]))
        picker(a,u,f,fl[k],fl[k]==11?"Widget style":"Buttons",fl[k]==11?"Corners and fills of controls in every app.":"How primary buttons are drawn.",system_writable(a));}
    if(s->has_scopes){
        zui_form_section(u,f,"Scope");
        r=zui_form_row(u,f,"Applies to",0,272,32);
        zui_segment_track(u,(struct zui_rect){r.x-4,r.y-4,r.w+8,r.h+8});
        zui_add(u,60,ZUI_SEGMENT,(struct zui_rect){r.x,r.y,r.w/2,32},"Personal",s->scope==1,!s->edited);
        zui_add(u,61,ZUI_SEGMENT,(struct zui_rect){r.x+r.w/2,r.y,r.w/2,32},"System",s->scope==0,!s->edited);
    }
    const char *where=s->has_scopes&&s->scope?(s->inherited?"Personal: following system defaults.":"Personal override for this account."):
        !(s->state.flags&Z_SETTINGS_WRITABLE)?"Read only: sign in as an administrator to save.":
        s->state.flags&Z_SETTINGS_PERSISTENT?"Installed system: applied changes survive reboot.":"Live system: changes will be lost at reboot.";
    const char *state=s->edited?"Unapplied changes - showing your preview.":s->state.flags&Z_SETTINGS_PENDING?"Applied. Waiting for the desktop or next login.":"Applied appearance is active.";
    if(s->has_scopes&&!s->scope&&!s->inherited)state="System defaults applied; personal overrides are active.";
    f->y+=8;
    text(u,f->x,f->y,where,&zui_caption,u->theme.secondary);f->y+=20;
    text(u,f->x,f->y,s->message?s->message:state,&zui_caption,u->theme.text);f->y+=20;
    if(!(s->has_scopes&&s->scope)&&!(s->state.flags&Z_SETTINGS_WRITABLE)){f->y+=8;unlock_row(a,u,f);}
}
static void preference_form(struct meridian *a,struct zui *u,struct zui_form *f,int kind){
    const struct meridian_bindings *b=&a->bindings;int writable=binding_writable(b,kind),count=binding_choice_count(b,kind);
    zui_form_section(u,f,"System default");
    struct zui_rect r=zui_form_row(u,f,kind==BIND_KEYBOARD?"Keyboard layout":kind==BIND_TIMEZONE?"Time zone":kind==BIND_ANIMATIONS?"Window animations":kind==BIND_TASKBAR?"Taskbar":kind==BIND_WALLPAPER?"Wallpaper":kind==BIND_LAUNCHER?"Taskbar button opens":kind==BIND_CURSOR?"Cursor set":"Login session",
        writable?"Administrators change this for all users.":"Read only: sign in as an administrator.",280,36);
    if(count){
        if(!a->combo.open)a->combo.value=(int)binding_choice(b,kind);
        zui_combo_draw(u,&a->combo,r,count,choice_rows,a);
        int field=-1;for(int i=0;i<u->count;i++)if(u->controls[i].id==ID_COMBO)field=i;
        if(field>=0){
            u->controls[field].enabled=writable;
            if(binding_choice(b,kind)>=(unsigned int)count)u->controls[field].label=kind==BIND_TIMEZONE?b->preferences.current_zone:b->preferences.current_session;
        }
    }else text(u,r.x,r.y+10,"Unavailable from the settings service.",&zui_body,u->theme.secondary);
    if(!writable&&count)unlock_row(a,u,f);
    status(u,f,b->message?b->message:binding_changed(b,kind)?"Unapplied change.":kind==BIND_KEYBOARD?"Applies to keyboard input across sessions immediately.":
        kind==BIND_TIMEZONE?"Changes local time display; the UTC clock is unchanged.":kind==BIND_ANIMATIONS?"Applies to the next window change. Off keeps every change immediate.":kind==BIND_TASKBAR?"Applies immediately. Dock when maximized removes the gap around the bar for a maximized window.":
        kind==BIND_WALLPAPER?"Applies immediately, here and on the login screen.":kind==BIND_LAUNCHER||kind==BIND_CURSOR?"Applies immediately.":"Applies on next boot; the running session is unchanged.");
}
/* Pickers (call 669): previews drawn by the desktop, one tile per choice, the draft outlined with a check. */
enum { ID_PICKS=8000 };   /* ID_PICKS + field * 64 + index */
static unsigned int appearance_key(const struct meridian *a){return (a->colors.state.mode&1)<<8|(a->colors.state.accent&255);}
static void tile_size(unsigned int field,int *w,int *h){
    switch(field){
        case 6:*w=120;*h=75;break;
        case 8:*w=176;*h=64;break;
        case 9:*w=96;*h=72;break;
        case 11:case 12:*w=160;*h=96;break;
        case 16:*w=0;*h=0;break;
        default:*w=176;*h=110;break;
    }
}
static void picker(struct meridian *a,struct zui *u,struct zui_form *f,unsigned int field,const char *title,const char *help,int writable){
    struct meridian_styles *st=&a->styles;
    const struct zenith_style_list *l=&st->list[field];
    int pw,ph;tile_size(field,&pw,&ph);
    int tile_w=pw?pw+12:180,tile_h=pw?ph+38:56,gap=12,cols=(f->w+gap)/(tile_w+gap);if(cols<1)cols=1;
    unsigned int groups=field==6?2:1;
    for(unsigned int group=0;group<groups;group++){
        zui_form_section(u,f,field==6?(group?"Solid colors":"Dynamic"):title);
        const char *line=field==6?(group?"A plain fill; Accent color follows your accent.":"Drawn for light or dark mode and your accent color."):help;
        if(line&&line[0]){text(u,f->x,f->y,line,&zui_caption,u->theme.secondary);f->y+=26;}
        int n=0;
        for(unsigned int i=0;i<l->count;i++){
            const struct zenith_style_entry *e=&l->entries[i];
            if(field==6&&(e->kind?1u:0u)!=group)continue;
            struct zui_rect r={f->x+(n%cols)*(tile_w+gap),f->y+(n/cols)*(tile_h+gap),tile_w,tile_h};
            if(pw){
                struct zui_rect pic={r.x+6,r.y+6,pw,ph};
                if(field==6&&e->kind)zui_round(u,pic,6,e->color&0xFFFFFF);
                else{
                    /* Only previews in view are drawn, so a long list costs nothing until it scrolls in. */
                    struct zui_rect view=a->page_scroll.rect;
                    const unsigned int *t=pic.y+ph>view.y&&pic.y<view.y+view.h?styles_thumb(st,field,i,pw,ph,appearance_key(a)):0;
                    if(t)zui_image(u,pic.x,pic.y,pw,ph,t);else zui_round(u,pic,6,u->theme.subtle);
                }
                text(u,r.x+8,r.y+ph+12,e->label,&zui_caption,u->theme.text);
            }else text(u,r.x+14,r.y+18,e->label,&zui_label,u->theme.text);
            zui_add(u,ID_PICKS+(int)field*64+(int)i,ZUI_TILE,r,e->label,i==st->draft[field],writable);
            n++;
        }
        f->y+=((n+cols-1)/cols)*(tile_h+gap)+8;
    }
}
/* System Sounds: the level and a switch and a Play button per event (ids 9001/9002, 9100+i, 9200+i). */
static void sound_events_form(struct meridian *a,struct zui *u,struct zui_form *f,int writable){
    struct meridian_styles *st=&a->styles;
    zui_form_section(u,f,"Level");
    struct zui_rect r=zui_form_row(u,f,"Volume of system sounds","Scaled with the output volume; 0 % plays nothing.",152,32);
    zui_add(u,9001,ZUI_BUTTON,(struct zui_rect){r.x,r.y,32,32},"-",0,writable&&st->sound_level>0);
    zui_add(u,9002,ZUI_BUTTON,(struct zui_rect){r.x+r.w-32,r.y,32,32},"+",0,writable&&st->sound_level<100);
    char t[8];int k=0;unsigned int v=st->sound_level;if(v>=100)t[k++]='1';if(v>=10)t[k++]=(char)('0'+v/10%10);t[k++]=(char)('0'+v%10);t[k++]='%';t[k]=0;
    text(u,r.x+52,r.y+8,t,&zui_label,u->theme.text);
    zui_form_section(u,f,"Events");
    for(unsigned int i=0;i<st->sound.count&&i<15;i++){
        st->sound.labels[i][31]=0;
        r=zui_form_row(u,f,st->sound.labels[i],0,f->narrow?f->w:240,32);
        zui_add(u,(int)(9200+i),ZUI_BUTTON,(struct zui_rect){r.x,r.y,64,32},"Play",0,st->sound_play!=0);
        zui_add(u,(int)(9100+i),ZUI_TOGGLE,(struct zui_rect){r.x+76,r.y,r.w-76,32},"",!!(st->sound_mask&(1u<<i)),writable);
    }
}
/* Notifications: Do Not Disturb, the popup time and a test post (ids 9300, 9310+i, 9320). */
static void notify_form(struct meridian *a,struct zui *u,struct zui_form *f,int writable){
    struct meridian_styles *st=&a->styles;
    zui_form_section(u,f,"Do Not Disturb");
    struct zui_rect r=zui_form_row(u,f,"Do Not Disturb","Notifications go to the history without a popup or a sound.",f->narrow?f->w:240,32);
    zui_add(u,9300,ZUI_TOGGLE,r,"",st->notify_dnd!=0,writable);
    zui_form_section(u,f,"Popups");
    static const char *names[3]={"Short","Normal","Long"};
    r=zui_form_row(u,f,"Shown for","4, 6 or 10 seconds; the pointer over a popup holds it.",f->narrow?f->w:300,32);
    int w=(r.w-16)/3;
    for(unsigned int i=0;i<3&&i<st->notify.choices;i++)zui_add(u,(int)(9310+i),ZUI_SEGMENT,(struct zui_rect){r.x+(int)i*(w+8),r.y,w,32},names[i],st->notify_popup==i,writable);
    r=zui_form_row(u,f,"Try it","Sends a notification from Meridian.",f->narrow?f->w:180,32);
    zui_add(u,9320,ZUI_BUTTON,(struct zui_rect){r.x,r.y,r.w,32},"Send a test",0,st->notify_post!=0);
}
static int system_writable(const struct meridian *a){return a->bindings.ready&&!!(a->bindings.preferences.flags&2);}
/* Every picker of the page; true when it drew any. */
static int pickers_form(struct meridian *a,struct zui *u,struct zui_form *f){
    unsigned int fields[4];int n=styles_page_fields(meridian_pages[a->page].id,fields),drawn=0,writable=system_writable(a);
    if(same(meridian_pages[a->page].id,"windows.notifications")&&a->styles.notify_ready){notify_form(a,u,f,writable);drawn=1;}
    for(int k=0;k<n;k++){
        unsigned int fl=fields[k];if(!styles_field_ready(&a->styles,fl))continue;
        static const char *titles[STYLE_FIELDS]={0,0,0,0,0,0,"Wallpaper",0,"Cursor pack","Size","Window decorations","Widget style","Buttons","Session splash","Login screen","Boot animation","Sound pack","Icon set"};
        static const char *helps[STYLE_FIELDS]={0,0,0,0,0,0,0,0,"The same shapes everywhere; login and recovery keep Meridian Light.","How large the pointer is drawn, at any display scale.",
            "The title bar the desktop draws for every window.","Corners and fills of controls in every app.","How primary buttons are drawn.",
            "The transition from signing in to the desktop. A press or key ends it.","The screen you sign in on.","Before the login screen; diagnostics stay reachable.","Sounds for system events.","The same drawings at three stroke weights; an icon a set lacks comes from Zenith."};
        picker(a,u,f,fl,titles[fl],helps[fl],writable);drawn=1;
    }
    if(drawn&&same(meridian_pages[a->page].id,"audio.sounds")&&a->styles.sound_ready)sound_events_form(a,u,f,writable);
    if(drawn){
        if(!writable)unlock_row(a,u,f);
        status(u,f,a->styles.message?a->styles.message:styles_changed(&a->styles,meridian_pages[a->page].id)?"Unapplied change: Apply to use it.":"Applies immediately.");
    }
    return drawn;
}
static void display_form(struct meridian *a,struct zui *u,struct zui_form *f){
    struct meridian_bindings *b=&a->bindings;
    zui_form_section(u,f,"Primary display");
    if(!b->display_ready){status(u,f,"Display service unavailable.");return;}
    status(u,f,b->display.driver);
    int writable=binding_writable(b,BIND_DISPLAY);
    struct zui_rect r=zui_form_row(u,f,"Resolution","The size of the picture the display shows.",280,36);
    if(!a->combo.open)a->combo.value=(int)binding_choice(b,BIND_DISPLAY);
    zui_combo_draw(u,&a->combo,r,binding_choice_count(b,BIND_DISPLAY),choice_rows,a);
    r=zui_form_row(u,f,"Scale","Makes apps, window controls and the panel larger.",280,36);
    if(!a->scale_combo.open)a->scale_combo.value=(int)binding_scale_choice(b);
    zui_combo_draw(u,&a->scale_combo,r,binding_scale_count(b),scale_rows,a);
    for(int i=0;i<u->count;i++)if(u->controls[i].id==ID_COMBO||u->controls[i].id==ID_SCALE)u->controls[i].enabled=writable;
    if(!writable)unlock_row(a,u,f);
    if(b->display.phase && (b->display.flags&4)){
        status(u,f,b->display.phase==1?"Preparing display preview...":"Keep this display? Reverts automatically after 15 seconds.");
        char timer[40]="Seconds remaining: ";char *t=timer+19;unsigned int v=b->display.seconds;if(v>=10)*t++='1';*t++=(char)('0'+v%10);*t=0;status(u,f,timer);
        zui_add(u,4001,ZUI_BUTTON,(struct zui_rect){f->x,f->y,120,36},"Keep",0,b->display.phase==2);
        zui_add(u,4002,ZUI_BUTTON,(struct zui_rect){f->x+132,f->y,120,36},"Revert",0,b->display.phase==2);f->y+=48;
    }
    status(u,f,b->display_message?b->display_message:"Apply previews the mode. Only Keep saves it for the next boot.");
    status(u,f,(b->display.flags&2)?"Administrator display settings for all sessions.":"Read only, inactive desktop or unsupported configuration.");
    if(b->display.mode_count==1)status(u,f,"This driver has no runtime resolution switching.");
    status(u,f,"125-175% are filtered for now; sharp scaling, orientation, refresh rate, multiple monitors and brightness are WIP.");
}
static void lines_form(struct zui *u,struct zui_form *f,const char *s){
    while(*s){
        unsigned long n=0;while(s[n]&&s[n]!='\n'&&n<4096)n++;
        if(n)zui_text(u,f->x,f->y,s,n,&zui_body,u->theme.secondary);
        f->y+=22;s+=n;if(*s)s++;
    }
}
static void net_row(struct zui *u,struct zui_form *f,const char *label,const char *value){
    text(u,f->x,f->y+2,label,&zui_caption,u->theme.secondary);
    text(u,f->x+(f->narrow?110:f->label_w),f->y,value,&zui_body,u->theme.text);f->y+=28;
}
static void network_form(struct meridian *a,struct zui *u,struct zui_form *f){
    if(!a->net_ok){status(u,f,"The network service did not answer.");return;}
    const struct zenith_network_status *s=&a->net;char t[160],*o;
    zui_form_section(u,f,"Connection");
    const char *words=s->state==Z_NET_ONLINE?"Connected":s->state==Z_NET_NO_ADAPTER?"No network adapter":s->state==Z_NET_CABLE_OUT?"The cable is out":
        s->state==Z_NET_CONNECTING?"Connecting...":s->state==Z_NET_OFF?(s->mode==2?"Turned off":"Disconnected"):"Not connected";
    o=net_put(t,words);if(s->state==Z_NET_ONLINE){o=net_put(o," \xc2\xb7 wired \xc2\xb7 ");net_put(o,s->mode==1?"manual":"DHCP");}
    net_row(u,f,"State",t);
    o=net_ip(t,s->address);if(!net_zero(s->mask)){o=net_put(o," / ");net_num(o,(unsigned long)net_prefix(s->mask));}
    net_row(u,f,"Address",net_zero(s->address)?"none":t);
    net_ip(t,s->gateway);net_row(u,f,"Gateway",net_zero(s->gateway)?"none":t);
    o=t;*o=0;for(unsigned int i=0;i<s->dns_count&&i<4;i++){if(i)o=net_put(o,", ");o=net_ip(o,s->dns[i]);}
    net_row(u,f,"DNS servers",s->dns_count?t:"none");
    o=t;for(int i=0;i<6;i++){const char *hex="0123456789abcdef";*o++=hex[s->mac[i]>>4];*o++=hex[s->mac[i]&15];if(i<5)*o++=':';}*o=0;
    net_row(u,f,"Hardware",s->state==Z_NET_NO_ADAPTER?"none":t);
    if(s->mode==0&&s->renew_seconds>=0){o=net_put(t,"renews in ");unsigned long m=(unsigned long)s->renew_seconds/60;if(m>=60){o=net_num(o,m/60);o=net_put(o," h ");}o=net_num(o,m%60);net_put(o," min");net_row(u,f,"Lease",t);}
    o=net_put(t,"received ");{unsigned long b=s->received;const char *unit=b>=1000000000?" GB":b>=1000000?" MB":b>=1000?" KB":" B";unsigned long d=b>=1000000000?1000000000:b>=1000000?1000000:b>=1000?1000:1;o=net_num(o,b/d);o=net_put(o,unit);}
    o=net_put(o,", sent ");{unsigned long b=s->sent;const char *unit=b>=1000000000?" GB":b>=1000000?" MB":b>=1000?" KB":" B";unsigned long d=b>=1000000000?1000000000:b>=1000000?1000000:b>=1000?1000:1;o=net_num(o,b/d);net_put(o,unit);}
    net_row(u,f,"Traffic",t);
    int on=s->state==Z_NET_ONLINE||s->state==Z_NET_CONNECTING||s->state==Z_NET_FAILED||s->state==Z_NET_CABLE_OUT;
    zui_add(u,7021,ZUI_BUTTON,(struct zui_rect){f->x,f->y,140,34},on?"Disconnect":"Connect",0,s->state!=Z_NET_NO_ADAPTER&&!a->net_busy&&!(s->mode==2&&!on));f->y+=46;
    zui_form_section(u,f,"IPv4 settings");
    int w=f->narrow?f->w:f->w-f->label_w;if(w>420)w=420;int x=f->narrow?f->x:f->x+f->label_w;
    text(u,f->x,f->y+8,"Configure",&zui_body,u->theme.text);
    zui_segment_track(u,(struct zui_rect){x,f->y,w,32});
    static const char *modes[3]={"Automatic","Manual","Off"};
    for(int i=0;i<3;i++)zui_add(u,7000+i,ZUI_SEGMENT,(struct zui_rect){x+i*w/3,f->y,w/3,32},modes[i],a->net_mode==i,!a->net_busy);
    f->y+=44;
    for(int i=0;i<4;i++)a->net_rects[i]=(struct zui_rect){0,0,0,0};
    if(a->net_mode==1){
        static const char *labels[4]={"Address","Prefix length","Gateway","DNS server"};
        for(int i=0;i<4;i++){
            text(u,f->x,f->y+8,labels[i],&zui_body,u->theme.text);
            struct zui_rect r={x,f->y,i==1?80:w,34};a->net_rects[i]=r;
            zui_round(u,r,8,u->theme.surface);zui_round_outline(u,r,8,a->net_field==i?u->theme.accent:u->theme.border);
            zui_input_draw(u,(struct zui_rect){r.x+8,r.y+1,r.w-12,r.h-2},&a->net_inputs[i],a->net_field==i);
            f->y+=44;
        }
    }else{
        status(u,f,a->net_mode==0?"The address, gateway and DNS server come from the network (DHCP).":"No address: the computer stays offline until this changes.");
    }
    zui_add(u,7020,ZUI_BUTTON,(struct zui_rect){x,f->y,120,34},a->net_busy?"Applying...":"Apply",1,a->net_edited&&!a->net_busy);
    f->y+=44;
    status(u,f,a->net_message[0]?a->net_message:"Applying keeps the old settings until the new ones connect.");
    if(a->net_locked)unlock_row(a,u,f);
}
static int parse_ipv4(const char *s,unsigned char *out){
    unsigned long part=0;int parts=0,digits=0;
    for(;;s++){
        if(*s>='0'&&*s<='9'){part=part*10+(unsigned long)(*s-'0');if(++digits>3||part>255)return 0;continue;}
        if(*s=='.'||!*s){if(!digits||parts>3)return 0;out[parts++]=(unsigned char)part;part=0;digits=0;if(!*s)break;continue;}
        return 0;
    }
    return parts==4;
}
static void copy_message(char *o,const char *s,unsigned long n){unsigned long i=0;for(;i+1<n&&s[i];i++)o[i]=s[i];o[i]=0;}
static void network_action(struct meridian *a,int id){
    if(!network_page(a))return;
    if(id>=7000&&id<=7002){if(a->net_mode!=id-7000){a->net_mode=id-7000;a->net_edited=1;a->net_field=a->net_mode==1?0:-1;}a->ui.dirty=1;return;}
    char message[200]={0};long r;
    if(id==7020){
        struct zenith_network_settings p={0};p.version=1;p.mode=(unsigned int)a->net_mode;
        if(a->net_mode==1){
            unsigned long prefix=0;for(const char *c=a->net_text[1];*c;c++){if(*c<'0'||*c>'9'){prefix=99;break;}prefix=prefix*10+(unsigned long)(*c-'0');}
            if(!parse_ipv4(a->net_text[0],p.address)){copy_message(a->net_message,"The address is not a valid IPv4 address, such as 192.168.1.20.",sizeof(a->net_message));a->ui.dirty=1;return;}
            if(prefix<8||prefix>30){copy_message(a->net_message,"The prefix length is a number from 8 to 30 (24 is 255.255.255.0).",sizeof(a->net_message));a->ui.dirty=1;return;}
            unsigned long mask=0xFFFFFFFFul<<(32-prefix)&0xFFFFFFFFul;for(int i=0;i<4;i++)p.mask[i]=(unsigned char)(mask>>(24-8*i));
            if(a->net_text[2][0]&&!parse_ipv4(a->net_text[2],p.gateway)){copy_message(a->net_message,"The gateway is not a valid IPv4 address.",sizeof(a->net_message));a->ui.dirty=1;return;}
            if(a->net_text[3][0]&&!parse_ipv4(a->net_text[3],p.dns)){copy_message(a->net_message,"The DNS server is not a valid IPv4 address.",sizeof(a->net_message));a->ui.dirty=1;return;}
        }
        a->net_busy=1;r=a->network(Z_NET_APPLY,0,message,sizeof(message),&p,sizeof(p));a->net_busy=0;
    }else if(id==7021){
        int on=a->net.state==Z_NET_ONLINE||a->net.state==Z_NET_CONNECTING||a->net.state==Z_NET_FAILED||a->net.state==Z_NET_CABLE_OUT;
        a->net_busy=1;r=a->network(Z_NET_CONNECT,on?0u:1u,message,sizeof(message),0,0);a->net_busy=0;
    }else return;
    message[sizeof(message)-1]=0;
    if(r==-13){a->net_locked=1;copy_message(a->net_message,"Changing the network needs an administrator. Unlock, then try again.",sizeof(a->net_message));}
    else if(r==0){a->net_locked=0;a->net_edited=0;copy_message(a->net_message,id==7020?"The new settings are active.":"Done.",sizeof(a->net_message));}
    else copy_message(a->net_message,message[0]?message:"That did not work.",sizeof(a->net_message));
    /* After a success the fields show the new state; after a refusal they keep what the person typed. */
    if(r==0)a->net_ok=0;
    refresh_network(a);a->ui.dirty=1;
}
/* Keys for a focused field; 1 when used. Tab moves between fields, Enter applies, Esc leaves the field. */
static int network_keys(struct meridian *a,const struct zenith_gui_event *e){
    if(!network_page(a)||a->net_mode!=1||a->net_field<0||e->type!=Z_GUI_KEY)return 0;
    if(e->extra==1){a->net_field=-1;a->ui.dirty=1;return 1;}
    if(e->extra==2){network_action(a,7020);return 1;}
    if(e->extra==4){a->net_field=(a->net_field+((e->modifiers&1)?3:1))%4;a->ui.dirty=1;return 1;}
    if(zui_input_event(&a->net_inputs[a->net_field],e)){a->net_edited=1;a->net_message[0]=0;a->ui.dirty=1;return 1;}
    return 0;
}
static void link_row(struct zui *u,struct zui_form *f,int page,const char *label){
    zui_add(u,ID_LINKS+page,ZUI_NAVIGATION,(struct zui_rect){f->x,f->y,f->w,36},label,0,1);f->y+=42;
}
static void overview_form(struct meridian *a,struct zui *u,struct zui_form *f){
    zui_form_section(u,f,"Appearance");
    link_row(u,f,MERIDIAN_COLORS,"Colors");
    for(int i=0;i<MERIDIAN_PAGE_COUNT;i++)if(same(meridian_pages[i].id,"style.wallpaper"))link_row(u,f,i,"Wallpaper");
    int shown=0;
    for(int i=0;i<a->recent_count&&shown<3;i++)if(a->recent[i]!=0&&a->recent[i]!=MERIDIAN_COLORS&&!same(meridian_pages[a->recent[i]].id,"style.wallpaper")){
        if(!shown)zui_form_section(u,f,"Recently visited");
        link_row(u,f,a->recent[i],meridian_pages[a->recent[i]].title);shown++;
    }
}
static void planned_form(struct meridian *a,struct zui *u,struct zui_form *f){
    zui_form_section(u,f,"Planned options");
    const char *s=meridian_pages[a->page].requirements;
    while(*s){
        unsigned long n=0;while(s[n]&&s[n]!='\n')n++;
        if(n>2&&s[0]=='-'&&s[1]==' '){
            zui_text(u,f->x,f->y,s+2,n-2,&zui_body,u->theme.text);f->y+=20;
            text(u,f->x,f->y,"Not available yet",&zui_caption,u->theme.secondary);f->y+=26;
        }
        s+=n;if(*s)s++;
    }
}
static void audio_percent(char *s,unsigned int value){
    if(value>100)value=100;
    int i=0;if(value==100)s[i++]='1';if(value>=10)s[i++]=(char)('0'+value/10%10);
    s[i++]=(char)('0'+value%10);s[i++]='%';s[i]=0;
}
static void audio_controls(struct zui *u,struct zui_form *f,int base,unsigned int volume,unsigned int flags,unsigned int peak,int enabled){
    int x=f->x,y=f->y,w=f->w;char percent[5];audio_percent(percent,volume);
    zui_add(u,base+1,ZUI_BUTTON,(struct zui_rect){x,y,32,32},"-",0,enabled&&volume>0);
    text(u,x+44,y+8,percent,&zui_body,u->theme.text);
    zui_add(u,base+2,ZUI_BUTTON,(struct zui_rect){x+98,y,32,32},"+",0,enabled&&volume<100);
    zui_add(u,base+3,ZUI_BUTTON,(struct zui_rect){x+142,y,100,32},flags&1?"Unmute":"Mute",flags&1,enabled);
    zui_meter(u,(struct zui_rect){x,y+42,w,6},(int)((unsigned long)peak*100/32768),u->theme.accent);f->y+=64;
}
static void audio_form(struct meridian *a,struct zui *u,struct zui_form *f){
    if(!a->audio_read||a->audio_error){status(u,f,"The audio service is unavailable on this kernel.");return;}
    if(a->audio_message)status(u,f,a->audio_message);
    if(!a->audio.available){status(u,f,"No supported audio device detected. Enable Intel HD Audio in the VM, then restart Zenith.");return;}
    int page=audio_page(a),shown=0,enabled=!!(a->audio.flags&1)&&a->audio_control;
    if(page==3){
        zui_form_section(u,f,"Applications using audio");
        for(unsigned int i=0;i<a->audio.stream_count;i++){
            struct zenith_audio_stream *s=&a->audio.streams[i];s->name[63]=0;a->audio_drawn[i]=s->handle;
            text(u,f->x,f->y,s->name,&zui_body,u->theme.text);f->y+=24;
            text(u,f->x,f->y,s->direction?"Recording - follows selected input":"Playback - follows selected output",&zui_caption,u->theme.secondary);f->y+=24;
            audio_controls(u,f,6000+(int)i*8,s->volume,s->flags,s->peak,enabled);shown++;
        }
        if(!shown)status(u,f,"No applications are playing or recording audio. Streams appear here while they are open.");
        return;
    }
    zui_form_section(u,f,page==1?"Output devices":"Input devices");
    for(unsigned int i=0;i<a->audio.device_count;i++){
        struct zenith_audio_device *d=&a->audio.devices[i];if(d->direction!=(unsigned int)(page-1))continue;
        d->name[63]=0;int base=5000+(int)i*8;shown++;
        text(u,f->x,f->y,d->name,&zui_body,u->theme.text);f->y+=28;
        zui_add(u,base,ZUI_BUTTON,(struct zui_rect){f->x,f->y,148,32},d->flags&2?"Selected device":"Use this device",!!(d->flags&2),enabled&&!(d->flags&2));
        if(page==1)zui_add(u,base+4,ZUI_BUTTON,(struct zui_rect){f->x+160,f->y,88,32},"Test",0,enabled&&!!(d->flags&2));
        f->y+=44;audio_controls(u,f,base,d->volume,d->flags,d->peak,enabled);
    }
    if(!shown)status(u,f,page==1?"No supported output route found.":"No supported input route found.");
    if(page==2){
        int recording=0;for(unsigned int i=0;i<a->audio.stream_count;i++)if(a->audio.streams[i].direction==1)recording++;
        status(u,f,recording?"Recording is active. See Applications for the programs using the input.":"No applications are recording.");
    }
    if(page==2)status(u,f,"The level meter is active only during authorized recording. Recording applications appear under Applications.");
}
static void audio_action(struct meridian *a,int id){
    if(!audio_page(a)||!a->audio_control)return;
    struct zenith_audio_control r={1,0,0,0,0,a->audio.revision};
    if(id>=5000&&id<5128){
        unsigned int i=(unsigned int)(id-5000)/8,part=(unsigned int)(id-5000)%8;if(i>=a->audio.device_count)return;
        struct zenith_audio_device *d=&a->audio.devices[i];r.target=d->id;
        if(part==0)r.action=d->direction?Z_AUDIO_SELECT_INPUT:Z_AUDIO_SELECT_OUTPUT;
        else if(part==1||part==2){r.action=Z_AUDIO_DEVICE_VOLUME;r.value=part==1?(d->volume>=5?d->volume-5:0):(d->volume<=95?d->volume+5:100);}
        else if(part==3){r.action=Z_AUDIO_DEVICE_MUTE;r.value=!(d->flags&1);}
        else if(part==4)r.action=Z_AUDIO_TEST;else return;
    }else if(id>=6000&&id<6128){
        unsigned int i=(unsigned int)(id-6000)/8,part=(unsigned int)(id-6000)%8;if(i>=a->audio.stream_count)return;
        struct zenith_audio_stream *s=&a->audio.streams[i];if(a->audio_drawn[i]!=s->handle)return;r.target=s->handle;
        if(part==1||part==2){r.action=Z_AUDIO_STREAM_VOLUME;r.value=part==1?(s->volume>=5?s->volume-5:0):(s->volume<=95?s->volume+5:100);}
        else if(part==3){r.action=Z_AUDIO_STREAM_MUTE;r.value=!(s->flags&1);}else return;
    }else return;
    long result=a->audio_control(&r);
    a->audio_message=result==-11?"Audio changed while editing. Try again.":result==-13?"Audio controls belong to the active session.":result?"The audio device could not apply this change.":r.action==Z_AUDIO_TEST?"Playing the output test.":0;
    refresh_audio(a);a->ui.dirty=1;
}
static void draw_page(struct meridian *a,struct zui *u,struct zui_rect c){
    const struct meridian_page *p=&meridian_pages[a->page];int kind=page_kind(a),footer=kind!=BIND_NONE||page_has_pickers(a)?56:0,x=c.x+24;
    text(u,x,c.y+14,p->title,&zui_heading,u->theme.text);
    text(u,x,c.y+50,meridian_categories[nav_category_of(a->page)],&zui_caption,u->theme.secondary);
    struct zui_rect body={c.x,c.y+74,c.w,c.h-74-footer};
    int top=zui_scroll_begin(u,&a->page_scroll,body);
    struct zui_form f;zui_form_begin(&f,(struct zui_rect){body.x+16,body.y,body.w-32,body.h},top+8,u->width);
    note(u,&f,wip_note(a,kind));
    if(kind==BIND_COLORS)colors_form(a,u,&f);
    else if(kind==BIND_DISPLAY)display_form(a,u,&f);
    else if(page_has_pickers(a)&&kind!=BIND_COLORS){pickers_form(a,u,&f);}
    else if(kind!=BIND_NONE)preference_form(a,u,&f,kind);
    else if(audio_page(a))audio_form(a,u,&f);
    else if(a->page==0)overview_form(a,u,&f);
    else if(network_page(a))network_form(a,u,&f);
    else if(information_kind(a))lines_form(u,&f,a->information_read&&a->public_kind==information_kind(a)&&!a->public_error?a->public_information:"Information service unavailable.");
    else if(a->page==MERIDIAN_ABOUT&&a->information[0])lines_form(u,&f,a->information);
    else planned_form(a,u,&f);
    zui_scroll_end(u,&a->page_scroll,zui_form_height(&f,top)+8);
    if(footer)zui_footer(u,(struct zui_rect){c.x,c.y+c.h-56,c.w,56},ID_FOOTER,0,page_changed(a),binding_at_defaults(&a->bindings,kind));
}
/* Indices change every frame: restore focus and press by control id. Dialog buttons
 * are registered last in reverse order, so their index follows from the page count. */
static int index_of(const struct zui *u,int id){
    for(int i=0;i<u->count;i++)if(u->controls[i].id==id&&u->controls[i].enabled)return i;
    return -1;
}
/* What changed fades: the page rises into place, while a changed sidebar only crossfades,
 * and the rise stays inside the rounded corners. */
static void fade_plan(struct meridian *a,struct zui_workspace w){
    if(!a->fade_request)return;
    struct zui_rect c=w.content;
    a->fade_ready=1;a->fade_rise=0;
    if(a->fade_request==FADE_PAGE){a->fade_rect=(struct zui_rect){c.x+12,c.y+12,c.w-24,c.h-24};a->fade_rise=10;}
    else if(a->fade_request==FADE_SIDEBAR)a->fade_rect=w.sidebar;
    else a->fade_rect=(struct zui_rect){0,0,a->ui.width,a->ui.height};
    a->fade_request=0;
}
void meridian_draw(struct meridian *a){
    struct zui *u=&a->ui;
    int focus=u->focus>=0&&u->focus<u->count?u->controls[u->focus].id:0,pressed=u->pressed>=0&&u->pressed<u->count?u->controls[u->pressed].id:0;
    u->count=0;
    struct zui_workspace w=zui_workspace_begin(u,sidebar_width(a));
    fade_plan(a,w);
    draw_sidebar(a,u,w.sidebar);
    draw_page(a,u,w.content);
    zui_clip(u,(struct zui_rect){0,0,u->width,u->height});
    zui_update_hover(u);if(a->combo.open||a->scale_combo.open||a->dialog.open)u->hover=-1;
    u->focus=focus?index_of(u,focus):-1;u->pressed=pressed?index_of(u,pressed):-1;
    if(a->focus_row!=-1){u->focus=index_of(u,ID_ROWS+(a->focus_row==-2?a->sidebar.selected:a->focus_row));a->focus_row=-1;}
    zui_draw_controls(u);
    struct zui_rect r=search_rect(w.sidebar);
    zui_input_draw(u,r,&a->search,a->search_focus&&u->window_focused);
    if(!a->query[0]&&!a->search_focus)text(u,r.x+10,r.y+9,"Search settings",&zui_caption,u->theme.secondary);
    int kind=page_kind(a);
    if(kind>=BIND_KEYBOARD&&a->combo.open)zui_combo_popup(u,&a->combo,binding_choice_count(&a->bindings,kind),choice_rows,a);
    if(kind==BIND_DISPLAY&&a->scale_combo.open)zui_combo_popup(u,&a->scale_combo,binding_scale_count(&a->bindings),scale_rows,a);
    if(a->bindings.display.phase==2 && (a->bindings.display.flags&4)){
        unsigned int seconds=a->bindings.display.seconds;char *t=a->display_countdown;
        const char *lead="Automatically reverts in ";while(*lead)*t++=*lead++;
        if(seconds>=10){*t++='1';}*t++=(char)('0'+seconds%10);
        const char *tail=" seconds.";while(*tail)*t++=*tail++;*t=0;
        a->display_dialog=(struct zui_dialog){1,4100,"Keep this display?",a->display_countdown,{"Keep","Revert"},2};
        int base=u->count,top=4102;
        if(focus>4100&&focus<=top)u->focus=base+top-focus;
        if(pressed>4100&&pressed<=top)u->pressed=base+top-pressed;
        zui_dialog_draw(u,&a->display_dialog);
    }else {a->display_dialog.open=0;}
    if(a->dialog.open && !a->display_dialog.open){
        int base=u->count,top=ID_DIALOG+a->dialog.count;
        if(focus>ID_DIALOG&&focus<=top)u->focus=base+top-focus;
        if(pressed>ID_DIALOG&&pressed<=top)u->pressed=base+top-pressed;
        zui_dialog_draw(u,&a->dialog);
    }
    zui_clip(u,(struct zui_rect){0,0,u->width,u->height});u->dirty=0;
}
static void confirm(struct meridian *a,int pending){
    a->pending=pending;a->combo.open=a->scale_combo.open=0;
    a->dialog=(struct zui_dialog){1,ID_DIALOG,"Apply changes?","This page has unapplied changes.",{"Apply","Discard","Cancel"},3};
    a->ui.pressed=-1;a->ui.dirty=1;
}
/* Colors and Application Style edit one appearance record: a draft moves between them without asking. */
static int shares_draft(const struct meridian *a,int page){return page>=0&&page<MERIDIAN_PAGE_COUNT&&binding_of(meridian_pages[page].id)==page_kind(a);}
static void request_row(struct meridian *a,int row){
    struct meridian_nav next=a->nav;nav_activate(&next,row);
    if(next.page!=a->page&&((!shares_draft(a,next.page)&&binding_changed(&a->bindings,page_kind(a)))||styles_changed(&a->styles,meridian_pages[a->page].id))){a->pending_row=row;confirm(a,PENDING_ROW);return;}
    if(next.mode!=a->nav.mode||next.category!=a->nav.category)zui_scroll_jump(&a->sidebar.scroll,0);
    adopt(a,&next);
}
static void request_page(struct meridian *a,int page){
    if(page!=a->page&&((!shares_draft(a,page)&&binding_changed(&a->bindings,page_kind(a)))||styles_changed(&a->styles,meridian_pages[a->page].id))){a->pending_page=page;confirm(a,PENDING_PAGE);return;}
    struct meridian_nav next=a->nav;nav_open(&next,meridian_pages[page].id);
    zui_scroll_jump(&a->sidebar.scroll,0);adopt(a,&next);
}
static void close_request(struct meridian *a){
    if(any_changed(a))confirm(a,PENDING_CLOSE);else a->closing=1;
}
static void resolve(struct meridian *a,int button){
    int pending=a->pending;a->pending=PENDING_NONE;a->ui.dirty=1;
    if(button==3)return;
    if(button==1&&page_apply(a))return;
    if(button==2)page_reset(a);
    if(pending==PENDING_CLOSE)a->closing=1;
    else if(pending==PENDING_ROW)request_row(a,a->pending_row);
    else if(pending==PENDING_PAGE)request_page(a,a->pending_page);
}
static void back(struct meridian *a){
    a->ui.focus=-1;
    if(a->nav.mode==NAV_SEARCH)clear_search(a);
    else if(a->nav.mode==NAV_PAGES){a->fade_request|=FADE_SIDEBAR;nav_back(&a->nav);zui_scroll_jump(&a->sidebar.scroll,0);}
    a->ui.dirty=1;
}
static void colors_action(struct meridian *a,int id){
    struct settings_app *s=&a->colors;unsigned int mode=s->mode,accent=s->accent,effects=s->effects,level=s->translucency;
    if(id==60||id==61){settings_set_scope(s,id==60);return;}
    if(id==1||id==2)mode=(unsigned int)id-1;
    else if(id>=10&&id<=15)accent=(unsigned int)id-10;
    else if(id>=50&&id<=52)effects^=1U<<(id-50);
    else if(id==53)level=level<10?0:level-10;
    else if(id==54)level=level>90?100:level+10;
    else return;
    settings_preview(s,mode,accent,effects,level);
}
static void page_keys(struct meridian *a,const struct zenith_gui_event *e){
    int max=zui_scroll_maximum(&a->page_scroll),next=a->page_scroll.offset;
    if(e->extra==7)next=zui_scroll_step(next,-40,max);
    else if(e->extra==8)next=zui_scroll_step(next,40,max);
    else if(e->extra==10)next=0;
    else if(e->extra==11)next=max;
    else if(e->extra==ZUI_KEY_PAGE_UP)next=zui_scroll_step(next,-a->page_scroll.rect.h,max);
    else if(e->extra==ZUI_KEY_PAGE_DOWN)next=zui_scroll_step(next,a->page_scroll.rect.h,max);
    if(next!=a->page_scroll.offset){a->page_scroll.offset=next;a->ui.dirty=1;}
}
void meridian_event(struct meridian *a,const struct zenith_gui_event *e){
    struct zui *u=&a->ui;
    if(e->type==Z_GUI_MOVE){zui_event(u,e);return;}
    if(e->type==Z_GUI_FOCUS||e->type==Z_GUI_RESET){zui_event(u,e);a->combo.open=a->scale_combo.open=0;a->sidebar.scroll.dragging=a->page_scroll.dragging=0;return;}
    if(e->type==Z_GUI_CLOSE){a->closing=1;return;}
    if(!u->window_focused&&e->type!=Z_GUI_CLOSE_INTENT)return;
    if(a->display_dialog.open){
        int button=zui_dialog_event(u,&a->display_dialog,e);
        if(e->type==Z_GUI_CLOSE_INTENT)button=2;
        if(button){struct meridian_bindings *b=&a->bindings;if(b->display_save)b->display_save(button==1?2:3,0,0,b->display.revision,b->display.token);binding_refresh(b);u->dirty=1;}
        return;
    }
    if(a->dialog.open){int button=zui_dialog_event(u,&a->dialog,e);if(button)resolve(a,button);sync_theme(a);return;}
    if(e->type==Z_GUI_CLOSE_INTENT||(e->type==Z_GUI_KEY&&e->modifiers==2&&(e->code=='q'||e->code=='Q'))){close_request(a);return;}
    int kind=page_kind(a);
    if(kind==BIND_DISPLAY&&binding_writable(&a->bindings,kind)&&!a->combo.open){
        int was=a->scale_combo.open,chosen=zui_combo_event(u,&a->scale_combo,e,scale_rows,a);
        if(chosen){binding_choose_scale(&a->bindings,(unsigned int)chosen-1);u->focus=index_of(u,ID_SCALE);}
        if(was||a->scale_combo.open){if(a->scale_combo.open!=was)u->pressed=-1;u->dirty=1;return;}
    }
    /* The wallpaper picker has tiles, not the combo: its stale field rectangle must not take clicks. */
    if(kind>=BIND_KEYBOARD&&binding_writable(&a->bindings,kind)&&!page_has_pickers(a)){
        int was=a->combo.open,chosen=zui_combo_event(u,&a->combo,e,choice_rows,a);
        if(chosen){binding_choose(&a->bindings,kind,(unsigned int)chosen-1);u->focus=index_of(u,ID_COMBO);}
        if(was||a->combo.open){if(a->combo.open!=was)u->pressed=-1;u->dirty=1;return;}
    }
    if(e->type==Z_GUI_KEY&&e->modifiers==2&&(e->code=='f'||e->code=='F')){
        a->search_focus=1;u->dirty=1;return;
    }
    if(network_keys(a,e)){sync_theme(a);return;}
    if(e->type==Z_GUI_DOWN&&network_page(a)){int was=a->net_field;a->net_field=-1;for(int i=0;i<4;i++){struct zui_rect r=a->net_rects[i];if(r.w&&e->x>=r.x&&e->y>=r.y&&e->x<r.x+r.w&&e->y<r.y+r.h)a->net_field=i;}if(was!=a->net_field)u->dirty=1;}
    if(a->search_focus&&e->type==Z_GUI_KEY&&e->extra!=4){
        if(e->extra==1)clear_search(a);
        else if(e->extra==2){a->search_focus=0;if(a->nav.mode==NAV_SEARCH&&a->nav.result_count)request_row(a,1);}
        else if(zui_input_event(&a->search,e)){nav_search(&a->nav,a->query);zui_scroll_jump(&a->sidebar.scroll,0);}
        u->dirty=1;sync_theme(a);return;
    }
    if(e->type==Z_GUI_KEY&&e->extra==4)a->search_focus=0;
    if(e->type==Z_GUI_DOWN)a->search_focus=0;
    if(e->type==Z_GUI_KEY&&(e->extra==1||(e->modifiers==4&&e->extra==5)||(!e->modifiers&&e->extra==3))){back(a);return;}
    int focused=u->focus>=0&&u->focus<u->count?u->controls[u->focus].id:0,on_rows=focused>=ID_ROWS&&focused<ID_ROWS+MERIDIAN_PAGE_COUNT+1;
    if(on_rows&&e->type==Z_GUI_KEY)a->sidebar.selected=focused-ID_ROWS;
    if(e->type!=Z_GUI_KEY||on_rows){
        int row=zui_list_event(u,&a->sidebar,e,sidebar_rows,a);
        if(row){
            /* Rows are replaced by id: never leave focus on whichever row inherits it. */
            int mode=a->nav.mode,category=a->nav.category;
            u->pressed=-1;request_row(a,row-1);
            if(a->nav.mode!=mode||a->nav.category!=category){u->focus=-1;if(e->type==Z_GUI_KEY)a->focus_row=-2;}
            else if(e->type==Z_GUI_KEY)a->focus_row=row-1;
            sync_theme(a);return;
        }
        if(on_rows&&e->type==Z_GUI_KEY&&(e->extra==7||e->extra==8||e->extra==10||e->extra==11||e->extra==ZUI_KEY_PAGE_UP||e->extra==ZUI_KEY_PAGE_DOWN||(!e->extra&&e->code>32&&e->code<127))){a->focus_row=a->sidebar.selected;u->focus_visible=1;u->dirty=1;return;}
    }
    if(zui_scroll_event(u,&a->page_scroll,e))return;
    if(e->type==Z_GUI_KEY&&!e->modifiers&&!on_rows)page_keys(a,e);
    int id=zui_event(u,e);
    if(id==ID_UNLOCK){unlock(a);u->dirty=1;sync_theme(a);return;}
    if(id>=5000&&id<6128)audio_action(a,id);
    if(id>=7000&&id<=7021)network_action(a,id);
    else if(id==ID_SEARCH){a->search_focus=1;u->dirty=1;}
    else if(id==ID_FOOTER+ZUI_FOOTER_APPLY)page_apply(a);
    else if(id==ID_FOOTER+ZUI_FOOTER_RESET)page_reset(a);
    else if(id==ID_FOOTER+ZUI_FOOTER_DEFAULTS)binding_defaults(&a->bindings,kind);
    else if((id==4001||id==4002)&&kind==BIND_DISPLAY){
        struct meridian_bindings *b=&a->bindings;
        if(b->display_save)b->display_save(id==4001?2:3,0,0,b->display.revision,b->display.token);
        binding_refresh(b);
    }
    else if(id>=ID_LINKS&&id<ID_LINKS+MERIDIAN_PAGE_COUNT)request_page(a,id-ID_LINKS);
    else if(id==9300){struct meridian_styles *st=&a->styles;styles_notify_set(st,!st->notify_dnd,st->notify_popup);}
    else if(id>=9310&&id<9313){struct meridian_styles *st=&a->styles;styles_notify_set(st,st->notify_dnd,(unsigned int)(id-9310));}
    else if(id==9320){struct meridian_styles *st=&a->styles;if(st->notify_post){long r=st->notify_post("Notifications work","This is how notifications from your apps will look.","info");st->message=r>0?"Test sent.":r==-16?"Sent too often; wait a few seconds.":"The desktop does not show notifications yet.";}}
    else if(id==9001||id==9002){struct meridian_styles *st=&a->styles;int v=(int)st->sound_level+(id==9002?10:-10);styles_sound_set(st,(unsigned int)(v<0?0:v>100?100:v),st->sound_mask);}
    else if(id>=9100&&id<9115){struct meridian_styles *st=&a->styles;styles_sound_set(st,st->sound_level,st->sound_mask^(1u<<(id-9100)));}
    else if(id>=9200&&id<9215){if(a->styles.sound_play)a->styles.sound_play((unsigned int)(id-9200));}
    else if(id>=ID_PICKS&&id<ID_PICKS+STYLE_FIELDS*64)styles_choose(&a->styles,(unsigned int)(id-ID_PICKS)/64,(unsigned int)(id-ID_PICKS)%64);
    else if(id&&kind==BIND_COLORS)colors_action(a,id);
    if(id)u->dirty=1;
    sync_theme(a);
}
