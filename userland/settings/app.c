#include "app.h"
static int valid(const struct zenith_appearance *s){
    if(s->version!=1||s->flags&~15U||!s->revision||s->mode>1||s->active_mode>1||s->accent>5||s->active_accent>5||s->reserved[0]||s->reserved[1])return 0;
    for(int m=0;m<2;m++)for(int c=0;c<6;c++)for(int i=0;i<8;i++)if(s->palette[m][c][i]>0xffffff)return 0;
    return 1;
}
static void palette(struct settings_app *a){
    if(a->have_state){
        const unsigned int *p=a->state.palette[a->mode][a->accent];
        for(int i=0;i<6;i++)a->ui.swatches[i]=a->state.palette[a->mode][i][6];
        a->ui.theme=(struct zui_theme){p[0],p[1],p[2],p[3],p[4],p[5],p[6],p[7]};
    }
    a->ui.full_background=(a->effects&5)==5;
    a->ui.dirty=1;
}
void settings_refresh(struct settings_app *a){
    struct zenith_appearance s={0};unsigned int saved=0,effective=0,available=0,reason=0,level=40;long result=a->backend.read(&s,&saved,&effective,&available,&reason,&level);
    if(result||level>100||!valid(&s)||saved>7||effective>7||available>7||reason>5){a->message="Settings service unavailable. Edits have been kept.";a->ui.dirty=1;return;}
    if(a->backend.scope){long info=a->backend.scope(3,0);if(info>=0){a->scope=info&1;a->inherited=!!(info&2);a->has_scopes=!!(info&4);}}
    int changed=effective!=a->effective_effects||available!=a->available_effects||reason!=a->material_reason||!a->have_state||s.revision!=a->state.revision||s.active_revision!=a->state.active_revision||s.resources!=a->state.resources||s.flags!=a->state.flags;
    if(!a->edited){a->translucency=level;a->effects=saved;a->mode=s.mode;a->accent=s.accent;a->base_revision=s.revision;}
    else if(s.revision!=a->base_revision)a->message="Changed elsewhere. Reload before saving; your edits are kept.";
    a->saved_translucency=level;a->saved_effects=saved;a->effective_effects=effective;a->available_effects=available;a->material_reason=reason;a->state=s;a->have_state=1;if(changed)palette(a);
}
int settings_init(struct settings_app *a,unsigned int *pixels,struct settings_backend backend){
    *a=(struct settings_app){0};a->backend=backend;
    if(!backend.read||!backend.save||!backend.dirty||!zui_init(&a->ui,pixels,600,480))return 0;
    settings_refresh(a);
    if(!a->have_state)return 0;
    return 1;
}
int settings_preview(struct settings_app *a,unsigned int mode,unsigned int accent,unsigned int effects,unsigned int level){
    if(!a->have_state||!(a->state.flags&Z_SETTINGS_WRITABLE))return -13;
    if(mode>1||accent>5||effects>7||level>100)return -22;
    if((effects^a->effects)&~a->available_effects)return -95;
    if((level!=a->translucency)&&!a->available_effects)return -95;
    int dirty=level!=a->saved_translucency||effects!=a->saved_effects||mode!=a->state.mode||accent!=a->state.accent;
    if(a->backend.dirty(dirty)){a->message="Cannot protect unsaved changes. Selection was not changed.";a->ui.dirty=1;return -5;}
    a->translucency=level;a->mode=mode;a->accent=accent;a->effects=effects;a->edited=dirty;a->message=0;palette(a);return 0;
}
long settings_apply(struct settings_app *a){
    long result=a->backend.save(a->mode,a->accent,a->effects,a->translucency,a->base_revision);
    if(!result){a->edited=0;a->backend.dirty(0);a->message="Appearance saved.";settings_refresh(a);}
    else a->message=result==-11?"Changed elsewhere. Reload before saving; your edits are kept.":result==-13?"Administrator permission required. Edits have been kept.":"Save failed. Check writable storage; edits have been kept.";
    a->ui.dirty=1;return result;
}
int settings_reset(struct settings_app *a){
    if(a->backend.dirty(0)){a->message="Cannot update window state. Edits have been kept.";a->ui.dirty=1;return -5;}
    a->edited=0;a->message=0;settings_refresh(a);palette(a);return 0;
}
int settings_defaults(struct settings_app *a){
    if(a->has_scopes&&a->scope){
        if(a->edited&&settings_reset(a))return -5;
        if(!a->backend.scope||a->backend.scope(2,a->base_revision)){a->message="Cannot restore defaults; edits kept.";a->ui.dirty=1;return -5;}
        a->message=0;settings_refresh(a);palette(a);return 0;
    }
    return settings_preview(a,0,0,0,40);
}
int settings_at_defaults(const struct settings_app *a){
    if(a->has_scopes&&a->scope)return (int)a->inherited&&!a->edited;
    return a->mode==0&&a->accent==0&&a->effects==0&&a->translucency==40;
}
int settings_set_scope(struct settings_app *a,unsigned int personal){
    if(a->edited||!a->backend.scope){a->message="Save or discard edits before changing scope.";a->ui.dirty=1;return -16;}
    if(a->backend.scope(personal?1:0,0)){a->message="Scope unavailable; previous settings kept.";a->ui.dirty=1;return -19;}
    a->message=0;settings_refresh(a);palette(a);return 0;
}
void settings_theme(const struct settings_app *a,struct zui *u){
    if(a->have_state){
        const unsigned int *p=a->state.palette[a->mode][a->accent];
        for(int i=0;i<6;i++)u->swatches[i]=a->state.palette[a->mode][i][6];
        u->theme=(struct zui_theme){p[0],p[1],p[2],p[3],p[4],p[5],p[6],p[7]};
    }
    u->full_background=(a->effects&5)==5;u->dirty=1;
}
