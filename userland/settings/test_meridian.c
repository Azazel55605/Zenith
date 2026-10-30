/* Host tests for the stage-2 Meridian shell: real clicks/keys over the shared widgets. */
#include "meridian.h"
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include <limits.h>
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
static struct zenith_system_settings preferences={.version=1,.flags=3,.revision=1,.keyboard_count=2,.zone_count=2,
    .current_zone="UTC",.keyboards={"us","de"},.zones={"UTC","Europe/Berlin"}};
static long read_preferences(struct zenith_system_settings *s){*s=preferences;return 0;}
static long save_preferences(unsigned int field,unsigned int choice,unsigned long revision){
    if(!(preferences.flags&2))return -13;
    if(revision!=preferences.revision)return -11;
    if(field==1)preferences.keyboard=choice;
    else if(field==2){preferences.zone=choice;strcpy(preferences.current_zone,preferences.zones[choice]);}
    preferences.revision++;return 0;
}
static long information_read(unsigned int kind,struct zenith_settings_information *s){
    *s=(struct zenith_settings_information){.version=1,.kind=kind};
    const char *text=kind==1?"address=10.0.2.15\ngateway=10.0.2.2\n":"user uid=1000\n";
    s->length=(unsigned int)strlen(text);strcpy(s->text,text);return 0;
}
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
    wheel_until(54);assert(app.page_scroll.offset>0);click_id(54);assert(app.colors.translucency==50&&app.colors.edited);click_id(900+ZUI_FOOTER_APPLY);assert(material_level==50);
    /* System defaults: combo choice, apply, stale revision, reset. */
    meridian_system_services(&app,read_preferences,save_preferences);
    assert(meridian_open(&app,"input.keyboard"));meridian_refresh(&app);draw();
    click_id(2004);assert(app.combo.open);draw();key(0,8,0);key(0,2,0);
    assert(!app.combo.open&&binding_changed(&app.bindings,BIND_KEYBOARD)&&dirty);
    click_id(900+ZUI_FOOTER_APPLY);assert(preferences.keyboard==1&&!binding_changed(&app.bindings,BIND_KEYBOARD)&&!dirty);
    click_id(2004);draw();key(0,7,0);key(0,2,0);assert(binding_changed(&app.bindings,BIND_KEYBOARD));
    preferences.revision++;meridian_refresh(&app);click_id(900+ZUI_FOOTER_APPLY);
    assert(binding_changed(&app.bindings,BIND_KEYBOARD)&&app.bindings.message&&preferences.keyboard==1);
    click_id(900+ZUI_FOOTER_RESET);assert(!binding_changed(&app.bindings,BIND_KEYBOARD)&&!dirty);
    /* Close intent with a draft goes through the dialog. */
    click_id(2004);draw();key(0,7,0);key(0,2,0);
    key('q',0,2);assert(app.dialog.open&&!app.closing);key(0,1,0);assert(!app.dialog.open&&!app.closing);
    send((struct zenith_gui_event){Z_GUI_CLOSE_INTENT,0,0,0,0,0});assert(app.dialog.open);
    click_label("Discard");assert(app.closing&&!dirty);app.closing=0;
    preferences.flags=1;meridian_refresh(&app);draw();click_id(2004);assert(!app.combo.open);preferences.flags=3;meridian_refresh(&app);
    /* Service-backed information pages. */
    meridian_information_services(&app,information_read);
    assert(meridian_open(&app,"network.connections"));meridian_refresh(&app);draw();
    assert(strstr(app.public_information,"10.0.2.15")&&!dirty);
    assert(meridian_open(&app,"system.users"));meridian_refresh(&app);draw();assert(app.public_kind==2&&strstr(app.public_information,"uid=1000"));
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
    app.ui.foreground=0;material_saved=0;meridian_refresh(&app);
    if(argc==2){
        const char *shots[]={"quick.overview","style.colors","input.keyboard","network.connections","devices.bluetooth"};
        for(int s=0;s<5;s++){
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
