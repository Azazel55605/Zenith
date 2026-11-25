/* Host tests for the stage-2 Meridian shell: real clicks/keys over the shared widgets. */
#include "meridian.h"
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include <limits.h>
#include <stdlib.h>
static struct zenith_appearance saved;
static int saves,dirty;
static unsigned int material_saved,material_level=40;
static long read_state(struct zenith_appearance *s,unsigned int *materials,unsigned int *effective,unsigned int *available,unsigned int *reason,unsigned int *level){
    *level=material_level;*s=saved;*materials=*effective=material_saved;*available=7;*reason=0;return 0;
}
static long save(unsigned int m,unsigned int a,unsigned int materials,unsigned int level,unsigned long revision){
    if(revision!=saved.revision)return -11;
    material_level=level;material_saved=materials;saved.mode=m;saved.accent=a;saved.revision++;saves++;return 0;
}
static long mark(int d){dirty=d;return 0;}
static unsigned int cursor_size,cursor_pack;
static struct zenith_system_settings preferences={.version=1,.flags=3,.revision=1,.keyboard_count=2,.zone_count=2,
    .current_zone="UTC",.keyboards={"us","de"},.zones={"UTC","Europe/Berlin"}};
static long read_preferences(struct zenith_system_settings *s){*s=preferences;return 0;}
static long save_preferences(unsigned int field,unsigned int choice,unsigned long revision){
    if(!(preferences.flags&2))return -13;
    if(revision!=preferences.revision)return -11;
    if(field==1)preferences.keyboard=choice;
    else if(field==2){preferences.zone=choice;strcpy(preferences.current_zone,preferences.zones[choice]);}
    else if(field==6)preferences.wallpaper=choice;
    else if(field==8){cursor_pack=choice;preferences.cursor=choice==1||choice==3?1:0;}   /* the snapshot names two */
    else if(field==9)cursor_size=choice;
    preferences.revision++;return 0;
}
/* The style catalogue: 12 legacy wallpapers (4 dynamic, 8 solid) and the second set of 4 dynamic ones. */
static int previews_drawn;
static long fake_catalog(unsigned int field,struct zenith_style_list *out){
    static const char *walls[16]={"Zenith","Dunes","Aurora","Contour","Graphite","Midnight","Forest","Plum","Cream","Mist","Sand","Accent color","Orbits","Tides","Facets","Nebula"};
    static const char *packs[5]={"Meridian Light","Meridian Dark","Contrast White","Contrast Black","Comet"};
    static const char *sizes[3]={"Normal (24 px)","Large (32 px)","Largest (48 px)"};
    static const char *packs16[2]={"Zenith Chimes","Silent"};
    const char **labels=field==6?walls:field==8?packs:field==9?sizes:field==16?packs16:0;unsigned int count=field==6?16:field==8?5:field==9?3:field==16?2:0;
    if(!labels)return -19;
    memset(out,0,sizeof(*out));out->version=1;out->field=field;out->count=count;out->revision=preferences.revision;
    out->current=field==6?preferences.wallpaper:field==8?cursor_pack:field==16?0:cursor_size;
    for(unsigned int i=0;i<count;i++){strcpy(out->entries[i].label,labels[i]);strcpy(out->entries[i].name,labels[i]);out->entries[i].kind=field==6&&i>=4&&i<12;out->entries[i].color=0x202125;}
    return 0;
}
static unsigned int sound_level=70,sound_mask=0x9FF;static int sound_played=-1;
static long fake_sounds(struct zenith_sound_settings *out){
    memset(out,0,sizeof(*out));out->version=1;out->level=sound_level;out->mask=sound_mask;out->count=12;out->revision=preferences.revision;
    static const char *names[12]={"Sign in","Sign out and lock","Notification","Attention","Error","Device connected","Device removed","Done","Volume","Trash emptied","Screenshot","Shut down"};
    for(int i=0;i<12;i++)strcpy(out->labels[i],names[i]);
    return 512;
}
static long fake_sound_save(unsigned int level,unsigned int mask,unsigned long revision){if(revision!=preferences.revision)return -11;sound_level=level;sound_mask=mask;preferences.revision++;return 0;}
static long fake_sound_play(unsigned int event){sound_played=(int)event;return 0;}
static unsigned int notify_dnd,notify_popup=1,notify_flags=Z_NOTIFY_FULLSCREEN|Z_NOTIFY_HOVER|Z_NOTIFY_SOUND,notify_corner;
static int notify_posted;
static long fake_notify_read(struct zenith_notify_settings *out){
    *out=(struct zenith_notify_settings){2,notify_dnd,notify_popup,3,preferences.revision,notify_flags,notify_corner};return sizeof(*out);
}
static long fake_notify_save(const struct zenith_notify_settings *value){
    if(value->revision!=preferences.revision)return -11;
    notify_dnd=value->dnd;notify_popup=value->popup;notify_flags=value->flags;notify_corner=value->corner;preferences.revision++;return 0;
}
static long fake_notify_post(const char *title,const char *body,const char *icon){(void)body;(void)icon;notify_posted=title&&title[0];return 7;}
static int muted_orbit;
static long fake_notify_senders(char *out,unsigned int capacity){const char *t=muted_orbit?"Orbit\t1\nShell\t0\n":"Orbit\t0\nShell\t0\n";unsigned int n=(unsigned int)strlen(t);if(n>capacity)return -34;memcpy(out,t,n);return n;}
static long fake_notify_mute(const char *name,unsigned int off,unsigned long revision){if(revision!=preferences.revision)return -11;if(!strcmp(name,"Orbit"))muted_orbit=(int)off;preferences.revision++;return 0;}
static long fake_preview(unsigned int field,unsigned int index,int w,int h,unsigned int *pixels){
    if((field!=6&&field!=8&&field!=9)||index>=16)return -22;
    for(int y=0;y<h;y++)for(int x=0;x<w;x++)pixels[y*w+x]=0xFF000000u|(unsigned int)(60+x*150/w)<<16|(unsigned int)(80+y*120/h)<<8|(index*12u);
    previews_drawn++;return w<<16|h;
}
static int tiling_supported;
static struct zenith_desktop_preferences desktop_state[6]={
    [1]={.version=1,.page=1,.flags=1,.revision=1},
    [2]={.version=1,.page=2,.flags=1,.revision=1,.values={0,250,1,0,0,0,1,0}},
    [3]={.version=1,.page=3,.flags=1,.revision=1},
    [4]={.version=1,.page=4,.flags=1,.revision=1,.values={1},.count=1,.entries={{0,"Desktop 1",{0}}}},
    [5]={.version=1,.page=5,.flags=1,.revision=1,.values={0,250,1,0,0,0,1,0}}
};
static long fake_desktop_read(unsigned int page,struct zenith_desktop_preferences *out){
    assert(page>=1&&page<=5);
    if(page==Z_DESKTOP_WINDOWS_TILING&&!tiling_supported)return -22;
    *out=desktop_state[page];return 0;
}
static long fake_desktop_save(const struct zenith_desktop_preferences *p){
    assert(p->page>=1&&p->page<=5&&p->flags==0);
    struct zenith_desktop_preferences *s=&desktop_state[p->page];
    if(!(s->flags&1))return -13;
    if(p->revision!=s->revision)return -11;
    *s=*p;s->flags=1;s->revision++;
    return 0;
}
static long fake_desktop_catalog(struct zenith_desktop_apps *apps){
    *apps=(struct zenith_desktop_apps){1,2,{{"Editor","Aurora"},{"Terminal","Polaris"}}};
    return 8+2*sizeof(apps->entries[0]);
}
static long information_read(unsigned int kind,struct zenith_settings_information *s){
    *s=(struct zenith_settings_information){.version=1,.kind=kind};
    const char *text=kind==1?"address=10.0.2.15\ngateway=10.0.2.2\n":"user uid=1000\n";
    s->length=(unsigned int)strlen(text);strcpy(s->text,text);return 0;
}
static struct zenith_audio audio_state={.version=1,.device_count=2,.stream_count=1,.flags=1,.revision=1,.output=11,.input=22,.available=1,.rate=48000,.channels=2,.bits=16,
    .devices={{.id=11,.flags=2,.volume=80,.name="HD Audio Speakers"},{.id=22,.direction=1,.flags=2,.volume=80,.name="HD Audio Microphone"}},
    .streams={{.handle=33,.pid=7,.uid=1000,.volume=70,.name="Music player"}}};
