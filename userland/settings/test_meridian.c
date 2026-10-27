#include "meridian.h"
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include <limits.h>
static struct zenith_appearance saved;
static int saves,dirty;
static long read_state(struct zenith_appearance *s){*s=saved;return 0;}
static long save(unsigned int m,unsigned int a,unsigned long revision){assert(revision==saved.revision);saved.mode=m;saved.accent=a;saved.revision++;saves++;return 0;}
static long mark(int d){dirty=d;return 0;}
static struct meridian app;
static unsigned int pixels[600*480];
static void click(int x,int y){struct zenith_gui_event e={Z_GUI_DOWN,0,x,y,0,0};meridian_event(&app,&e);meridian_draw(&app);e.type=Z_GUI_UP;meridian_event(&app,&e);meridian_draw(&app);}
static void key(int code,int extra,int mods){struct zenith_gui_event e={Z_GUI_KEY,code,0,0,mods,extra};meridian_event(&app,&e);meridian_draw(&app);}
int main(int argc,char **argv){
    saved.version=1;saved.revision=1;saved.flags=3;
    struct zui palette;zui_init(&palette,pixels,600,480);
    for(int m=0;m<2;m++)for(int a=0;a<6;a++){zui_appearance(&palette,m,a);memcpy(saved.palette[m][a],&palette.theme,sizeof(palette.theme));}
    assert(meridian_init(&app,pixels,(struct settings_backend){read_state,save,mark},0));meridian_refresh(&app);
    struct zenith_gui_event e={Z_GUI_FOCUS,1,0,0,0,0};meridian_event(&app,&e);meridian_draw(&app);
    for(int i=0;i<43;i++){assert(meridian_open(&app,meridian_pages[i].id));meridian_draw(&app);assert(app.page==i);assert(meridian_ui(&app)->count<=32);}
    assert(saves==0&&!dirty);assert(!meridian_open(&app,"missing.page"));
    assert(meridian_open(&app,"style.colors"));meridian_draw(&app);click(160,120);assert(app.colors.edited&&dirty);
    click(500,28);assert(app.colors.prompt==3&&app.page==16);click(60,436);assert(app.colors.edited&&!app.colors.prompt);
    click(500,28);click(210,436);assert(app.page==-1&&!app.colors.edited&&!dirty&&saves==0);
    key('f',0,2);for(const char *p="bluetooth";*p;p++)key(*p,0,0);
    assert(app.match_count==1);assert(strcmp(meridian_pages[app.matches[0]].id,"devices.bluetooth")==0);
    key(0,2,0);click(300,130);assert(strcmp(meridian_pages[app.page].id,"devices.bluetooth")==0);
    assert(meridian_open(&app,"style.colors"));meridian_draw(&app);click(160,120);click(70,435);assert(saves==1&&!dirty);
    key('f',0,2);click(500,76);click(110,154);assert(app.category==1&&app.match_count==5);
    click(300,130);assert(app.group==0&&app.match_count==2);
    click(300,174);assert(strcmp(meridian_pages[app.page].id,"input.touchpad")==0);
    click(500,28);assert(app.page==-1&&app.group==0);key(0,1,0);assert(app.group==-1);
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
    }
    puts("PASS all 43 Meridian pages, search, real pointer capture, navigation discard, save isolation and UTF-8 editing");return 0;
}
