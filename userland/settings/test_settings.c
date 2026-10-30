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
int main(void){
    struct zui ui;assert(zui_init(&ui,pixels,600,480));
    for(int m=0;m<2;m++)for(int a=0;a<6;a++){
        zui_appearance(&ui,m,a);memcpy(state.palette[m][a],&ui.theme,sizeof(ui.theme));
    }
    state.version=1;state.flags=3;state.revision=1;state.active_revision=1;
    struct settings_backend backend={read_state,save_state,dirty_state,0};
    state.palette[0][3][6]=0x987654;
    assert(settings_init(&app,pixels,backend));assert(zui_accent_color(&app.ui,3)==0x987654); /* package-provided swatches */
    struct zui original;assert(zui_init(&original,pixels,600,480));zui_appearance(&original,0,3);
    state.palette[0][3][6]=original.theme.accent;
    failure=-5;assert(!settings_preview(&app,1,0,0,40));assert(settings_apply(&app)==-5&&app.edited&&guarded&&strstr(app.message,"Save failed"));
    failure=0;assert(!settings_reset(&app)&&!guarded);
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
    puts("PASS Settings model: drafts, save failure, stale edits, reset, defaults, scope refusal and read-only");
    return 0;
}