static long read_audio(struct zenith_audio *s){*s=audio_state;return 0;}
static long change_audio(const struct zenith_audio_control *r){
    assert(r->version==1&&r->revision==audio_state.revision);
    if(r->target==11||r->target==22){struct zenith_audio_device *d=&audio_state.devices[r->target==22];
        if(r->action==Z_AUDIO_DEVICE_VOLUME)d->volume=r->value;
        else if(r->action==Z_AUDIO_DEVICE_MUTE)d->flags=(d->flags&~1u)|r->value;
        else assert(r->action==Z_AUDIO_TEST&&r->target==11);
    }else {assert(r->target==33);if(r->action==Z_AUDIO_STREAM_VOLUME)audio_state.streams[0].volume=r->value;
        else {assert(r->action==Z_AUDIO_STREAM_MUTE);audio_state.streams[0].flags=r->value;}}
    audio_state.revision++;return 0;
}
static struct zenith_display display;
static int display_actions;
static long read_display(struct zenith_display *s){*s=display;return 0;}
static long change_display(unsigned int action,unsigned int mode,unsigned int scale,unsigned long revision,unsigned long token){
    assert(revision==display.revision);display_actions++;
    if(action==1){assert(mode==1&&scale==2&&!token);display.phase=1;display.flags|=4;display.token=77;}
    else {assert(token==77);display.phase=0;display.flags&=~4U;if(action==2){display.width=1600;display.height=1200;display.scale=2;}}
    display.revision++;return 0;
}
/* A fake network service: online by DHCP; manual settings with gateway 10.0.2.99 do not connect; -13 while locked. */
static struct zenith_network_status net_state={.version=1,.state=Z_NET_ONLINE,.mode=0,.flags=Z_NET_LINK|Z_NET_LEASE,
    .address={10,0,2,15},.mask={255,255,255,0},.gateway={10,0,2,2},.dns={{10,0,2,3}},.dns_count=1,.mac={0x52,0x54,0,0x12,0x34,0x56},
    .connected_seconds=600,.lease_seconds=86000,.renew_seconds=43000,.received=1500000,.sent=20000};
