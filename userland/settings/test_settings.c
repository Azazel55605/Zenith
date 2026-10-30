#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "app.h"
static unsigned int pixels[600*480];
static struct zenith_appearance state;
static struct settings_app app;
static long failure;
static int guarded;
static unsigned int material_saved;
static unsigned int material_level=40;
static long read_state(struct zenith_appearance *s,unsigned int *saved,unsigned int *effective,unsigned int *available,unsigned int *reason,unsigned int *level){*level=material_level;*s=state;*saved=material_saved;*effective=(material_saved&1)?material_saved:0;*available=7;*reason=0;return 0;}
static long save_state(unsigned int m,unsigned int a,unsigned int materials,unsigned int level,unsigned long revision){assert(level<=100);
    if(failure)return failure;
    if(revision!=state.revision)return -11;
    material_level=level;material_saved=materials;state.mode=m;state.accent=a;state.revision++;return 0;
}
static long dirty_state(int dirty){guarded=dirty;return 0;}
static void event(int type,int code,int x,int y,int extra){
    struct zenith_gui_event e={type,code,x,y,0,extra};settings_event(&app,&e);
    if(app.ui.dirty)settings_draw(&app);
}
static void click(int x,int y){event(Z_GUI_DOWN,1,x,y,0);event(Z_GUI_UP,1,x,y,0);}
static void activate(int id){for(int i=0;i<app.ui.count;i++)if(app.ui.controls[i].id==id){struct zui_rect r=app.ui.controls[i].rect;click(r.x+r.w/2,r.y+r.h/2);return;}assert(0);}
int main(int argc,char **argv){
    struct zui ui;assert(zui_init(&ui,pixels,600,480));
    for(int m=0;m<2;m++)for(int a=0;a<6;a++){
        zui_appearance(&ui,m,a);memcpy(state.palette[m][a],&ui.theme,sizeof(ui.theme));
    }
    state.version=1;state.flags=3;state.revision=1;state.active_revision=1;
    struct settings_backend backend={read_state,save_state,dirty_state,0};
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
    activate(15);assert(app.edited&&app.accent==5);
    event(Z_GUI_KEY,0,0,0,1);assert(app.prompt==1&&!app.closing);
    click(230,438);assert(app.closing&&!guarded&&state.accent==0);
    state.flags=1;assert(settings_init(&app,pixels,backend));settings_draw(&app);
    event(Z_GUI_FOCUS,1,0,0,0);click(70,120);assert(!app.edited); // Read-only users cannot edit.
    state.mode=0;state.palette[0][3][6]=0x987654;
    assert(settings_init(&app,pixels,backend));
    assert(zui_accent_color(&app.ui,3)==0x987654); /* package-provided swatches */
    struct zui original;assert(zui_init(&original,pixels,600,480));zui_appearance(&original,0,3);
    state.palette[0][3][6]=original.theme.accent;
    state.flags=3;material_saved=0;assert(settings_init(&app,pixels,backend));settings_draw(&app);
    event(Z_GUI_FOCUS,1,0,0,0);activate(50);activate(51);assert(app.effects==3&&app.edited&&guarded);
    click(70,438);assert(material_saved==3&&!app.edited&&!guarded);
    activate(50);assert(app.effects==2&&app.edited);click(70,438);assert(material_saved==2&&!app.edited);
    activate(52);assert(app.effects==6&&app.edited);activate(30);assert(material_saved==6);
    activate(50);activate(30);assert(material_saved==7&&app.ui.full_background);
    state.flags=1;assert(settings_init(&app,pixels,backend));settings_draw(&app);event(Z_GUI_FOCUS,1,0,0,0);
    activate(50);assert(app.effects==7&&!app.edited);material_saved=0;
    if(argc==2){
        for(int mode=0;mode<2;mode++)for(int accent=0;accent<6;accent++){
            state.mode=mode;state.accent=accent;state.flags=3;
            assert(settings_init(&app,pixels,backend));settings_draw(&app);
            char path[4096];snprintf(path,sizeof(path),"%s/appearance-%d-%d.ppm",argv[1],mode,accent);
            FILE *f=fopen(path,"wb");assert(f);fprintf(f,"P6\n600 480\n255\n");
            for(int i=0;i<600*480;i++){unsigned char b[]={pixels[i]>>16,pixels[i]>>8,pixels[i]};assert(fwrite(b,1,3,f)==3);}assert(!fclose(f));
        }
    }
    { /* drawing-free model API used by Meridian stage 2 */
        state.flags=3;state.mode=0;state.accent=0;state.revision=7;material_saved=0;material_level=40;failure=0;
        assert(settings_init(&app,pixels,backend));assert(settings_at_defaults(&app));
        assert(!settings_preview(&app,1,2,0,40)&&app.edited&&guarded&&app.mode==1&&app.accent==2);
        assert(!settings_at_defaults(&app));
        assert(!settings_preview(&app,0,0,0,40)&&!app.edited&&!guarded);      /* back to saved */
        assert(settings_preview(&app,0,0,0,101)==-22&&!app.edited);
        assert(!settings_preview(&app,1,4,1,60)&&app.edited);
        struct zui themed;assert(zui_init(&themed,pixels,600,480));settings_theme(&app,&themed);
        assert(themed.theme.accent==state.palette[1][4][6]&&themed.swatches[2]==state.palette[1][2][6]);
        assert(settings_set_scope(&app,1)==-16);                                /* refused while edited */
        state.revision++;assert(settings_apply(&app)==-11&&app.edited&&strstr(app.message,"Changed elsewhere"));
        assert(!settings_reset(&app)&&!app.edited&&!guarded&&app.mode==0);
        assert(!settings_preview(&app,1,4,1,60)&&settings_apply(&app)==0&&!app.edited&&!guarded);
        assert(state.mode==1&&state.accent==4&&material_saved==1&&material_level==60);
        assert(!settings_at_defaults(&app));
        assert(!settings_defaults(&app)&&app.edited&&app.mode==0&&app.accent==0&&app.effects==0&&app.translucency==40);
        assert(settings_at_defaults(&app));settings_reset(&app);
        state.flags=1;settings_refresh(&app);
        assert(settings_preview(&app,0,0,0,40)==-13&&!app.edited&&app.mode==1);  /* read only */
    }
    puts("PASS Settings save failure, stale edits, explicit reload/discard, chrome intent and readonly controls");
    return 0;
}
