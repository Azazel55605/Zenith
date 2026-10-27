#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "app.h"
static unsigned int pixels[600*480];
static struct zenith_appearance state;
static struct settings_app app;
static long failure;
static int guarded;
static long read_state(struct zenith_appearance *s){*s=state;return 0;}
static long save_state(unsigned int m,unsigned int a,unsigned long revision){
    if(failure)return failure;
    if(revision!=state.revision)return -11;
    state.mode=m;state.accent=a;state.revision++;return 0;
}
static long dirty_state(int dirty){guarded=dirty;return 0;}
static void event(int type,int code,int x,int y,int extra){
    struct zenith_gui_event e={type,code,x,y,0,extra};settings_event(&app,&e);
    if(app.ui.dirty)settings_draw(&app);
}
static void click(int x,int y){event(Z_GUI_DOWN,1,x,y,0);event(Z_GUI_UP,1,x,y,0);}
int main(void){
    struct zui ui;assert(zui_init(&ui,pixels,600,480));
    for(int m=0;m<2;m++)for(int a=0;a<6;a++){
        zui_appearance(&ui,m,a);memcpy(state.palette[m][a],&ui.theme,sizeof(ui.theme));
    }
    state.version=1;state.flags=3;state.revision=1;state.active_revision=1;
    struct settings_backend backend={read_state,save_state,dirty_state};
    assert(settings_init(&app,pixels,backend));settings_draw(&app);
    event(Z_GUI_FOCUS,1,0,0,0);click(200,120);assert(app.edited&&guarded&&app.mode==1);
    failure=-5;click(70,438);assert(app.edited&&guarded&&state.mode==0);
    assert(strstr(app.message,"Save failed"));
    event(Z_GUI_CLOSE_INTENT,0,0,0,0);assert(app.prompt==1&&!app.closing);
    click(70,438);assert(!app.prompt&&app.edited&&!app.closing); // Keep editing.
    state.revision++;settings_refresh(&app);assert(app.edited&&app.mode==1&&app.base_revision==1);
    failure=0;click(70,438);assert(app.edited&&strstr(app.message,"Changed elsewhere"));
    click(230,438);assert(app.prompt==2);click(230,438);assert(!app.prompt&&!app.edited&&!guarded&&app.mode==0);
    click(200,120);click(70,438);assert(!app.edited&&!guarded&&state.mode==1);
    click(530,202);assert(app.edited&&app.accent==5);
    event(Z_GUI_KEY,0,0,0,1);assert(app.prompt==1&&!app.closing);
    click(230,438);assert(app.closing&&!guarded&&state.accent==0);
    state.flags=1;assert(settings_init(&app,pixels,backend));settings_draw(&app);
    event(Z_GUI_FOCUS,1,0,0,0);click(70,120);assert(!app.edited); // Read-only users cannot edit.
    puts("PASS Settings save failure, stale edits, explicit reload/discard, chrome intent and readonly controls");
    return 0;
}