static int net_locked,net_applies;static struct zenith_network_settings net_last;
static long fake_network(unsigned int op,unsigned int value,void *out,unsigned int capacity,const void *in,unsigned int bytes){
    if(op==Z_NET_STATUS){assert(capacity>=sizeof(net_state));memcpy(out,&net_state,sizeof(net_state));return sizeof(net_state);}
    if(op==Z_NET_APPLY){
        assert(bytes==sizeof(net_last));if(net_locked)return -13;
        memcpy(&net_last,in,sizeof(net_last));net_applies++;
        if(net_last.mode==1&&net_last.gateway[3]==99){snprintf(out,capacity,"The new settings did not connect (the gateway did not answer); the previous settings are back.");return -5;}
        net_state.mode=net_last.mode;if(net_last.mode==1){memcpy(net_state.saved_address,net_last.address,4);memcpy(net_state.saved_mask,net_last.mask,4);memcpy(net_state.saved_gateway,net_last.gateway,4);}
        return 0;
    }
    if(op==Z_NET_CONNECT){net_state.state=value?Z_NET_ONLINE:Z_NET_OFF;return 0;}
    (void)value;return -22;
}
static long portal_state=2;
static long fake_authorize(void){return 9;}
static long fake_poll(long h,char *message,unsigned long size){assert(h==9);snprintf(message,size,"%s",portal_state==4?"dismissed":"authenticated as user");return portal_state;}
static struct meridian app;
static unsigned int pixels[1920*1000];
static void draw(void){meridian_draw(&app);}
static void send(struct zenith_gui_event e){meridian_event(&app,&e);draw();}
static void key(int code,int extra,int mods){send((struct zenith_gui_event){Z_GUI_KEY,code,0,0,mods,extra});}
static void type(const char *s){for(;*s;s++)key(*s,0,0);}
static int find_id(int id){for(int i=0;i<app.ui.count;i++)if(app.ui.controls[i].id==id)return i;return -1;}
static int find_label(const char *label){for(int i=0;i<app.ui.count;i++)if(!strcmp(app.ui.controls[i].label,label))return i;return -1;}
static void click_index(int i){
    assert(i>=0);struct zui_rect r=zui_control_area(&app.ui,i);assert(r.w>0&&r.h>0);
    int x=r.x+r.w/2,y=r.y+r.h/2;
    send((struct zenith_gui_event){Z_GUI_DOWN,1,x,y,0,0});send((struct zenith_gui_event){Z_GUI_UP,1,x,y,0,0});
}
static void click_id(int id){click_index(find_id(id));}
static void click_label(const char *label){click_index(find_label(label));}
static void wheel_until(int id){for(int i=0;i<40&&find_id(id)<0;i++)send((struct zenith_gui_event){Z_GUI_SCROLL,-120,400,300,0,0});assert(find_id(id)>=0);}
static int page_id(const char *id){for(int i=0;i<MERIDIAN_PAGE_COUNT;i++)if(!strcmp(id,meridian_pages[i].id))return i;assert(0);return 0;}
static int enabled(int id){int i=find_id(id);return i>=0&&app.ui.controls[i].enabled;}
static void reveal(int id){wheel_until(id);for(int i=0;i<3;i++)send((struct zenith_gui_event){Z_GUI_SCROLL,-120,400,300,0,0});assert(find_id(id)>=0);}
static int find_page_index(const char *id){for(int i=0;i<MERIDIAN_PAGE_COUNT;i++)if(!strcmp(meridian_pages[i].id,id))return i;return -1;}
int main(int argc,char **argv){
    saved.version=1;saved.revision=1;saved.flags=3;
    struct zui palette;zui_init(&palette,pixels,600,480);
    for(int m=0;m<2;m++)for(int a=0;a<6;a++){zui_appearance(&palette,m,a);memcpy(saved.palette[m][a],&palette.theme,sizeof(palette.theme));}
    assert(meridian_init(&app,pixels,(struct settings_backend){read_state,save,mark,0},0));
    assert(meridian_resize(&app,640,480,1920*1000));meridian_refresh(&app);
    send((struct zenith_gui_event){Z_GUI_FOCUS,1,0,0,0,0});
    assert(app.page==page_id("quick.overview"));
    /* Every destination draws at the default and the minimum size within the region budget. */
    for(int size=0;size<2;size++){
        assert(meridian_resize(&app,size?600:640,480,1920*1000));
        for(int i=0;i<MERIDIAN_PAGE_COUNT;i++){assert(meridian_open(&app,meridian_pages[i].id));draw();assert(app.page==i&&app.ui.count<=ZUI_MAX_CONTROLS);}
    }
    assert(!meridian_open(&app,"missing.page"));
    assert(!meridian_resize(&app,599,480,1920*1000)&&!meridian_resize(&app,INT_MAX,INT_MAX,1920*1000));
    assert(meridian_resize(&app,640,480,1920*1000));
    /* Drill-down, back and search by real input. */
    assert(meridian_open(&app,"quick.overview"));draw();
    click_label("Input & Output");assert(app.nav.mode==NAV_PAGES&&app.page==page_id("input.mouse"));
    click_label("Keyboard");assert(app.page==page_id("input.keyboard"));
    click_id(app.sidebar.base_id);assert(app.nav.mode==NAV_CATEGORIES&&app.page==page_id("input.keyboard"));
    key('f',0,2);assert(app.search_focus);type("bluetooth");assert(app.nav.mode==NAV_SEARCH&&app.nav.result_count==1);
    key(0,2,0);assert(app.page==page_id("devices.bluetooth")&&!app.search_focus);
    key(0,1,0);assert(app.nav.mode==NAV_CATEGORIES);
    /* Selector: a pointer leaves no focus ring and no focus on a row that replaced it. */
    assert(meridian_open(&app,"input.mouse"));draw();
    click_id(app.sidebar.base_id);assert(app.nav.mode==NAV_CATEGORIES&&!app.ui.focus_visible);
    assert(app.ui.focus<0||app.ui.controls[app.ui.focus].id!=app.sidebar.base_id);
    /* Keyboard: Tab onto rows, arrows move focus, Enter activates the focused row. */
    key(0,4,0);assert(app.ui.focus_visible&&app.ui.controls[app.ui.focus].id==1002);
    key(0,4,0);assert(app.ui.controls[app.ui.focus].id==app.sidebar.base_id);
    key(0,8,0);key(0,8,0);assert(app.ui.controls[app.ui.focus].id==app.sidebar.base_id+2&&app.nav.category==1);
    key(0,2,0);assert(app.nav.mode==NAV_PAGES&&app.nav.category==2&&app.page==page_id("devices.bluetooth"));
    assert(app.ui.focus_visible&&app.ui.controls[app.ui.focus].id==app.sidebar.base_id+1);
    key(0,3,0);assert(app.nav.mode==NAV_CATEGORIES);
    /* Hover tracks the pointer without stealing input; Page Down scrolls the form. */
    assert(meridian_open(&app,"style.colors"));draw();app.ui.hover_supported=1;
    int hover_row=zui_control_area(&app.ui,find_id(app.sidebar.base_id+2)).y+10;
    send((struct zenith_gui_event){Z_GUI_MOVE,0,100,hover_row,0,0});assert(app.ui.controls[app.ui.hover].id==app.sidebar.base_id+2);
    send((struct zenith_gui_event){Z_GUI_MOVE,0,-1,-1,0,0});assert(app.ui.hover==-1);app.ui.hover_supported=0;
    assert(meridian_open(&app,"style.application"));draw();
    key(0,ZUI_KEY_PAGE_DOWN,0);assert(app.page_scroll.offset>0);key(0,ZUI_KEY_PAGE_UP,0);assert(app.page_scroll.offset==0);
    /* Colors draft, footer state and the unsaved-changes dialog on navigation. */
    assert(meridian_open(&app,"style.colors"));draw();
    assert(!enabled(900+ZUI_FOOTER_APPLY)&&!enabled(900+ZUI_FOOTER_RESET));
    click_label(saved.mode?"Dark":"Light");assert(app.colors.edited&&dirty);
    assert(enabled(900+ZUI_FOOTER_APPLY)&&enabled(900+ZUI_FOOTER_RESET));
    assert(!meridian_open(&app,"devices.bluetooth"));                     /* drafts block silent opens */
    click_id(app.sidebar.base_id);assert(!app.dialog.open&&app.nav.mode==NAV_CATEGORIES);
    click_label("Devices");assert(app.dialog.open&&app.page==MERIDIAN_COLORS);
    key(0,1,0);assert(!app.dialog.open&&app.page==MERIDIAN_COLORS&&app.colors.edited);   /* Esc = Cancel */
    click_label("Devices");click_label("Discard");
    assert(!app.dialog.open&&app.page==page_id("devices.bluetooth")&&!app.colors.edited&&!dirty&&saves==0);
    assert(meridian_open(&app,"style.colors"));draw();click_label(saved.mode?"Dark":"Light");
    click_id(900+ZUI_FOOTER_APPLY);assert(saves==1&&!app.colors.edited&&!dirty);
    click_label(saved.mode?"Dark":"Light");click_id(app.sidebar.base_id);click_label("Devices");click_id(950+1);
    assert(saves==2&&app.page==page_id("devices.bluetooth")&&!dirty);       /* Apply then navigate */
    assert(meridian_open(&app,"style.colors"));draw();click_id(13);click_id(900+ZUI_FOOTER_APPLY);assert(saved.accent==3);
    click_id(900+ZUI_FOOTER_DEFAULTS);
    assert(app.colors.edited&&app.colors.mode==0&&app.colors.accent==0);click_id(900+ZUI_FOOTER_RESET);assert(!app.colors.edited);
    /* The materials moved to Application Style; Colors no longer shows them. A Colors draft carries over to
     * Application Style without the unsaved-changes dialog: both edit one record. */
    assert(find_id(54)<0);
    click_label(saved.mode?"Dark":"Light");assert(app.colors.edited);
    click_label("Application Style");assert(!app.dialog.open&&app.page==page_id("style.application")&&app.colors.edited);
    click_id(900+ZUI_FOOTER_RESET);assert(!app.colors.edited);
    assert(meridian_open(&app,"style.colors"));draw();
    assert(meridian_open(&app,"style.application"));draw();assert(find_id(10)<0);
    wheel_until(54);click_id(54);assert(app.colors.translucency==50&&app.colors.edited);click_id(900+ZUI_FOOTER_APPLY);assert(material_level==50);
    /* System defaults: combo choice, apply, stale revision, reset. */
    meridian_system_services(&app,read_preferences,save_preferences);
    assert(meridian_open(&app,"input.keyboard"));meridian_refresh(&app);draw();
    key(0,4,1);assert(app.ui.focus==find_id(2004));key(0,2,0);assert(app.combo.open);key(0,8,0);key(0,2,0);
    assert(!app.combo.open&&binding_changed(&app.bindings,BIND_KEYBOARD)&&dirty);
    key(0,4,0);assert(app.ui.focus==find_id(900+ZUI_FOOTER_APPLY));key(0,2,0);assert(preferences.keyboard==1&&!binding_changed(&app.bindings,BIND_KEYBOARD)&&!dirty);
    click_id(2004);draw();key(0,7,0);key(0,2,0);assert(binding_changed(&app.bindings,BIND_KEYBOARD));
    preferences.revision++;meridian_refresh(&app);click_id(900+ZUI_FOOTER_APPLY);
    assert(binding_changed(&app.bindings,BIND_KEYBOARD)&&app.bindings.message&&preferences.keyboard==1);
    click_id(900+ZUI_FOOTER_RESET);assert(!binding_changed(&app.bindings,BIND_KEYBOARD)&&!dirty);
    /* Close intent with a draft goes through the dialog. */
    click_id(2004);draw();key(0,7,0);key(0,2,0);
    key('q',0,2);assert(app.dialog.open&&!app.closing);key(0,1,0);assert(!app.dialog.open&&!app.closing);
    send((struct zenith_gui_event){Z_GUI_CLOSE_INTENT,0,0,0,0,0});assert(app.dialog.open);
    click_label("Discard");assert(app.closing&&!dirty);app.closing=0;
    preferences.flags=1;meridian_refresh(&app);draw();click_id(2004);assert(!app.combo.open);
    assert(find_id(4003)<0);                                            /* no portal: no Unlock */
    app.authorize=fake_authorize;app.authorization_poll=fake_poll;draw();
    click_id(4003);assert(app.unlock_handle==9&&!enabled(4003));
    meridian_refresh(&app);draw();assert(app.unlock_handle==9);       /* still shown */
    portal_state=4;meridian_refresh(&app);draw();
    assert(!app.unlock_handle&&strstr(app.unlock_note,"dismissed")&&enabled(4003));
    portal_state=3;click_id(4003);preferences.flags=3;meridian_refresh(&app);draw();
    assert(!app.unlock_handle&&!app.unlock_note[0]&&find_id(4003)<0&&enabled(2004));   /* approved: writable */
    app.authorize=0;app.authorization_poll=0;
    /* Pickers (call 669): wallpapers in Dynamic and Solid groups with desktop-drawn previews, cursor packs and sizes,
     * the second set of wallpapers beyond the 12 the snapshot names, chosen by pointer and applied in one footer. */
    preferences.version=2;preferences.wallpaper_count=12;preferences.wallpaper=0;preferences.cursor_count=2;preferences.cursor=0;
    for(int i=0;i<12;i++)strcpy(preferences.wallpapers[i],"w");
    preferences.session_count=1;strcpy(preferences.sessions[0],"de");strcpy(preferences.current_session,"de");
    meridian_style_services(&app,fake_catalog,fake_preview);
    assert(styles_field_ready(&app.styles,6)&&styles_field_ready(&app.styles,8)&&styles_field_ready(&app.styles,9)&&!styles_field_ready(&app.styles,10));
    assert(meridian_open(&app,"style.wallpaper"));draw();
    /* MERIDIAN_DUMP=DIR writes the wallpaper picker in each widget style as PPM, to look at the tiles. */
    if(getenv("MERIDIAN_DUMP"))for(int ws=0;ws<3;ws++){
        app.ui.widget_style=ws;app.ui.dirty=1;draw();
        char path[512];snprintf(path,sizeof(path),"%s/picker-%d.ppm",getenv("MERIDIAN_DUMP"),ws);
        FILE *f=fopen(path,"wb");assert(f);fprintf(f,"P6 %d %d 255\n",app.ui.width,app.ui.height);
        for(int i=0;i<app.ui.width*app.ui.height;i++){unsigned int c=app.ui.pixels[i];fputc(c>>16&255,f);fputc(c>>8&255,f);fputc(c&255,f);}
        fclose(f);
    }
    app.ui.widget_style=0;draw();
    assert(find_id(8384)>=0);assert(previews_drawn>=1);assert(app.ui.controls[find_id(8384)].value);
    int before=previews_drawn;draw();assert(previews_drawn==before);                 /* cached */
    /* The largest tile's picture (176x110 grown by 3 px a side) fits the thumbnail cache. */
    assert(styles_thumb(&app.styles,8,0,182,116,12345)!=0);
    wheel_until(8384+13);click_id(8384+13);assert(app.styles.draft[6]==13&&styles_changed(&app.styles,"style.wallpaper")&&dirty);
    click_id(900+ZUI_FOOTER_APPLY);assert(preferences.wallpaper==13&&!styles_changed(&app.styles,"style.wallpaper")&&!dirty);
    draw();assert(app.ui.controls[find_id(8384+13)].value);                           /* Tides, from the second set */
    wheel_until(8384+4);                                                             /* the solid colors follow */
    /* Cursors: pack and size on one page, one Apply; a draft blocks navigation until it is resolved. */
    assert(meridian_open(&app,"style.cursors"));draw();
    wheel_until(8512+4);click_id(8512+4);assert(app.styles.draft[8]==4);
    wheel_until(8576+2);click_id(8576+2);assert(app.styles.draft[9]==2);
    assert(!meridian_open(&app,"devices.bluetooth"));                                 /* drafts block silent opens */
    click_id(900+ZUI_FOOTER_APPLY);assert(cursor_pack==4&&cursor_size==2&&!dirty);
    click_id(8576+0);click_id(900+ZUI_FOOTER_RESET);assert(app.styles.draft[9]==2&&!dirty);
    /* System Sounds: level and per-event switches are drafts like the pack; Play previews at once. */
    meridian_sound_services(&app,fake_sounds,fake_sound_save,fake_sound_play);
    assert(meridian_open(&app,"audio.sounds"));draw();
    wheel_until(9002);click_id(9002);assert(app.styles.sound_level==80&&dirty);
    wheel_until(9109);click_id(9109);assert(app.styles.sound_mask&(1u<<9));
    wheel_until(9209);click_id(9209);assert(sound_played==9);
    click_id(900+ZUI_FOOTER_APPLY);assert(sound_level==80&&(sound_mask&(1u<<9))&&!dirty);
    meridian_sound_services(&app,0,0,0);
    /* Notifications: Do Not Disturb and the popup time are drafts; the test button posts at once. */
    meridian_notify_services(&app,fake_notify_read,fake_notify_save,fake_notify_post);
    assert(meridian_open(&app,"windows.notifications"));draw();
    click_id(9300);assert(app.styles.notify_dnd==1&&dirty);
    click_id(9312);assert(app.styles.notify_popup==2);
    wheel_until(9320);click_id(9320);assert(notify_posted);
    wheel_until(9343);click_id(9343);assert(app.styles.notify_corner==Z_NOTIFY_TOP_LEFT&&notify_corner==0);
    wheel_until(9332);click_id(9332);assert(!(app.styles.notify_flags&Z_NOTIFY_HOVER));
    wheel_until(9333);click_id(9333);assert(!(app.styles.notify_flags&Z_NOTIFY_SOUND));
    wheel_until(9330);click_id(9330);assert(!(app.styles.notify_flags&Z_NOTIFY_FULLSCREEN));
    wheel_until(9331);click_id(9331);assert(app.styles.notify_flags&Z_NOTIFY_FOCUSED_APP);
    click_id(900+ZUI_FOOTER_APPLY);assert(notify_dnd==1&&notify_popup==2&&notify_flags==Z_NOTIFY_FOCUSED_APP&&notify_corner==Z_NOTIFY_TOP_LEFT&&!dirty);
    click_id(9331);preferences.revision++;meridian_refresh(&app);draw();
    click_id(900+ZUI_FOOTER_APPLY);assert(dirty&&notify_flags==Z_NOTIFY_FOCUSED_APP&&strstr(app.styles.message,"Changed elsewhere"));
    click_id(900+ZUI_FOOTER_RESET);assert(!dirty&&app.styles.notify_flags==notify_flags);
    /* Per app: switching Orbit off is a draft until Apply, saved against the latest revision. */
    meridian_notify_senders(&app,fake_notify_senders,fake_notify_mute);draw();
    assert(app.styles.sender_count==2);wheel_until(9400);assert(app.ui.controls[find_id(9400)].value);
    click_id(9400);assert(app.styles.senders[0].draft==1&&dirty&&!muted_orbit);
    click_id(900+ZUI_FOOTER_APPLY);assert(muted_orbit==1&&!dirty);draw();assert(!app.ui.controls[find_id(9400)].value);
    meridian_notify_senders(&app,0,0);
    meridian_notify_services(&app,0,0,0);
    meridian_style_services(&app,0,0);preferences.wallpaper=0;preferences.cursor=0;cursor_pack=0;preferences.version=1;meridian_refresh(&app);
    /* Desktop forms use revisioned drafts and the same keyboard, focus, and Apply/Reset controls. */
    app.desktop.read=fake_desktop_read;app.desktop.save=fake_desktop_save;
    app.desktop.apps=fake_desktop_catalog;app.desktop.dirty=mark;
    assert(meridian_resize(&app,640,480,1920*1000));
    assert(meridian_open(&app,"windows.taskbar"));draw();
    /* The installed Settings gate changes visibility by keyboard at this size. */
    key(0,4,1);key(0,4,1);
    assert(app.ui.focus==find_label("Dock when maximized"));
    key(0,2,0);key(0,8,0);key(0,8,0);key(0,2,0);
    assert(dirty&&desktop_state[1].values[0]==0&&app.desktop.draft.values[0]==2);
    key(0,4,0);key(0,4,0);assert(app.ui.focus==find_label("Apply"));key(0,2,0);
    assert(!dirty&&desktop_state[1].values[0]==2);
    assert(meridian_resize(&app,1150,960,1920*1000));draw();
    click_label("Bottom");click_label("Top");
    click_label("Choose an application");click_label("Aurora");
    assert(dirty&&app.desktop.draft.values[1]==1&&app.desktop.draft.count==1);
    assert(!meridian_open(&app,"windows.behavior"));
    click_id(900+ZUI_FOOTER_APPLY);
    assert(!dirty&&desktop_state[1].values[1]==1&&!strcmp(desktop_state[1].entries[0].name,"Editor"));
    assert(meridian_open(&app,"windows.behavior"));draw();
    assert(app.desktop.saved.page==Z_DESKTOP_WINDOWS&&!desktop_action(&app.desktop,&app.ui,10241));
    click_label("Click to focus");click_label("Focus follows pointer");
    click_id(10219);assert(app.desktop.draft.values[1]==300);
    click_id(900+ZUI_FOOTER_APPLY);assert(desktop_state[2].values[0]==1&&desktop_state[2].values[1]==300&&!dirty);
    assert(meridian_open(&app,"windows.switcher"));draw();
    click_label("Cards");click_label("Compact list");
    click_label("Current desktop");click_label("All desktops");
    desktop_state[3].revision++;meridian_refresh(&app);draw();
    click_id(900+ZUI_FOOTER_APPLY);assert(dirty&&desktop_state[3].values[3]==0);
    click_id(900+ZUI_FOOTER_RESET);assert(!dirty);
    click_label("Cards");click_label("Compact list");
    click_label("Current desktop");click_label("All desktops");
    click_id(900+ZUI_FOOTER_APPLY);assert(!dirty&&desktop_state[3].values[0]==1&&desktop_state[3].values[3]==1);
    assert(meridian_open(&app,"windows.desktops"));draw();
    click_label("Add desktop");click_label("Desktop 2");
    key(0,3,0);type("B");key(0,2,0);
    click_id(10105); /* Move the second desktop up. */
    click_id(900+ZUI_FOOTER_APPLY);
    assert(!dirty&&desktop_state[4].count==2&&desktop_state[4].entries[0].id==1
           &&!strcmp(desktop_state[4].entries[0].name,"Desktop B"));
    click_id(10103);click_id(900+ZUI_FOOTER_APPLY);
    assert(!dirty&&desktop_state[4].count==1&&desktop_state[4].entries[0].id==0);
    /* Window Behavior on a WM-capable kernel saves both spacings with the existing fields. */
    tiling_supported=1;
    assert(meridian_open(&app,"windows.behavior"));draw();
    assert(app.desktop.saved.page==Z_DESKTOP_WINDOWS_TILING);
    click_label("Click to focus");click_label("Focus follows pointer");
    wheel_until(10243);assert(!enabled(10240)&&!enabled(10242));
    click_id(10241);click_id(10243);click_id(10243);
    assert(dirty&&app.desktop.draft.inner_gap==1&&app.desktop.draft.outer_gap==2);
    struct zenith_desktop_preferences *wm=&desktop_state[Z_DESKTOP_WINDOWS_TILING];
    assert(wm->inner_gap==0&&wm->outer_gap==0&&wm->values[0]==0);
    click_id(900+ZUI_FOOTER_APPLY);
    assert(!dirty&&wm->inner_gap==1&&wm->outer_gap==2&&wm->values[0]==1);
    click_id(10240);wm->inner_gap=31;wm->revision++;meridian_refresh(&app);draw();
    click_id(900+ZUI_FOOTER_APPLY);
    assert(dirty&&wm->inner_gap==31&&strstr(app.desktop.message,"changed elsewhere"));
    click_id(900+ZUI_FOOTER_RESET);assert(!dirty&&app.desktop.draft.inner_gap==31);
    click_id(10241);assert(!enabled(10241)&&app.desktop.draft.inner_gap==32&&app.desktop.draft.outer_gap==2);
    click_id(10242);assert(app.desktop.draft.outer_gap==1&&wm->outer_gap==2);
    click_id(900+ZUI_FOOTER_DEFAULTS);click_id(900+ZUI_FOOTER_APPLY);
    assert(!dirty&&wm->inner_gap==0&&wm->outer_gap==0&&wm->values[0]==0);
    wm->flags=0;meridian_refresh(&app);draw();
    assert(!enabled(10241)&&!enabled(10243)&&!desktop_action(&app.desktop,&app.ui,10241)&&!dirty);
    assert(meridian_resize(&app,640,480,1920*1000));
    /* Service-backed information pages. */
    meridian_information_services(&app,information_read);
    assert(meridian_open(&app,"network.connections"));meridian_refresh(&app);draw();
    assert(strstr(app.public_information,"10.0.2.15")&&!dirty);
    assert(meridian_open(&app,"system.users"));meridian_refresh(&app);draw();assert(app.public_kind==2&&strstr(app.public_information,"uid=1000"));
    /* Network page over call 667: live state, manual settings with field keys, a refused gateway, locked changes. */
    meridian_network_services(&app,fake_network);
    assert(meridian_open(&app,"network.connections"));meridian_refresh(&app);draw();
    assert(app.net_ok);
    wheel_until(7021);assert(find_label("Disconnect")>=0);wheel_until(7001);   /* the controls sit below the connection details */
    assert(!enabled(7020));                                             /* nothing changed: Apply is off */
    click_label("Manual");assert(app.net_mode==1&&app.net_field==0);wheel_until(7020);assert(enabled(7020));
    assert(!strcmp(app.net_text[0],"10.0.2.15")&&!strcmp(app.net_text[1],"24")&&!strcmp(app.net_text[2],"10.0.2.2"));
    key(0,4,0);key(0,4,0);assert(app.net_field==2);                  /* Tab to the gateway */
    key(0,3,0);                                                         /* Backspace edits the field, not the page */
    assert(app.page==find_page_index("network.connections"));
    key('9',0,0);key('9',0,0);assert(!strcmp(app.net_text[2],"10.0.2.99"));
    key(0,2,0);assert(net_applies==1&&strstr(app.net_message,"previous settings are back"));
    for(int i=0;i<2;i++){key(0,3,0);}
    key('2',0,0);key(0,2,0);
    assert(net_applies==2&&net_last.gateway[3]==2&&net_state.mode==1&&strstr(app.net_message,"active"));
    key(0,1,0);assert(app.net_field==-1);
    net_locked=1;wheel_until(7000);click_label("Automatic");reveal(7020);click_id(7020);assert(app.net_locked&&strstr(app.net_message,"administrator"));
    net_locked=0;reveal(7020);click_id(7020);assert(net_state.mode==0&&!app.net_locked);
    for(int i=0;i<40;i++)send((struct zenith_gui_event){Z_GUI_SCROLL,120,400,300,0,0});
    reveal(7021);click_label("Disconnect");assert(net_state.state==Z_NET_OFF);meridian_refresh(&app);draw();assert(find_label("Connect")>=0);
    click_label("Connect");assert(net_state.state==Z_NET_ONLINE);
    app.network=0;
    /* Rapid extreme wheel input stays within both scroll ranges. */
    assert(meridian_open(&app,"devices.bluetooth"));draw();click_id(app.sidebar.base_id);
    for(int i=0;i<2000;i++){
        send((struct zenith_gui_event){Z_GUI_SCROLL,i%2?INT_MIN:INT_MAX,i%4<2?100:400,300,0,0});
        assert(app.sidebar.scroll.offset>=0&&app.sidebar.scroll.offset<=zui_scroll_maximum(&app.sidebar.scroll));
        assert(app.page_scroll.offset>=0&&app.page_scroll.offset<=zui_scroll_maximum(&app.page_scroll));
    }
    /* Drafts survive resize. */
    assert(meridian_open(&app,"style.colors"));draw();click_label(saved.mode?"Dark":"Light");
    assert(meridian_resize(&app,1920,1000,1920*1000));draw();assert(app.colors.edited);
    assert(meridian_resize(&app,600,480,1920*1000));draw();assert(app.colors.edited);
    click_id(900+ZUI_FOOTER_RESET);assert(!app.colors.edited&&!dirty);       /* footer fits the minimum window */
    /* Material frames keep controls opaque. */
    assert(meridian_resize(&app,640,480,1920*1000));
    material_saved=7;meridian_refresh(&app);app.ui.foreground=1;draw();
    int apply=find_id(900+ZUI_FOOTER_APPLY);assert(apply>=0);struct zui_rect r=zui_control_area(&app.ui,apply);
    assert(app.ui.pixels[(r.y+r.h/2)*app.ui.width+r.x+4]>>24==255);
    for(int i=0;i<app.ui.width*app.ui.height;i++){unsigned int c=app.ui.pixels[i],a=c>>24;assert((c&255)<=a&&((c>>8)&255)<=a&&((c>>16)&255)<=a);}
    /* The host tints materials with the active theme, so a preview of the other mode paints its own opaque surfaces. */
    assert(app.ui.material_count>0);
    unsigned int live_mode=app.colors.mode;
    settings_preview(&app.colors,!live_mode,app.colors.accent,app.colors.effects,app.colors.translucency);meridian_refresh(&app);draw();
    assert(app.ui.material_count==0);assert(app.ui.pixels[100*app.ui.width+4]>>24==255);
    settings_reset(&app.colors);meridian_refresh(&app);draw();
    assert(app.colors.mode==live_mode&&app.ui.material_count>0);
    app.ui.foreground=0;material_saved=0;meridian_refresh(&app);
    /* The real display binding starts a host preview; the shared modal stays in
       the viewport even when the form itself scrolls. Keep and Escape use tokens. */
    display=(struct zenith_display){.version=1,.flags=3,.revision=1,.width=1024,.height=768,.scale=1,.mode_count=2,.driver="VMWareSVGAII",.modes={{1024,768},{1600,1200}}};
    app.bindings.display_read=read_display;app.bindings.display_save=change_display;
    meridian_refresh(&app);assert(meridian_open(&app,"display.configuration"));draw();
    assert(enabled(2004)&&enabled(2006));key(0,4,1);key(0,4,1);key(0,2,0);key(0,11,0);key(0,2,0);
    assert(binding_choice(&app.bindings,BIND_DISPLAY)==1&&app.bindings.display_choice==1&&dirty);   /* 1600x1200 at 100% */
    key(0,4,0);key(0,2,0);key(0,11,0);key(0,2,0);                                                   /* Scale: 200% */
    assert(binding_scale_choice(&app.bindings)==1&&app.bindings.display_choice==2);key(0,4,0);key(0,2,0);
    assert(display.phase==1&&dirty&&display_actions==1);
    display.phase=2;display.seconds=15;display.revision++;meridian_refresh(&app);draw();
    assert(app.display_dialog.open&&enabled(4101)&&enabled(4102));
    struct zui_rect keep=zui_control_area(&app.ui,find_id(4101));assert(keep.y>=0&&keep.y+keep.h<=app.ui.height);
    click_id(4101);meridian_refresh(&app);draw();assert(!display.phase&&!dirty&&!app.display_dialog.open&&display_actions==2);
    display.phase=2;display.flags|=4;display.seconds=9;display.token=77;display.revision++;meridian_refresh(&app);draw();
    key(0,1,0);meridian_refresh(&app);draw();assert(!display.phase&&display_actions==3);
    /* A kernel with filtered fractional scales (flag 8) offers 125-175% where the logical size stays 800x600 or more. */
    display=(struct zenith_display){.version=1,.flags=3|8,.revision=9,.width=1600,.height=1200,.scale=1,.percent=150,.mode_count=2,.driver="VMWareSVGAII",.modes={{1024,768},{1600,1200}}};
    meridian_refresh(&app);assert(app.bindings.display_count==7);   /* 1024x768 at 100/125%, 1600x1200 at 100-200% */
    assert(!strcmp(app.bindings.display_labels[4],"1600 x 1200 (150%)")&&app.bindings.display_scales[4]==150);
    display.flags=3;display.revision++;meridian_refresh(&app);assert(app.bindings.display_count==3);

    /* H2: real widget events on the three Sound pages, stale stream identity and absent hardware. */
    assert(meridian_open(&app,"audio.playback"));meridian_audio_services(&app,read_audio,change_audio);draw();
    assert(find_label("Test")>=0);click_id(5002);assert(audio_state.devices[0].volume==85);
    click_id(5003);assert(audio_state.devices[0].flags&1);click_id(5004);
    assert(meridian_open(&app,"audio.recording"));meridian_refresh(&app);draw();click_id(5009);assert(audio_state.devices[1].volume==75);
    assert(meridian_open(&app,"audio.streams"));meridian_refresh(&app);draw();click_id(6001);assert(audio_state.streams[0].volume==65);
    click_id(6003);assert(audio_state.streams[0].flags==1);
    audio_state.stream_count=0;meridian_refresh(&app);draw();assert(find_id(6003)<0);
    audio_state.available=0;assert(meridian_open(&app,"audio.playback"));meridian_refresh(&app);draw();assert(find_label("Test")<0);
    audio_state.available=1;audio_state.stream_count=1;
    if(argc==2){
        const char *shots[]={"quick.overview","style.colors","input.keyboard","network.connections","devices.bluetooth","audio.playback","audio.recording","audio.streams"};
        for(int s=0;s<8;s++){
            assert(meridian_open(&app,shots[s]));meridian_refresh(&app);draw();
            char path[1024];snprintf(path,sizeof(path),"%s/meridian-%s.ppm",argv[1],shots[s]);
            FILE *f=fopen(path,"wb");assert(f);fprintf(f,"P6\n%d %d\n255\n",app.ui.width,app.ui.height);
            for(int i=0;i<app.ui.width*app.ui.height;i++){unsigned char b[3]={app.ui.pixels[i]>>16,app.ui.pixels[i]>>8,app.ui.pixels[i]};fwrite(b,1,3,f);}
            fclose(f);
        }
    }
    puts("PASS Meridian stage 2: drill-down, search, form pages, footer, dialog, combo defaults and material layouts");
    return 0;
}
