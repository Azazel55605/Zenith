#include "meridian.h"
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include <limits.h>
static struct zenith_appearance saved;
static int saves,dirty;
static unsigned int material_saved;
static unsigned int material_level=40;
static long read_state(struct zenith_appearance *s,unsigned int *materials,unsigned int *effective,unsigned int *available,unsigned int *reason,unsigned int *level){*level=material_level;*s=saved;*materials=*effective=material_saved;*available=7;*reason=0;return 0;}
static long save(unsigned int m,unsigned int a,unsigned int materials,unsigned int level,unsigned long revision){assert(level<=100);assert(revision==saved.revision);material_level=level;material_saved=materials;saved.mode=m;saved.accent=a;saved.revision++;saves++;return 0;}
static long mark(int d){dirty=d;return 0;}
static struct meridian app;
static unsigned int pixels[600*480];
static void click(int x,int y){struct zenith_gui_event e={Z_GUI_DOWN,0,x,y,0,0};meridian_event(&app,&e);meridian_draw(&app);e.type=Z_GUI_UP;meridian_event(&app,&e);meridian_draw(&app);}
static void activate(int id){struct zui *u=meridian_ui(&app);for(int i=0;i<u->count;i++)if(u->controls[i].id==id){struct zui_rect r=u->controls[i].rect;click(r.x+r.w/2,r.y+r.h/2);return;}assert(!"missing control");}
static int page_id(const char *id){for(int i=0;i<43;i++)if(!strcmp(id,meridian_pages[i].id))return i;assert(0);return 0;}
static void key(int code,int extra,int mods){struct zenith_gui_event e={Z_GUI_KEY,code,0,0,mods,extra};meridian_event(&app,&e);meridian_draw(&app);}
static struct zenith_system_settings preferences={.version=1,.flags=3,.revision=1,.keyboard_count=2,.zone_count=2,.keyboard=0,.zone=0,.current_zone="UTC",.keyboards={"us","de"},.zones={"UTC","Europe/Berlin"}};
static long read_preferences(struct zenith_system_settings *s){*s=preferences;return 0;}
static long save_preferences(unsigned int field,unsigned int choice,unsigned long revision){
    if(!(preferences.flags&2))return -13;
    if(revision!=preferences.revision)return -11;
    if(field==1)preferences.keyboard=choice;
    else {preferences.zone=choice;strcpy(preferences.current_zone,preferences.zones[choice]);}
    preferences.revision++;return 0;
}
int main(int argc,char **argv){
    saved.version=1;saved.revision=1;saved.flags=3;
    struct zui palette;zui_init(&palette,pixels,600,480);
    for(int m=0;m<2;m++)for(int a=0;a<6;a++){zui_appearance(&palette,m,a);memcpy(saved.palette[m][a],&palette.theme,sizeof(palette.theme));}
    assert(meridian_init(&app,pixels,(struct settings_backend){read_state,save,mark},0));meridian_refresh(&app);
    struct zenith_gui_event e={Z_GUI_FOCUS,1,0,0,0,0};meridian_event(&app,&e);meridian_draw(&app);
    for(int i=0;i<43;i++){assert(meridian_open(&app,meridian_pages[i].id));meridian_draw(&app);assert(app.page==i);assert(meridian_ui(&app)->count<=32);}
    assert(saves==0&&!dirty);assert(!meridian_open(&app,"missing.page"));
    assert(meridian_open(&app,"style.colors"));meridian_draw(&app);activate(2);assert(app.colors.edited&&dirty);
    activate(1101);assert(app.colors.prompt==3&&app.page==16);activate(90);assert(app.colors.edited&&!app.colors.prompt);
    activate(1101);activate(91);assert(app.page==-1&&!app.colors.edited&&!dirty&&saves==0&&app.category==1);
    key('f',0,2);for(const char *p="bluetooth";*p;p++)key(*p,0,0);
    assert(app.match_count==1);assert(strcmp(meridian_pages[app.matches[0]].id,"devices.bluetooth")==0);
    key(0,2,0);activate(100+page_id("devices.bluetooth"));assert(strcmp(meridian_pages[app.page].id,"devices.bluetooth")==0);
    assert(meridian_open(&app,"style.colors"));meridian_draw(&app);activate(2);activate(30);assert(saves==1&&!dirty);
    activate(1101);assert(app.category==1&&app.match_count==5);activate(300);assert(app.group==0&&app.match_count==2);
    activate(100+page_id("input.touchpad"));assert(strcmp(meridian_pages[app.page].id,"input.touchpad")==0);
    key(0,1,0);assert(app.page==-1&&app.group==0);key(0,1,0);assert(app.group==-1);
    for(int i=0;i<2000;i++){
        e=(struct zenith_gui_event){Z_GUI_SCROLL,i%2?INT_MIN:INT_MAX,300,200,0,0};meridian_event(&app,&e);meridian_draw(&app);
        assert(app.offset>=0&&app.offset<43&&meridian_ui(&app)->count<=32);
    }
    assert(!meridian_resize(&app,INT_MAX,INT_MAX,600*480));
    assert(meridian_resize(&app,600,480,600*480));
    material_saved=7;meridian_refresh(&app);assert(meridian_open(&app,"style.colors"));
    app.colors.ui.foreground=app.navigation.foreground=1;meridian_draw(&app);
    assert(app.colors.ui.material_count==1&&pixels[0]==0);
    assert(pixels[100*600+203]==0); /* no rectangular detail underlay */
    assert(pixels[12*600+204]==0); /* rounded detail corner reveals base material */
    for(int i=0;i<600*480;i++){unsigned int c=pixels[i],alpha=c>>24;assert((c&255)<=alpha&&((c>>8)&255)<=alpha&&((c>>16)&255)<=alpha);}
    assert(pixels[430*600+230]>>24==255); /* controls remain opaque */
    static unsigned int large[1920*1000];app.colors.ui.pixels=app.navigation.pixels=large;
    activate(saved.mode==0?2:1);assert(app.colors.edited&&dirty);unsigned long draft_revision=app.colors.base_revision;
    assert(meridian_resize(&app,1280,692,1920*1000));meridian_draw(&app);assert(app.colors.ui.width==1280&&app.colors.ui.height==692);
    assert(app.colors.edited&&dirty&&app.colors.base_revision==draft_revision);
    assert(saves==1);activate(1101);activate(91);assert(!app.colors.edited&&!dirty);
    key('f',0,2);assert(meridian_resize(&app,1920,1000,1920*1000));meridian_draw(&app);assert(app.navigation.count<=32);
    app.colors.ui.pixels=app.navigation.pixels=pixels;material_saved=0;meridian_refresh(&app);
    assert(meridian_resize(&app,600,480,600*480));app.colors.ui.foreground=app.navigation.foreground=0;
    /* The same UI uses bounded typed catalogues, stale-edit rejection and close intent. */
    meridian_system_services(&app,read_preferences,save_preferences);
    assert(meridian_open(&app,"input.keyboard"));meridian_refresh(&app);meridian_draw(&app);
    activate(2001);assert(app.preference_edited&&dirty&&app.preference_choice==1);
    key('f',0,2);assert(app.page==page_id("input.keyboard")&&app.preference_edited);
    activate(2002);assert(!app.preference_edited&&!dirty&&preferences.keyboard==1);
    assert(meridian_open(&app,"locale.time"));meridian_refresh(&app);assert(!app.preference_message);meridian_draw(&app);activate(2001);assert(app.preference_choice==1);
    preferences.revision++;meridian_refresh(&app);activate(2002);assert(app.preference_edited&&dirty);
    activate(2003);assert(!dirty&&!app.preference_edited);
    activate(2001);key('q',0,2);assert(app.preference_prompt==1&&!app.closing);
    activate(2010);assert(!app.preference_prompt&&app.preference_edited);
    key(0,1,0);assert(app.preference_prompt==2);activate(2011);assert(app.page==-1&&!dirty);
    preferences.flags=1;assert(meridian_open(&app,"input.keyboard"));meridian_refresh(&app);meridian_draw(&app);
    unsigned int old=app.preference_choice;activate(2001);assert(app.preference_choice==old&&!dirty);
    assert(meridian_open(&app,"style.colors"));meridian_refresh(&app);meridian_draw(&app);activate(54);assert(app.colors.translucency==50&&app.colors.edited);
    activate(30);assert(material_level==50&&!dirty);
    struct zui_input input;char bytes[16]="A\xc3\xa9";assert(zui_input_init(&input,bytes,sizeof(bytes)));
    e=(struct zenith_gui_event){Z_GUI_KEY,0,0,0,0,3};assert(zui_input_event(&input,&e));assert(strcmp(bytes,"A")==0);
    e.code=0x1f600;e.extra=0;assert(zui_input_event(&input,&e));assert(input.length==5);e.code=0;e.extra=3;zui_input_event(&input,&e);assert(strcmp(bytes,"A")==0);
    zui_fill(&app.navigation,(struct zui_rect){0,0,600,480},0x112233);
    zui_clip(&app.navigation,(struct zui_rect){10,10,2,2});
    zui_input_draw(&app.navigation,(struct zui_rect){0,0,500,40},&input,1);
    zui_paragraph(&app.navigation,(struct zui_rect){0,0,500,40},"bounded text",&zui_body,0xffffff,INT_MIN);
    assert(pixels[0]==0x112233&&pixels[600*9+10]==0x112233);
    zui_input_draw(&app.navigation,(struct zui_rect){INT_MAX,INT_MIN,INT_MAX,1},&input,1);
    assert(!zui_paragraph(&app.navigation,(struct zui_rect){INT_MAX,0,INT_MAX,10},"x",&zui_body,0xffffff,INT_MAX));
    zui_clip(&app.navigation,(struct zui_rect){0,0,600,480});
    if(argc==2){
        assert(meridian_open(&app,"quick.overview"));meridian_refresh(&app);meridian_draw(&app);
        char path[1024];snprintf(path,sizeof(path),"%s/meridian.ppm",argv[1]);FILE *f=fopen(path,"wb");assert(f);fprintf(f,"P6\n600 480\n255\n");
        for(int i=0;i<600*480;i++){unsigned char b[3]={pixels[i]>>16,pixels[i]>>8,pixels[i]};fwrite(b,1,3,f);}fclose(f);
        for(int mode=0;mode<2;mode++){
            saved.mode=mode;saved.revision++;assert(meridian_open(&app,"style.colors"));meridian_refresh(&app);meridian_draw(&app);
            snprintf(path,sizeof(path),"%s/meridian-colors-%d.ppm",argv[1],mode);f=fopen(path,"wb");assert(f);fprintf(f,"P6\n600 480\n255\n");
            for(int i=0;i<600*480;i++){unsigned char b[3]={pixels[i]>>16,pixels[i]>>8,pixels[i]};fwrite(b,1,3,f);}fclose(f);
        }
    }
    puts("PASS all 43 Meridian pages, search, real pointer capture, navigation discard, save isolation and UTF-8 editing");return 0;
}
